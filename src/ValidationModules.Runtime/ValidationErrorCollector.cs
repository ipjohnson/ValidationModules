namespace ValidationModules;

/// <summary>
/// Accumulates the errors of one validation pass.
/// </summary>
/// <remarks>
/// <para>
/// Public because pooling it is the point: a request pipeline validating a body per request should
/// reuse one collector rather than allocate a fresh error list each time. Call <see cref="Reset"/>
/// between passes.
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
    /// One recorded failure, and the link to the next. A class rather than an array slot so that
    /// adding never resizes and never copies what is already there.
    /// </summary>
    private sealed class ErrorNode {
        public ValidationError Error;
        public ErrorNode? Next;
    }

    private readonly object? _gate;

    private ErrorNode? _head;
    private ErrorNode? _tail;
    private int _count;

    /// <summary>
    /// Nodes released by <see cref="Reset"/>, ready to be handed straight back out. This is what
    /// makes a pooled collector allocation-free once it has seen its worst pass: the second request
    /// to record eight failures allocates nothing at all.
    /// </summary>
    private ErrorNode? _free;

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
    /// Creates a collector that tolerates concurrent pushes and adds, for async validators that
    /// genuinely fan out - <c>Task.WhenAll</c> over collection elements, say.
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
    public int Count => _count;

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
    /// The count is known before the walk, so this fills one exactly-sized array rather than
    /// growing a list into it.
    /// </remarks>
    public ValidationResult ToResult() {
        if (_head is null) {
            return ValidationResult.Valid;
        }

        var errors = new ValidationError[_count];
        var i = 0;
        for (var node = _head; node is not null; node = node.Next) {
            errors[i++] = node.Error;
        }

        return ValidationResult.FromOwnedArray(errors);
    }

    /// <summary>
    /// Clears the errors, keeping the nodes for the next pass.
    /// </summary>
    /// <remarks>
    /// The recorded chain is spliced onto the free list whole, so this is constant time however many
    /// failures the pass produced. A released node keeps its strings reachable until it is handed
    /// out again and overwritten - bounded by the worst pass this collector has seen, and the reason
    /// to pool one per pipeline rather than one per process.
    /// </remarks>
    public void Reset() {
        if (_tail is not null) {
            _tail.Next = _free;
            _free = _head;
            _head = null;
            _tail = null;
            _count = 0;
        }

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

        Append(in error);

        // Only a real failure suppresses. A Required reported as a warning is advisory, and
        // silencing the rest of the field on the strength of it would be wrong.
        if (error.Severity == ValidationSeverity.Error &&
            string.Equals(error.Code, ValidationCodes.Required, StringComparison.Ordinal)) {
            (_requiredFields ??= []).Add(error.Field);
        }
    }

    /// <summary>
    /// Appends at the tail, so errors come out in the order they were recorded - which §4.2 requires
    /// and which a head-insert would reverse.
    /// </summary>
    private void Append(in ValidationError error) {
        ErrorNode node;

        if (_free is not null) {
            node = _free;
            _free = node.Next;
        } else {
            node = new ErrorNode();
        }

        node.Error = error;
        node.Next = null;

        if (_tail is null) {
            _head = node;
        } else {
            _tail.Next = node;
        }

        _tail = node;
        _count++;
    }

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
