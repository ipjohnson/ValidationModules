using System.Text;

namespace ValidationModules.Benchmarks.Design;

/// <summary>
/// A prototype of the chain-shaped context, for <see cref="ContextShapeBenchmarks"/> to measure
/// against the shipped one. Nothing here is used by the library.
/// </summary>
/// <remarks>
/// <para>
/// <b>The idea.</b> The shipped design keeps path segments in an append-only log on the collector
/// and has each context hold an index into it. This one puts the path in the contexts themselves:
/// a context holds its parent chain, its own segment, and its depth.
/// </para>
/// <para>
/// <b>Why it is not simply a <c>ref struct</c>.</b> A struct cannot hold a struct of its own type,
/// so "context points at parent" needs the parent either on the heap - a class, which makes every
/// push allocate - or behind a <c>ref</c>, which means <c>ref struct</c> and is exactly what
/// API-SURFACE §13.1 ruled out: CS4012 forbids one as an <c>async</c> parameter and CS9202 as an
/// async local.
/// </para>
/// <para>
/// <b>The way out is to pin lazily.</b> A context carries its own innermost segment inline and only
/// materializes a <see cref="ChainPathNode"/> when something pushes <i>past</i> it. A push whose
/// context is never pushed past therefore allocates nothing, which covers the two cases that
/// dominate: a single level of object nesting, and a collection whose elements are leaves.
/// </para>
/// </remarks>
internal sealed class ChainPathNode {
    public readonly ChainPathNode? Parent;
    public readonly string Name;
    public readonly int Index;
    public readonly string? Key;

    public ChainPathNode(ChainPathNode? parent, string name, int index, string? key) {
        Parent = parent;
        Name = name;
        Index = index;
        Key = key;
    }
}

/// <summary>
/// The prototype's collector. Errors and the suppression rule only - it owns no path storage,
/// which is the whole point of the shape.
/// </summary>
public sealed class ChainErrorCollector {
    internal const int NoIndex = -1;

    private List<ValidationError>? _errors;
    private List<string>? _requiredFields;

    public bool HasErrors => _errors is { Count: > 0 };

    public int Count => _errors?.Count ?? 0;

    /// <summary>
    /// Clears the errors. There is no path log to clear, so a pooled collector of this shape has
    /// nothing to reset when the pass found nothing.
    /// </summary>
    public void Reset() {
        _errors?.Clear();
        _requiredFields?.Clear();
    }

    public ValidationResult ToResult() =>
        _errors is null || _errors.Count == 0
            ? ValidationResult.Valid
            : ValidationResult.FromErrors(_errors);

    /// <summary>
    /// The same choke point and the same forward-only, exact-match suppression rule as the shipped
    /// collector, so the two shapes produce identical error sets.
    /// </summary>
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
}

/// <summary>
/// The prototype context: four words, holding its own parent chain rather than an index into a
/// shared log.
/// </summary>
/// <remarks>
/// <para>
/// Two consequences beyond allocation, both of which the benchmarks pick up:
/// </para>
/// <para>
/// <b>Sibling collision is unrepresentable.</b> The shipped log is append-only rather than a
/// depth-indexed stack precisely because siblings at one depth would otherwise overwrite each
/// other's segment when one parks on an await. Here two siblings are two different struct values,
/// so there is no shared cell to overwrite and the hazard cannot occur at all.
/// </para>
/// <para>
/// <b>The depth guard is O(1).</b> Depth rides in the struct, so the cycle check is a comparison
/// rather than a walk up the parent chain on every push. Note that this one is separable: the
/// shipped log could carry a depth per node and get the same result without changing shape, so a
/// reading here should not credit the chain with all of it.
/// </para>
/// </remarks>
public readonly struct ChainContext {
    private const int NoIndex = ChainErrorCollector.NoIndex;

    /// <summary>Matches <see cref="ValidationErrorCollector.MaxDepth"/> so the guard costs the same.</summary>
    public const int MaxDepth = 64;

    private readonly ChainErrorCollector _collector;

    /// <summary>The chain above this level, already on the heap.</summary>
    private readonly ChainPathNode? _ancestors;

    /// <summary>This level's segment, still only in the struct. Null at the root.</summary>
    private readonly string? _segment;

    private readonly int _index;
    private readonly string? _key;
    private readonly int _depth;

    public ChainContext(ChainErrorCollector collector) {
        ArgumentNullException.ThrowIfNull(collector);

        _collector = collector;
        _ancestors = null;
        _segment = null;
        _index = NoIndex;
        _key = null;
        _depth = 0;
    }

    private ChainContext(
        ChainErrorCollector collector,
        ChainPathNode? ancestors,
        string? segment,
        int index,
        string? key,
        int depth) {
        _collector = collector;
        _ancestors = ancestors;
        _segment = segment;
        _index = index;
        _key = key;
        _depth = depth;
    }

    public ChainContext Push(string segment) => Descend(segment, NoIndex, null);

    public ChainContext PushIndex(string segment, int index) => Descend(segment, index, null);

    public ChainContext PushKey(string segment, string key) => Descend(segment, NoIndex, key);

    public bool HasErrors => _collector.HasErrors;

    public int ErrorCount => _collector.Count;

    public void Add(
        string field,
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        _collector.Add(new ValidationError(BuildPath(field), code, message) { Severity = severity });

    public void AddHere(
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        _collector.Add(new ValidationError(BuildPath(null), code, message) { Severity = severity });

    private ChainContext Descend(string segment, int index, string? key) {
        var depth = _depth + 1;

        // A comparison rather than a walk, which is what carrying depth in the struct buys.
        if (depth > MaxDepth) {
            throw new InvalidOperationException(
                $"Validation nested more than {MaxDepth} levels deep. " +
                "This normally means the object graph contains a cycle.");
        }

        return new ChainContext(_collector, Pin(), segment, index, key, depth);
    }

    /// <summary>
    /// Moves this context's own segment onto the heap, because a child is about to need something
    /// to point at. The allocation every reading in <see cref="ContextShapeBenchmarks"/> turns on -
    /// and the reason a leaf push is free, since a leaf is never pinned.
    /// </summary>
    private ChainPathNode? Pin() =>
        _segment is null ? _ancestors : new ChainPathNode(_ancestors, _segment, _index, _key);

    /// <summary>
    /// Materializes the path. Only ever reached when an error is actually being added, which is
    /// what keeps a clean pass free of it.
    /// </summary>
    private string BuildPath(string? leaf) {
        if (_depth == 0) {
            return leaf ?? string.Empty;
        }

        var builder = new StringBuilder();

        // Recursion rather than a temporary array: the chain runs leaf-to-root and the unwind
        // visits it root-to-leaf for free. Bounded by MaxDepth, so the stack is not at risk.
        // This differs from the shipped BuildPath, which fills a temp array - a separable
        // improvement that could be back-ported without changing shape.
        AppendAncestors(builder, _ancestors);
        AppendSegment(builder, _segment!, _index, _key);

        if (leaf is not null) {
            if (builder.Length > 0) {
                builder.Append('.');
            }

            builder.Append(leaf);
        }

        return builder.ToString();
    }

    private static void AppendAncestors(StringBuilder builder, ChainPathNode? node) {
        if (node is null) {
            return;
        }

        AppendAncestors(builder, node.Parent);
        AppendSegment(builder, node.Name, node.Index, node.Key);
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
