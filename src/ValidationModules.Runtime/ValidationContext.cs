namespace ValidationModules;

/// <summary>
/// A cursor into a <see cref="ValidationErrorCollector"/>: the collector, plus the index of this
/// context's node in the collector's path log. Two words, copied freely.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a <c>ref struct</c>.</b> It is a short-lived cursor, so <c>ref struct</c> is
/// the natural instinct, and it is what the design was originally written with. It does not
/// survive contact with the async contract: a <c>ref struct</c> cannot be a parameter of an
/// <c>async</c> method (CS4012), and under C# 12 - net8.0's default - it cannot even be a local in
/// one (CS9202). Keeping the modifier would have meant a second context type for the async side,
/// or forcing every async caller into a sync-core/async-tail split. See API-SURFACE.md §13.1.
/// </para>
/// <para>
/// <b>What replaces the safety it was providing.</b> The obvious zero-allocation path
/// representation is a stack the contexts index by depth. It is wrong under concurrency: two
/// sibling contexts at the same depth overwrite each other's segment, so a context that is pushed,
/// parked on an await, and used later reports whichever sibling wrote last - exactly what an async
/// validator fanning out over collection elements does. So the collector never overwrites. Every
/// push appends an immutable node, and a context holds that node's index, which makes a stored
/// context correct rather than merely prevented.
/// </para>
/// <para>
/// <b>The one rule.</b> A context is safe to hand to concurrent branches. The collector behind it
/// is not safe to mutate from them - if those branches add errors in parallel, build it with
/// <see cref="ValidationErrorCollector.CreateSynchronized"/>.
/// </para>
/// </remarks>
public readonly struct ValidationContext {
    private readonly ValidationErrorCollector _collector;
    private readonly int _node;

    /// <summary>
    /// Starts a validation pass at the root of the path.
    /// </summary>
    /// <param name="collector">Receives the errors this pass produces.</param>
    public ValidationContext(ValidationErrorCollector collector) {
        ArgumentNullException.ThrowIfNull(collector);

        _collector = collector;
        _node = ValidationErrorCollector.RootNode;
    }

    private ValidationContext(ValidationErrorCollector collector, int node) {
        _collector = collector;
        _node = node;
    }

    /// <summary>
    /// Descends into a nested object. An error added through the returned context reads
    /// <c>home.postalCode</c> rather than <c>postalCode</c>.
    /// </summary>
    /// <param name="segment">The field name of the nested object.</param>
    public ValidationContext Push(string segment) =>
        new(_collector, _collector.AddNode(_node, segment, ValidationErrorCollector.NoIndex));

    /// <summary>
    /// Descends into a collection element. An error added through the returned context reads
    /// <c>toys[3].name</c>.
    /// </summary>
    /// <param name="segment">The field name of the collection.</param>
    /// <param name="index">The element's position.</param>
    public ValidationContext PushIndex(string segment, int index) =>
        new(_collector, _collector.AddNode(_node, segment, index));

    /// <summary>
    /// Descends into a dictionary value. An error added through the returned context reads
    /// <c>items[sku-1].name</c>.
    /// </summary>
    /// <param name="segment">The field name of the dictionary.</param>
    /// <param name="key">The entry's key, rendered into the path.</param>
    public ValidationContext PushKey(string segment, string key) =>
        new(_collector, _collector.AddKeyedNode(_node, segment, key));

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
        ValidationSeverity severity = ValidationSeverity.Error) =>
        _collector.Emit(_node, field, code, message, severity);

    /// <summary>
    /// Records a failure against the current object itself, for type-level and cross-field rules.
    /// </summary>
    /// <param name="code">A stable machine-readable code.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public void AddHere(
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        _collector.Emit(_node, null, code, message, severity);

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
    /// The profile this pass is running under, or <see langword="null"/> for the default profile.
    /// </summary>
    public Type? Profile => _collector.Profile;
}
