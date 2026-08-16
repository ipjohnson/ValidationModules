namespace ValidationModules;

/// <summary>
/// The path a validation pass is currently at, plus the collector its errors land in. A handful of
/// words, copied freely, allocating nothing at any depth.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it records, and what it does not.</b> A context keeps the <i>outermost</i> segment, the
/// <i>immediate parent</i> segment and nothing between them, so an error four levels down reads
/// <c>body...address.postalCode</c> rather than <c>body.order.address.postalCode</c>. See
/// HANDOFF.md §3.1 for why full ancestry was dropped; the short version is that request bodies are
/// one or two levels deep, where this reports a complete path anyway, and the <c>...</c> only ever
/// appears when something really was omitted.
/// </para>
/// <para>
/// <b>Both retained segments carry their own index or key.</b> Rendering <c>toys.owner.name</c> for
/// what is really <c>toys[3].owner.name</c> would not be a shortened path, it would be a false one -
/// it asserts an object at <c>toys</c> that does not exist. Elision is allowed to omit, not to lie,
/// so the index travels with the segment it belongs to.
/// </para>
/// <para>
/// <b>Why this is not a <c>ref struct</c>.</b> It is a short-lived cursor, so <c>ref struct</c> is
/// the natural instinct, and it is what the design was originally written with. It does not
/// survive contact with the async contract: a <c>ref struct</c> cannot be a parameter of an
/// <c>async</c> method (CS4012), and under C# 12 - net8.0's default - it cannot even be a local in
/// one (CS9202). Keeping the modifier would have meant a second context type for the async side,
/// or forcing every async caller into a sync-core/async-tail split. See API-SURFACE.md §13.1.
/// </para>
/// <para>
/// <b>Concurrency.</b> A context is safe to hand to concurrent branches, and so is
/// <see cref="Push"/> - descending writes nothing anyone else can see, because the path lives
/// entirely in the copied struct. Only <i>adding</i> touches shared state, so a pass whose branches
/// add errors in parallel needs
/// <see cref="ValidationErrorCollector.CreateSynchronized"/>; one that fans out and adds afterwards
/// does not.
/// </para>
/// </remarks>
public readonly struct ValidationContext {

    /// <summary>The index value meaning "this segment is not a collection element".</summary>
    private const int NoIndex = -1;

    private readonly ValidationErrorCollector _collector;

    /// <summary>
    /// The scope this pass is running in, or <see langword="null"/> when it was started without
    /// one. See <see cref="Services"/>.
    /// </summary>
    private readonly IServiceProvider? _services;

    private readonly string? _outermost;
    private readonly string? _outermostKey;
    private readonly string? _parent;
    private readonly string? _parentKey;

    private readonly int _outermostIndex;
    private readonly int _parentIndex;

    /// <summary>
    /// How many times this pass has descended. Drives both the cycle guard and the decision to
    /// elide, and is the reason the guard is a comparison rather than a walk.
    /// </summary>
    private readonly int _depth;

    /// <summary>
    /// Starts a validation pass at the root of the path.
    /// </summary>
    /// <param name="collector">Receives the errors this pass produces.</param>
    /// <param name="services">
    /// The scope to resolve additional validators from, or <see langword="null"/>. Optional so that
    /// a generated validator called through its <c>Instance</c> field keeps working with no
    /// container present, which is the arrangement plan §2 rests on.
    /// </param>
    public ValidationContext(ValidationErrorCollector collector, IServiceProvider? services = null) {
        ArgumentNullException.ThrowIfNull(collector);

        _collector = collector;
        _services = services;
        _outermostIndex = NoIndex;
        _parentIndex = NoIndex;
    }

    private ValidationContext(
        ValidationErrorCollector collector,
        IServiceProvider? services,
        string? outermost,
        string? outermostKey,
        int outermostIndex,
        string? parent,
        string? parentKey,
        int parentIndex,
        int depth) {
        _collector = collector;
        _services = services;
        _outermost = outermost;
        _outermostKey = outermostKey;
        _outermostIndex = outermostIndex;
        _parent = parent;
        _parentKey = parentKey;
        _parentIndex = parentIndex;
        _depth = depth;
    }

    /// <summary>
    /// Descends into a nested object. An error added through the returned context reads
    /// <c>home.postalCode</c> rather than <c>postalCode</c>.
    /// </summary>
    /// <param name="segment">The field name of the nested object.</param>
    public ValidationContext Push(string segment) => Descend(segment, NoIndex, null);

    /// <summary>
    /// Descends into a collection element. An error added through the returned context reads
    /// <c>toys[3].name</c>.
    /// </summary>
    /// <param name="segment">The field name of the collection.</param>
    /// <param name="index">The element's position.</param>
    public ValidationContext PushIndex(string segment, int index) => Descend(segment, index, null);

    /// <summary>
    /// Descends into a dictionary value. An error added through the returned context reads
    /// <c>items[sku-1].name</c>.
    /// </summary>
    /// <param name="segment">The field name of the dictionary.</param>
    /// <param name="key">The entry's key, rendered into the path.</param>
    public ValidationContext PushKey(string segment, string key) => Descend(segment, NoIndex, key);

    /// <summary>
    /// Records a failure against a field of the current object.
    /// </summary>
    /// <param name="field">The field name, appended to the current path.</param>
    /// <param name="code">A stable machine-readable code - see the vocabulary in API-SURFACE.md §4.1.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public void Add(
        string field,
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error) {
        var error = new ValidationError(BuildPath(field), code, message) { Severity = severity };

        _collector.Add(in error);
    }

    /// <summary>
    /// Records a failure against the current object itself, for type-level and cross-field rules.
    /// </summary>
    /// <param name="code">A stable machine-readable code.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public void AddHere(
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error) {
        var error = new ValidationError(BuildPath(null), code, message) { Severity = severity };

        _collector.Add(in error);
    }

    /// <summary>
    /// Whether anything in this pass has failed. Pass-wide, not scoped to this subtree.
    /// </summary>
    public bool HasErrors => _collector.HasErrors;

    /// <summary>
    /// How many failures this pass has recorded. Snapshot it before and after a block to find out
    /// whether that block failed, which <see cref="HasErrors"/> cannot tell you.
    /// </summary>
    public int ErrorCount => _collector.Count;

    /// <summary>
    /// The scope this pass is running in, or <see langword="null"/> when it was started without one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried so that descending into a nested object can pick up validators registered for
    /// <i>that</i> type. Without it, composition worked at the top level and silently did not
    /// nested: a hand-written <c>IValidatorFor&lt;Address&gt;</c> ran when an Address was validated
    /// directly and was invisible when the same Address was reached through a Pet.
    /// </para>
    /// <para>
    /// <b>Nullable, and that is the design rather than a concession.</b> A generated validator is a
    /// singleton reached through a static field; there is no scope at type-init time and there does
    /// not need to be one, because structural constraints are a pure function of the value. Null
    /// means "no container took part in this pass", and every consumer of it short-circuits on that
    /// before allocating anything.
    /// </para>
    /// <para>
    /// It is <see cref="IServiceProvider"/> rather than a scope, because a validator has no business
    /// creating or disposing one. Whoever started the pass owns the lifetime.
    /// </para>
    /// </remarks>
    public IServiceProvider? Services => _services;

    /// <summary>
    /// The one place a descent happens. The first push fills the outermost slot and leaves the
    /// parent empty - at depth 1 they would be the same segment, and holding it once keeps the
    /// render from having to special-case printing it twice. Every push after that overwrites the
    /// parent, which is exactly the middle of the path being dropped.
    /// </summary>
    private ValidationContext Descend(string segment, int index, string? key) {
        // O(1) where the path log made it a walk to the root: depth is carried rather than counted.
        if (_depth >= ValidationErrorCollector.MaxDepth) {
            throw new InvalidOperationException(
                $"Validation nested more than {ValidationErrorCollector.MaxDepth} levels deep at " +
                $"'{BuildPath(segment)}'. This normally means the object graph contains a cycle.");
        }

        return _depth == 0
            ? new ValidationContext(_collector, _services, segment, key, index, null, null, NoIndex, 1)
            : new ValidationContext(
                _collector, _services, _outermost, _outermostKey, _outermostIndex,
                segment, key, index, _depth + 1);
    }

    /// <summary>
    /// Renders the compact path. Only ever reached when an error is actually being added, which is
    /// what keeps a clean pass at zero allocations, and it reads nothing outside this struct, which
    /// is what lets it run without the collector's lock.
    /// </summary>
    private string BuildPath(string? field) {
        if (_outermost is null) {
            return field ?? string.Empty;
        }

        var head = Segment(_outermost, _outermostKey, _outermostIndex);

        if (_parent is null) {
            return field is null ? head : string.Concat(head, ".", field);
        }

        // Three or more descents means one was dropped between these two, and the marker says so.
        var joiner = _depth >= 3 ? "..." : ".";
        var tail = Segment(_parent, _parentKey, _parentIndex);

        return field is null
            ? string.Concat(head, joiner, tail)
            : $"{head}{joiner}{tail}.{field}";
    }

    private static string Segment(string name, string? key, int index) {
        if (key is not null) {
            return string.Concat(name, "[", key, "]");
        }

        return index >= 0 ? $"{name}[{index}]" : name;
    }
}
