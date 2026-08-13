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

    private readonly object? _gate;

    private List<ValidationError>? _errors;

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
    public bool HasErrors => _errors is { Count: > 0 };

    /// <summary>
    /// How many failures this pass has recorded.
    /// </summary>
    public int Count => _errors?.Count ?? 0;

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
    public ValidationResult ToResult() =>
        _errors is null || _errors.Count == 0
            ? ValidationResult.Valid
            : ValidationResult.FromErrors(_errors);

    /// <summary>
    /// Clears the errors, keeping the buffer for the next pass.
    /// </summary>
    /// <remarks>
    /// <see cref="List{T}.Clear"/> nulls the vacated slots, so a pooled collector does not keep the
    /// last pass's messages and paths alive.
    /// </remarks>
    public void Reset() {
        _errors?.Clear();
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

        (_errors ??= []).Add(error);

        // Only a real failure suppresses. A Required reported as a warning is advisory, and
        // silencing the rest of the field on the strength of it would be wrong.
        if (error.Severity == ValidationSeverity.Error &&
            string.Equals(error.Code, ValidationCodes.Required, StringComparison.Ordinal)) {
            (_requiredFields ??= []).Add(error.Field);
        }
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
