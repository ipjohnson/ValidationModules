using System.Text;

namespace ValidationModules.Benchmarks.Design;

/// <summary>
/// The third shape: today's log storage, with the two changes the chain prototype suggested but
/// without replacing the array with individual heap nodes.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the first comparison was not isolating what it claimed. The shipped
/// collector allocates its 16-entry buffer in a field initializer, so a fresh one costs 472 bytes
/// whether or not anything nests, and the chain prototype's collector has no buffer at all - so
/// most of the "93% less allocation" on a flat model was the eager buffer rather than the shape.
/// This arm has the log's storage and the chain's laziness, which separates the two.
/// </para>
/// <para>
/// Two changes from the shipped collector, both of them small:
/// </para>
/// <list type="number">
/// <item>The node buffer is allocated on first use, so a flat model never pays for one.</item>
/// <item>A push does not append. The context carries its own innermost segment inline, exactly as
/// <see cref="ChainContext"/> does, and appends only when a child pushes past it - so a leaf push
/// writes nothing at all.</item>
/// </list>
/// <para>
/// If this matches the chain on flat models and leaf collections while keeping the log's zero
/// allocation on nested elements under a pooled collector, it is the shape to take: it is a small
/// delta from shipped code rather than a new context type.
/// </para>
/// </remarks>
public sealed class LazyPinCollector {
    internal const int RootNode = -1;
    internal const int NoIndex = -1;
    public const int MaxDepth = 64;

    private struct PathNode {
        public int Parent;
        public string Name;
        public int Index;
        public string? Key;
    }

    private PathNode[]? _nodes;
    private int _nodeCount;
    private List<ValidationError>? _errors;
    private List<string>? _requiredFields;

    public bool HasErrors => _errors is { Count: > 0 };

    public int Count => _errors?.Count ?? 0;

    /// <summary>
    /// Clears the errors and the node log, keeping both buffers. The node buffer may not exist at
    /// all, which is the point.
    /// </summary>
    public void Reset() {
        _errors?.Clear();
        _requiredFields?.Clear();

        if (_nodes is not null) {
            Array.Clear(_nodes, 0, _nodeCount);
        }

        _nodeCount = 0;
    }

    public ValidationResult ToResult() =>
        _errors is null || _errors.Count == 0
            ? ValidationResult.Valid
            : ValidationResult.FromErrors(_errors);

    internal void Add(in ValidationError error) {
        if (_requiredFields is { Count: > 0 } && IsSuppressed(error.Field)) {
            return;
        }

        (_errors ??= []).Add(error);

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

    /// <summary>
    /// Appends a segment. Reached only from a context that is being pushed past, so a pass over
    /// leaves never calls it and never causes the buffer to exist.
    /// </summary>
    internal int Append(int parent, string name, int index, string? key) {
        // Four rather than sixteen: most models nest one or two levels, and doubling covers the
        // rest. The shipped sixteen was sized for a log that appended on every push.
        _nodes ??= new PathNode[4];

        if (_nodeCount == _nodes.Length) {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        _nodes[_nodeCount] = new PathNode { Parent = parent, Name = name, Index = index, Key = key };

        return _nodeCount++;
    }

    /// <summary>
    /// Materializes a path from the materialized ancestors plus the caller's own unpinned segment.
    /// Only ever reached when an error is actually being added.
    /// </summary>
    internal string BuildPath(int parent, string? segment, int index, string? key, string? leaf) {
        if (parent == RootNode && segment is null) {
            return leaf ?? string.Empty;
        }

        var builder = new StringBuilder();

        AppendAncestors(builder, parent);

        if (segment is not null) {
            AppendSegment(builder, segment, index, key);
        }

        if (leaf is not null) {
            if (builder.Length > 0) {
                builder.Append('.');
            }

            builder.Append(leaf);
        }

        return builder.ToString();
    }

    private void AppendAncestors(StringBuilder builder, int node) {
        if (node == RootNode) {
            return;
        }

        var nodes = _nodes!;

        AppendAncestors(builder, nodes[node].Parent);
        AppendSegment(builder, nodes[node].Name, nodes[node].Index, nodes[node].Key);
    }

    private static void AppendSegment(StringBuilder builder, string name, int index, string? key) {
        if (builder.Length > 0) {
            builder.Append('.');
        }

        builder.Append(name);

        if (key is not null) {
            builder.Append('[').Append(key).Append(']');
        } else if (index != NoIndex) {
            builder.Append('[').Append(index).Append(']');
        }
    }
}

/// <summary>
/// The context for <see cref="LazyPinCollector"/>: an index into the log for the materialized
/// ancestors, plus this level's segment held inline until something needs it pinned.
/// </summary>
public readonly struct LazyPinContext {
    private const int RootNode = LazyPinCollector.RootNode;
    private const int NoIndex = LazyPinCollector.NoIndex;

    private readonly LazyPinCollector _collector;
    private readonly int _parent;
    private readonly string? _segment;
    private readonly int _index;
    private readonly string? _key;
    private readonly int _depth;

    public LazyPinContext(LazyPinCollector collector) {
        ArgumentNullException.ThrowIfNull(collector);

        _collector = collector;
        _parent = RootNode;
        _segment = null;
        _index = NoIndex;
        _key = null;
        _depth = 0;
    }

    private LazyPinContext(
        LazyPinCollector collector, int parent, string? segment, int index, string? key, int depth) {
        _collector = collector;
        _parent = parent;
        _segment = segment;
        _index = index;
        _key = key;
        _depth = depth;
    }

    public LazyPinContext Push(string segment) => Descend(segment, NoIndex, null);

    public LazyPinContext PushIndex(string segment, int index) => Descend(segment, index, null);

    public LazyPinContext PushKey(string segment, string key) => Descend(segment, NoIndex, key);

    public bool HasErrors => _collector.HasErrors;

    public int ErrorCount => _collector.Count;

    public void Add(
        string field,
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        _collector.Add(new ValidationError(
            _collector.BuildPath(_parent, _segment, _index, _key, field), code, message) { Severity = severity });

    public void AddHere(
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        _collector.Add(new ValidationError(
            _collector.BuildPath(_parent, _segment, _index, _key, null), code, message) { Severity = severity });

    private LazyPinContext Descend(string segment, int index, string? key) {
        var depth = _depth + 1;

        // O(1), because depth rides in the struct rather than being counted by walking the chain.
        if (depth > LazyPinCollector.MaxDepth) {
            throw new InvalidOperationException(
                $"Validation nested more than {LazyPinCollector.MaxDepth} levels deep. " +
                "This normally means the object graph contains a cycle.");
        }

        return new LazyPinContext(_collector, Pin(), segment, index, key, depth);
    }

    /// <summary>
    /// Appends this level to the log because a child is about to need something to point at. A leaf
    /// is never pinned, so a pass over a collection of leaves never touches the log.
    /// </summary>
    private int Pin() =>
        _segment is null ? _parent : _collector.Append(_parent, _segment, _index, _key);
}
