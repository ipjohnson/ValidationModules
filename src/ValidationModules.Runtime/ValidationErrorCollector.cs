namespace ValidationModules;

/// <summary>
/// Accumulates the errors of one validation pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Construct one per validation.</b> It was made public so a request pipeline could pool one and
/// skip a 472-byte allocation per request; that buffer is gone and a fresh collector is 48 bytes
/// holding nothing, so pooling now saves 48 bytes and costs a node per error on every failing pass -
/// measured 2026-08-13, HANDOFF.md §2.6. The first consumer runs on Lambda, where holding state
/// across invocations for a 48-byte saving is not a trade worth the complexity.
/// </para>
/// <para>
/// Reuse is still supported: <see cref="Reset"/> between passes, and one collector can gather
/// several validations into a single result. It is just no longer the recommended shape.
/// </para>
/// <para>
/// It owns no part of the path. <see cref="ValidationContext"/> carries its own path in the struct
/// and arrives here with it already rendered, which is why descending needs neither this object's
/// storage nor its lock.
/// </para>
/// <para>
/// It does own one semantic rule rather than only storage: a field that has failed
/// <see cref="ValidationCodes.Required"/> accepts no further errors for the rest of the pass. That
/// lives here so every engine gets it, including ones that map errors from elsewhere and have no
/// control flow to express it with. See <c>AddCore</c>.
/// </para>
/// <para>
/// Not thread-safe by default. Use <see cref="CreateSynchronized"/> when concurrent branches add
/// errors in parallel. The lock now guards only <see cref="Add"/>, so a clean pass never touches it
/// and descending no longer contends for it whether it is there or not.
/// </para>
/// </remarks>
public sealed class ValidationErrorCollector {

    /// <summary>
    /// How deep validation may nest before it is treated as a cycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A self-referential type is legitimate and validates fine over a tree. An object graph with an
    /// actual cycle - a.Child = b, b.Child = a - would recurse until the stack ran out, and a
    /// StackOverflowException cannot be caught and takes the process with it. Failing here instead
    /// turns that into an ordinary, diagnosable exception. 64 is far past any hand-written model and
    /// far short of the stack.
    /// </para>
    /// <para>
    /// Enforced by <see cref="ValidationContext"/>, which carries its own depth and so compares
    /// rather than walking a chain to the root on every descent. It stays declared here because it
    /// is a property of a validation pass rather than of one cursor into it.
    /// </para>
    /// </remarks>
    public const int MaxDepth = 64;

    /// <summary>
    /// One recorded failure, and the link to the one recorded before it. A class rather than an
    /// array slot so that adding never resizes and never copies what is already there.
    /// </summary>
    private sealed class ErrorNode {
        public ValidationError Error;
        public ErrorNode? Next;
    }

    private readonly object? _gate;

    /// <summary>
    /// The most recent failure. The chain runs newest to oldest, which is what lets this be the only
    /// field the storage needs.
    /// </summary>
    /// <remarks>
    /// Appending in order would want a tail pointer, and sizing the result array would want a count,
    /// and those two fields push the object from 48 to 72 bytes - paid on every clean pass, which is
    /// most of production traffic, to speed up passes that fail. Inserting at the head and having
    /// <see cref="ToResult"/> fill its array backwards keeps declaration order without either one.
    /// </remarks>
    private ErrorNode? _head;

    /// <summary>
    /// Field paths that have already failed <see cref="ValidationCodes.Required"/> at error
    /// severity. Allocated only once something is actually missing, and short in every realistic
    /// pass, so the membership test below stays a linear scan rather than a set.
    /// </summary>
    private List<string>? _requiredFields;

    /// <summary>
    /// Creates an unsynchronized collector for the default profile.
    /// </summary>
    public ValidationErrorCollector() { }

    /// <summary>
    /// Creates an unsynchronized collector for a specific profile.
    /// </summary>
    /// <param name="profile">The profile this pass runs under, or <see langword="null"/> for the default.</param>
    public ValidationErrorCollector(Type? profile) {
        Profile = profile;
    }

    private ValidationErrorCollector(Type? profile, object gate) {
        Profile = profile;
        _gate = gate;
    }

    /// <summary>
    /// Creates a collector that tolerates concurrent adds, for async validators that genuinely fan
    /// out - <c>Task.WhenAll</c> over collection elements, say. Descending needs no synchronization
    /// either way; <see cref="ValidationContext"/> carries its own path and never writes here.
    /// </summary>
    /// <remarks>
    /// The default collector does not synchronize because generated straight-line code never needs
    /// it and the lock would sit on the hot path. Opt in here rather than paying for it everywhere.
    /// </remarks>
    /// <param name="profile">The profile this pass runs under, or <see langword="null"/> for the default.</param>
    public static ValidationErrorCollector CreateSynchronized(Type? profile = null) => new(profile, new object());

    /// <summary>
    /// Whether this pass has recorded any failure, at any severity.
    /// </summary>
    public bool HasErrors => _head is not null;

    /// <summary>
    /// How many failures this pass has recorded.
    /// </summary>
    /// <remarks>
    /// Walks the chain, so this is linear rather than a field read. A pass holds one node per
    /// failure and realistic passes hold none or one, which is why the two fields a cached count
    /// would cost are not worth charging to every clean pass. Snapshotting it around a block, which
    /// is what this is for, stays cheap for the same reason.
    /// </remarks>
    public int Count {
        get {
            var count = 0;
            for (var node = _head; node is not null; node = node.Next) {
                count++;
            }

            return count;
        }
    }

    /// <summary>
    /// The profile this pass runs under, or <see langword="null"/> for the default profile.
    /// </summary>
    public Type? Profile { get; }

    /// <summary>
    /// Adds an error whose field path is already resolved. Used by adapters that receive a flat
    /// field name from another engine rather than walking a path.
    /// </summary>
    public void Add(in ValidationError error) {
        if (_gate is null) {
            AddCore(in error);
            return;
        }

        lock (_gate) {
            AddCore(in error);
        }
    }

    /// <summary>
    /// Snapshots what has been collected into an immutable result. Returns the shared
    /// <see cref="ValidationResult.Valid"/> instance when nothing failed.
    /// </summary>
    /// <remarks>
    /// Counts, then fills one exactly-sized array from the back, because the chain runs newest to
    /// oldest and <see cref="ValidationResult.Errors"/> is declaration order. Two walks over a
    /// handful of nodes, against a list that would have grown its backing array underneath.
    /// </remarks>
    public ValidationResult ToResult() {
        if (_head is null) {
            return ValidationResult.Valid;
        }

        var errors = new ValidationError[Count];
        var i = errors.Length;
        for (var node = _head; node is not null; node = node.Next) {
            errors[--i] = node.Error;
        }

        return ValidationResult.FromOwnedArray(errors);
    }

    /// <summary>
    /// Drops the errors, so the collector can run another pass.
    /// </summary>
    /// <remarks>
    /// The nodes are released rather than retained. Holding them back for reuse costs a field on
    /// every collector, clean ones included, to save allocating a node on the passes that fail - and
    /// a fresh collector is cheap enough that constructing one per validation is the simpler
    /// default. Reuse is still supported; it just no longer recycles.
    /// </remarks>
    public void Reset() {
        _head = null;
        _requiredFields?.Clear();
    }

    /// <summary>
    /// The single choke point every error passes through, and therefore where the suppression rule
    /// lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why here rather than in the emitter.</b> Generated code expresses suppression as an
    /// <c>else if</c> chain, which works only for engines that generate code. The FluentValidation
    /// adapter maps <c>ValidationFailure</c>s that FluentValidation has already produced - it has no
    /// control flow to put an <c>else</c> in - so if suppression were only a shape in emitted
    /// source, the adapter could never honour it and §8's conformance suite would have to exclude
    /// it. Enforcing it here makes it a property of the error model, which every engine reaches
    /// through, and the <c>else if</c> in generated code becomes an optimization rather than the
    /// mechanism.
    /// </para>
    /// <para>
    /// <b>Forward-only, and exact-match.</b> A field is suppressed from the moment it fails
    /// Required; errors already recorded are not removed retroactively, because a result that
    /// changes its contents based on a later unrelated add is worse to reason about than an
    /// occasional duplicate. That makes the rule depend on Required being evaluated first, which
    /// §4.2 requires of every engine. Matching is on the whole path, so <c>home.postalCode</c> and
    /// <c>work.postalCode</c> are different fields, and it is not a prefix match: a failed Required
    /// on <c>home</c> does not suppress <c>home.postalCode</c>. Nothing recurses into a value that
    /// failed Required in the first place, so there is nothing there to suppress.
    /// </para>
    /// </remarks>
    private void AddCore(in ValidationError error) {
        if (_requiredFields is { Count: > 0 } && IsSuppressed(error.Field)) {
            return;
        }

        Record(in error);

        // Only a real failure suppresses. A Required reported as a warning is advisory, and
        // silencing the rest of the field on the strength of it would be wrong.
        if (error.Severity == ValidationSeverity.Error &&
            string.Equals(error.Code, ValidationCodes.Required, StringComparison.Ordinal)) {
            (_requiredFields ??= []).Add(error.Field);
        }
    }

    /// <summary>
    /// Links the failure in at the head. Storage order is the reverse of declaration order;
    /// <see cref="ToResult"/> is the only thing that reads the chain and it unwinds it.
    /// </summary>
    private void Record(in ValidationError error) =>
        _head = new ErrorNode { Error = error, Next = _head };

    private bool IsSuppressed(string field) {
        var fields = _requiredFields!;

        for (var i = 0; i < fields.Count; i++) {
            if (string.Equals(fields[i], field, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
}
