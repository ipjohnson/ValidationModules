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
/// <b>A pass is single-threaded.</b> One collector belongs to one validation, and the path buffer
/// its contexts share is a depth-indexed stack, so two branches walking at once would overwrite
/// each other's segments. Validate concurrently by giving each branch its own collector and merging
/// the results - which measured faster than sharing one under a lock in any case.
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
    public const int DefaultDepthLimit = 64;

    /// <summary>
    /// One recorded failure, and the link to the one recorded before it. A class rather than an
    /// array slot so that adding never resizes and never copies what is already there.
    /// </summary>
    private sealed class ErrorNode {
        public ValidationError Error;
        public ErrorNode? Next;
    }

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

    private long _stamp;

    /// <summary>
    /// Field paths that have already failed <see cref="ValidationCodes.Required"/> at error
    /// severity. Allocated only once something is actually missing, and short in every realistic
    /// pass, so the membership test below stays a linear scan rather than a set.
    /// </summary>
    private List<string>? _requiredFields;

    /// <summary>
    /// Creates an unsynchronized collector.
    /// </summary>
    public ValidationErrorCollector() : this(ValidationPathMode.Bounded) { }

    /// <summary>Creates a collector that renders error paths the given way.</summary>
    public ValidationErrorCollector(ValidationPathMode pathMode) : this(null, pathMode) { }

    /// <summary>Creates a collector carrying the services this unit of work should reach.</summary>
    public ValidationErrorCollector(IServiceProvider? services) : this(services, ValidationPathMode.Bounded) { }

    /// <summary>Creates a collector carrying services and a path rendering.</summary>
    public ValidationErrorCollector(IServiceProvider? services, ValidationPathMode pathMode) {
        Services = services;
        PathMode = pathMode;
    }

    /// <summary>
    /// The services this validation pass can reach, or null when it was started without any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than on the validator.</b> Validators are registered singleton - they are
    /// stateless and building the rule graph once is a hard requirement - while
    /// <see cref="ValidationRunner{T}"/> is scoped and creates its collector per call. A provider
    /// injected into a singleton validator would be a captive dependency handing out root-scoped
    /// services forever; on a per-call collector the scope is correct by construction, and under
    /// ASP.NET Core you hold request services without anyone having to think about it.
    /// </para>
    /// <para>
    /// It also avoids a transitive closure over the nesting graph: were the provider a constructor
    /// argument, anything nesting a validator that needed one would need one too, recursively, with
    /// cycles to handle.
    /// </para>
    /// <para>
    /// <b><see cref="Reset"/> keeps it.</b> A collector belongs to one unit of work and carries
    /// that scope's services; reuse within a scope is the point. Constructor-only with no setter is
    /// what encodes the invariant in the type - re-arming a pooled collector for a different scope
    /// is simply not expressible, so crossing a scope means a new collector, which costs 40 bytes.
    /// This is deliberate, not an oversight to be tidied away later.
    /// </para>
    /// </remarks>
    public IServiceProvider? Services { get; }

    /// <summary>
    /// Creates a collector that tolerates concurrent adds, for async validators that genuinely fan
    /// out - <c>Task.WhenAll</c> over collection elements, say. Descending needs no synchronization
    /// either way; <see cref="ValidationContext"/> carries its own path and never writes here.
    /// </summary>
    /// <remarks>
    /// The default collector does not synchronize because generated straight-line code never needs
    /// it and the lock would sit on the hot path. Opt in here rather than paying for it everywhere.
    /// </remarks>
    /// <summary>The path rendering this pass uses. Fixed at construction.</summary>
    public ValidationPathMode PathMode { get; }

    /// <summary>
    /// Whether this pass stops at its first Error-severity failure. Defaults to
    /// <see cref="ValidationStopMode.CollectAll"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than on the validator or the context.</b> Validators are stateless
    /// singletons, so a mode on one would be shared by every caller at once. The context is a
    /// readonly struct copied on every <see cref="ValidationContext.Push(string)"/>, so a field
    /// there would be paid on every descent of every pass. This object already exists per pass and
    /// already owns one semantic rule - Required suppression - so fail-fast being a second rule of
    /// the same kind keeps both in one place.
    /// </para>
    /// <para>
    /// <b>Init-only, and <see cref="Reset"/> keeps it</b>, for the same reason
    /// <see cref="PathMode"/> and <see cref="Services"/> are: it describes the unit of work rather
    /// than what the pass has found so far.
    /// </para>
    /// </remarks>
    public ValidationStopMode StopMode { get; init; }

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
    /// An O(1) token that changes whenever an error is recorded. Compared by reference around a
    /// block to answer "did that add anything", which <see cref="Count"/> would answer in linear
    /// time - and a rule chain asks it once per field, so linear would compound.
    /// </summary>
    internal object? ChangeToken => _head;

    /// <summary>
    /// Whether anything Error-severity was recorded. This, and not <see cref="HasErrors"/>, is what
    /// "valid" means - a warning is surfaced but the value is accepted. Stops at the first one, and
    /// on a clean pass never leaves the null check.
    /// </summary>
    internal bool HasBlockingErrors {
        get {
            for (var node = _head; node is not null; node = node.Next) {
                if (node.Error.Severity == ValidationSeverity.Error) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The next path-write stamp. Monotonic for the life of the collector, so a context can tell
    /// whether the segments it walked are still the ones in the buffer.
    /// </summary>
    internal long NextStamp() => ++_stamp;

    /// <summary>
    /// Records an error without consulting the Required suppression rule. Used by
    /// <see cref="ValidationContext"/>, whose engines short-circuit a failed Required per field
    /// themselves - the emitter with an <c>else if</c>, the rule builder with a field chain - so a
    /// second, path-keyed rule here would only ever fire when two positions rendered alike.
    /// </summary>
    internal ValidationFlow AddDirect(in ValidationError error) {
        if (Finished) {
            return ValidationFlow.Stop;
        }

        Record(in error);

        return Flow(in error);
    }

    /// <summary>
    /// Adds an error whose field path is already resolved. Used by adapters that receive a flat
    /// field name from another engine rather than walking a path.
    /// </summary>
    public ValidationFlow Add(in ValidationError error) {
        if (Finished) {
            return ValidationFlow.Stop;
        }

        AddCore(in error);

        return Flow(in error);
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
    /// Whether this pass has already answered what it was asked for and is closed to further
    /// errors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever true under <see cref="ValidationStopMode.StopOnFirstError"/>, where it makes the
    /// result independent of whether the validator running it can stop. A generated validator with
    /// <c>ValidationModules_FailFast</c> turned off, a hand-written rule that discards its
    /// <see cref="ValidationFlow"/>, an <see cref="IAsyncValidatorFor{T}"/> - all of them keep
    /// going, and without this each would report a different number of errors for the same request.
    /// The mode promises one error; this is what makes that true of every engine rather than of the
    /// well-behaved ones.
    /// </para>
    /// <para>
    /// It does not make skipping the work unnecessary. Dropping an error still evaluated the rule
    /// that produced it, which is the difference the emitted return exists to remove.
    /// </para>
    /// <para>
    /// The walk is bounded: once one blocking error is recorded nothing further is, so the chain it
    /// scans is at most that one error and whatever warnings preceded it.
    /// </para>
    /// </remarks>
    private bool Finished => StopMode == ValidationStopMode.StopOnFirstError && HasBlockingErrors;

    /// <summary>
    /// Whether the pass that just recorded <paramref name="error"/> should stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads the severity of the error in hand rather than asking whether the pass has any
    /// blocking error, which would walk the chain on every add. The two agree: a pass in
    /// <see cref="ValidationStopMode.StopOnFirstError"/> returns at the first Error, so there is
    /// never a second one to find.
    /// </para>
    /// <para>
    /// A suppressed error reaches here having recorded nothing, and answers
    /// <see cref="ValidationFlow.Continue"/> on its severity alone. That is unreachable in
    /// practice - suppression needs an earlier failed Required on the same field, which
    /// <see cref="Finished"/> would already have closed the pass on - and continuing is the
    /// harmless direction if it ever is.
    /// </para>
    /// </remarks>
    private ValidationFlow Flow(in ValidationError error) =>
        StopMode == ValidationStopMode.StopOnFirstError &&
        error.Severity == ValidationSeverity.Error
            ? ValidationFlow.Stop
            : ValidationFlow.Continue;

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
