namespace ValidationModules;

/// <summary>
/// The path a validation pass is currently at, plus the collector its errors land in. A handful of
/// words, copied freely, allocating nothing at any depth.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it records.</b> Every segment walked, written into a buffer the caller supplies, with
/// <see cref="_depth"/> as the write index. How much of that is <i>rendered</i> is a separate
/// choice: <see cref="ValidationPathMode.Bounded"/>, the default, prints the outermost segment and
/// the immediate parent so an error four levels down reads <c>body...address.postalCode</c>;
/// <see cref="ValidationPathMode.Full"/> prints the lot. Rendering only happens when an error is
/// actually recorded, so a clean pass allocates nothing either way.
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
/// <b>Concurrency.</b> A pass is single-threaded. The buffer is a depth-indexed stack shared by
/// every context in one walk, so two branches descending at once would overwrite each other's
/// segments. Validate concurrently by giving each branch its own collector and merging the results,
/// which is faster than sharing one anyway.
/// </para>
/// </remarks>
public readonly struct ValidationContext {

    /// <summary>The index value meaning "this segment is not a collection element".</summary>
    private const int NoIndex = -1;

    private readonly ValidationErrorCollector _collector;

    /// <summary>
    /// The path segments walked to get here, indexed by depth. Shared by every context in one pass;
    /// slots at or above <see cref="_depth"/> belong to walks that have already unwound and are
    /// never read, which is why nothing has to clear them.
    /// </summary>
    private readonly PathSegment[] _path;

    /// <summary>
    /// How many times this pass has descended. Drives both the cycle guard and the decision to
    /// elide, and is the reason the guard is a comparison rather than a walk.
    /// </summary>
    private readonly int _depth;

    /// <summary>The stamp this context's own segment was written with; 0 at the root.</summary>
    private readonly long _stamp;

    /// <summary>
    /// Starts a validation pass at the root of the path.
    /// </summary>
    /// <param name="collector">Receives the errors this pass produces.</param>
    /// <summary>
    /// Starts a pass with a buffer of its own. The library's own entry points rent one instead;
    /// this is the shape for a caller holding a context by hand, where there is nothing to return
    /// it to and an allocation is the safe answer.
    /// </summary>
    public ValidationContext(ValidationErrorCollector collector)
        : this(collector, new PathSegment[ValidationErrorCollector.DefaultDepthLimit]) { }

    /// <summary>
    /// Starts a pass over a buffer the caller owns. Its length is the depth limit, so a caller that
    /// wants to fail earlier on a cycle passes a shorter one.
    /// </summary>
    internal ValidationContext(ValidationErrorCollector collector, PathSegment[] path) {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length == 0) {
            throw new ArgumentException("A validation path buffer needs room for at least one segment.", nameof(path));
        }

        _collector = collector;
        _path = path;
        _depth = 0;
    }

    private ValidationContext(ValidationErrorCollector collector, PathSegment[] path, int depth, long stamp) {
        _collector = collector;
        _path = path;
        _depth = depth;
        _stamp = stamp;
    }

    /// <summary>
    /// The services this validation pass can reach, or null when it was started without any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Forwarded from the collector, not held.</b> This struct is copied on every
    /// <see cref="Push(string)"/>, so a fifth field would cost eight bytes on every descent;
    /// forwarding costs one indirection on the rare read.
    /// </para>
    /// <para>
    /// <b>An escape hatch, not the library's own mechanism.</b> Generated code never calls
    /// <c>GetService</c> at a rule site - it resolves a typed lookup once and calls through it.
    /// This exists for user code, principally <c>rules.Apply(…)</c> rules, whose
    /// <c>RuleAction&lt;T&gt;</c> signature gives them no other way to reach a dependency, which is
    /// why checks needing services currently get hoisted into static helpers away from their
    /// declarations.
    /// </para>
    /// </remarks>
    public IServiceProvider? Services => _collector.Services;

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
    /// Records a failure against a field of the current object, and answers whether the pass
    /// carries on.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="ValidationFlow"/> is <see cref="ValidationFlow.Stop"/> when the
    /// collector is in <see cref="ValidationStopMode.StopOnFirstError"/> and this was a blocking
    /// failure. A caller that discards it simply keeps validating, which is what every
    /// <see cref="ValidationStopMode.CollectAll"/> pass does anyway.
    /// </remarks>
    /// <param name="field">The field name, appended to the current path.</param>
    /// <param name="code">A stable machine-readable code - see the vocabulary in API-SURFACE.md §4.1.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public ValidationFlow Report(
        string field,
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error) {
        var error = new ValidationError(BuildPath(field), code, message) { Severity = severity };

        return _collector.AddDirect(in error);
    }

    /// <summary>
    /// Records a failure against the current object itself, for type-level and cross-field rules.
    /// </summary>
    /// <param name="code">A stable machine-readable code.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public ValidationFlow ReportHere(
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error) {
        var error = new ValidationError(BuildPath(null), code, message) { Severity = severity };

        return _collector.AddDirect(in error);
    }

    /// <summary>
    /// Whether anything in this pass has failed. Pass-wide, not scoped to this subtree.
    /// </summary>
    public bool HasErrors => _collector.HasErrors;

    /// <summary>O(1) token for "has anything been recorded since"; see the collector.</summary>
    internal object? ChangeToken => _collector.ChangeToken;

    /// <summary>
    /// Whether this pass stops at its first blocking failure. Forwarded from the collector, which
    /// owns the decision; generated code reads the <see cref="ValidationFlow"/> that
    /// <see cref="Report(string,string,string,ValidationSeverity)"/> returns rather than this.
    /// </summary>
    public ValidationStopMode StopMode => _collector.StopMode;

    /// <summary>
    /// How many failures this pass has recorded. Snapshot it before and after a block to find out
    /// whether that block failed, which <see cref="HasErrors"/> cannot tell you.
    /// </summary>
    public int ErrorCount => _collector.Count;

    /// <summary>
    /// The one place a descent happens. The first push fills the outermost slot and leaves the
    /// parent empty - at depth 1 they would be the same segment, and holding it once keeps the
    /// render from having to special-case printing it twice. Every push after that overwrites the
    /// parent, which is exactly the middle of the path being dropped.
    /// </summary>
    private ValidationContext Descend(string segment, int index, string? key) {
        // The buffer's length is the limit: one number, so a buffer and a guard cannot disagree.
        if (_depth >= _path.Length) {
            throw new InvalidOperationException(
                $"Validation nested more than {_path.Length} levels deep at '{BuildPath(segment)}'. " +
                "That is the length of the path buffer this pass was given. Either the object graph " +
                "contains a cycle, or a deeper buffer is needed.");
        }

        var stamp = _collector.NextStamp();

        _path[_depth].Name = segment;
        _path[_depth].Key = key;
        _path[_depth].Index = index;
        _path[_depth].Stamp = stamp;

        return new ValidationContext(_collector, _path, _depth + 1, stamp);
    }

    /// <summary>
    /// Renders the compact path. Only ever reached when an error is actually being added, which is
    /// what keeps a clean pass at zero allocations, and it reads nothing outside this struct, which
    /// is what lets it run without the collector's lock.
    /// </summary>
    private string BuildPath(string? field) {
        if (_depth == 0) {
            return field ?? string.Empty;
        }

        EnsurePathIsIntact();

        return _collector.PathMode == ValidationPathMode.Full
            ? BuildFullPath(field)
            : BuildBoundedPath(field);
    }

    private string BuildBoundedPath(string? field) {
        var head = Segment(_path[0]);

        if (_depth == 1) {
            return field is null ? head : string.Concat(head, ".", field);
        }

        // Three or more descents means one was dropped between these two, and the marker says so.
        var joiner = _depth >= 3 ? "..." : ".";
        var tail = Segment(_path[_depth - 1]);

        return field is null
            ? string.Concat(head, joiner, tail)
            : $"{head}{joiner}{tail}.{field}";
    }

    private string BuildFullPath(string? field) {
        var builder = new System.Text.StringBuilder();

        for (var i = 0; i < _depth; i++) {
            if (i > 0) {
                builder.Append('.');
            }

            Append(builder, _path[i]);
        }

        if (field is not null) {
            builder.Append('.').Append(field);
        }

        return builder.ToString();
    }

    private static void Append(System.Text.StringBuilder builder, in PathSegment segment) {
        builder.Append(segment.Name);

        if (segment.Key is not null) {
            builder.Append('[').Append(segment.Key).Append(']');
        }
        else if (segment.Index >= 0) {
            builder.Append('[').Append(segment.Index).Append(']');
        }
    }

    /// <summary>
    /// Verifies that the buffer still holds the segments this context walked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The buffer is a depth-indexed stack shared by one pass, so a context is a cursor into a walk
    /// that is still in progress, not a snapshot that outlives it. Descend, use, unwind, descend
    /// again - which is the shape every engine emits, and the shape a loop over a collection has
    /// naturally. Holding two sibling contexts and adding to the first after creating the second is
    /// the pattern this catches.
    /// </para>
    /// <para>
    /// Stamps only ever increase, so a context's lineage is intact exactly when its own slot still
    /// carries its stamp and the stamps below it are strictly increasing. Anything written after
    /// this context was created carries a higher stamp and breaks one of those two. Runs only when
    /// an error is being recorded, so a clean pass never pays for it.
    /// </para>
    /// </remarks>
    private void EnsurePathIsIntact() {
        if (_path[_depth - 1].Stamp == _stamp) {
            var intact = true;

            for (var i = 1; i < _depth; i++) {
                if (_path[i - 1].Stamp >= _path[i].Stamp) {
                    intact = false;
                    break;
                }
            }

            if (intact) {
                return;
            }
        }

        throw new InvalidOperationException(
            "This validation context no longer describes where it was created. Its path was " +
            "overwritten by another descent in the same pass, which happens when two contexts from " +
            "the same parent are held at once and the earlier one is used after the later one was " +
            "created. A pass walks depth-first: descend, validate, let it unwind, then descend " +
            "again. Reporting the path as it now stands would attribute the error to the wrong " +
            "place, so it fails here instead.");
    }

    private static string Segment(in PathSegment segment) {
        if (segment.Key is not null) {
            return string.Concat(segment.Name, "[", segment.Key, "]");
        }

        return segment.Index >= 0 ? $"{segment.Name}[{segment.Index}]" : segment.Name;
    }
}
