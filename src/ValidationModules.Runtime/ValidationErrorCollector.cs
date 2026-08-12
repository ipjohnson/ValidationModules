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
    }

    private readonly object? _gate;

    private PathNode[] _nodes = new PathNode[16];
    private int _nodeCount;
    private List<ValidationError>? _errors;

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

        // Clearing rather than only resetting the count: the nodes hold string references, and a
        // pooled collector would otherwise keep the last pass's segment names alive indefinitely.
        Array.Clear(_nodes, 0, _nodeCount);
        _nodeCount = 0;
    }

    private void AddCore(in ValidationError error) => (_errors ??= []).Add(error);

    internal int AddNode(int parent, string name, int index) {
        if (_gate is null) {
            return AddNodeCore(parent, name, index);
        }

        lock (_gate) {
            return AddNodeCore(parent, name, index);
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

    private int AddNodeCore(int parent, string name, int index) {
        if (_nodeCount == _nodes.Length) {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        _nodes[_nodeCount] = new PathNode { Parent = parent, Name = name, Index = index };

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

            if (segments[i].Index != NoIndex) {
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
