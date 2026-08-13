using System.Text;

namespace ValidationModules;

/// <summary>
/// Accumulates the errors of one validation pass, and owns the path log the
/// <see cref="ValidationContext"/> cursors point into.
/// </summary>
/// <remarks>
/// <para>
/// Public because pooling it is the point: a request pipeline validating a body per request should
/// reuse one collector rather than allocate a fresh path buffer each time. Call
/// <see cref="Reset"/> between passes.
/// </para>
/// <para>
/// It also owns one semantic rule rather than only storage: a field that has failed
/// <see cref="ValidationCodes.Required"/> accepts no further errors for the rest of the pass. That
/// lives here so every engine gets it, including ones that map errors from elsewhere and have no
/// control flow to express it with. See <c>AddCore</c>.
/// </para>
/// <para>
/// Not thread-safe by default. Use <see cref="CreateSynchronized"/> when concurrent branches add
/// errors in parallel; the default keeps the lock off the path that runs per nested object, which
/// is correct for every generated validator and for any async validator that awaits sequentially.
/// </para>
/// </remarks>
public sealed class ValidationErrorCollector {

    /// <summary>The node index meaning "the root of the path".</summary>
    internal const int RootNode = -1;

    /// <summary>The index value meaning "this segment is not a collection element".</summary>
    internal const int NoIndex = -1;

    /// <summary>
    /// A path segment. Immutable once written, which is what makes a stored
    /// <see cref="ValidationContext"/> safe - see that type's remarks.
    /// </summary>
    private struct PathNode {
        public int Parent;
        public string Name;
        public int Index;

        /// <summary>Set for a dictionary entry, where the path segment is a key rather than a position.</summary>
        public string? Key;
    }

    /// <summary>
    /// How deep validation may nest before it is treated as a cycle.
    /// </summary>
    /// <remarks>
    /// A self-referential type is legitimate and validates fine over a tree. An object graph with an
    /// actual cycle - a.Child = b, b.Child = a - would recurse until the stack ran out, and a
    /// StackOverflowException cannot be caught and takes the process with it. Failing here instead
    /// turns that into an ordinary, diagnosable exception. 64 is far past any hand-written model and
    /// far short of the stack.
    /// </remarks>
    public const int MaxDepth = 64;

    private readonly object? _gate;

    private PathNode[] _nodes = new PathNode[16];
    private int _nodeCount;
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
    /// Clears the errors and the path log, keeping both buffers for the next pass.
    /// </summary>
    public void Reset() {
        _errors?.Clear();
        _requiredFields?.Clear();

        // Clearing rather than only resetting the count: the nodes hold string references, and a
        // pooled collector would otherwise keep the last pass's segment names alive indefinitely.
        Array.Clear(_nodes, 0, _nodeCount);
        _nodeCount = 0;
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

    internal int AddNode(int parent, string name, int index) {
        if (_gate is null) {
            return AddNodeCore(parent, name, index, null);
        }

        lock (_gate) {
            return AddNodeCore(parent, name, index, null);
        }
    }

    internal int AddKeyedNode(int parent, string name, string key) {
        if (_gate is null) {
            return AddNodeCore(parent, name, NoIndex, key);
        }

        lock (_gate) {
            return AddNodeCore(parent, name, NoIndex, key);
        }
    }

    internal void Emit(int node, string? field, string code, string message, ValidationSeverity severity) {
        if (_gate is null) {
            AddCore(new ValidationError(BuildPath(node, field), code, message) { Severity = severity });
            return;
        }

        lock (_gate) {
            AddCore(new ValidationError(BuildPath(node, field), code, message) { Severity = severity });
        }
    }

    private int AddNodeCore(int parent, string name, int index, string? key) {
        var depth = 1;
        for (var n = parent; n != RootNode; n = _nodes[n].Parent) {
            if (++depth > MaxDepth) {
                throw new InvalidOperationException(
                    $"Validation nested more than {MaxDepth} levels deep at '{BuildPath(parent, name)}'. " +
                    "This normally means the object graph contains a cycle.");
            }
        }

        if (_nodeCount == _nodes.Length) {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        _nodes[_nodeCount] = new PathNode { Parent = parent, Name = name, Index = index, Key = key };

        return _nodeCount++;
    }

    /// <summary>
    /// Materializes a path by walking the node's parent chain. Only ever reached when an error is
    /// actually being added, which is what keeps a clean pass at zero allocations.
    /// </summary>
    private string BuildPath(int node, string? leaf) {
        if (node == RootNode) {
            return leaf ?? string.Empty;
        }

        var depth = 0;
        for (var n = node; n != RootNode; n = _nodes[n].Parent) {
            depth++;
        }

        // The chain runs leaf-to-root; fill the buffer backwards rather than reversing after.
        var segments = new PathNode[depth];
        var write = depth;
        for (var n = node; n != RootNode; n = _nodes[n].Parent) {
            segments[--write] = _nodes[n];
        }

        var builder = new StringBuilder();
        for (var i = 0; i < depth; i++) {
            if (i > 0) {
                builder.Append('.');
            }

            builder.Append(segments[i].Name);

            if (segments[i].Key is { } key) {
                builder.Append('[').Append(key).Append(']');
            } else if (segments[i].Index != NoIndex) {
                builder.Append('[').Append(segments[i].Index).Append(']');
            }
        }

        if (leaf is not null) {
            if (builder.Length > 0) {
                builder.Append('.');
            }

            builder.Append(leaf);
        }

        return builder.ToString();
    }
}
