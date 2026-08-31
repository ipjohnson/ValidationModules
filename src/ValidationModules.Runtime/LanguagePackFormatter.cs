using System.Collections.Concurrent;
using System.Globalization;

namespace ValidationModules;

/// <summary>
/// The <see cref="ValidationMessageFormatter"/> over every registered
/// <see cref="IValidationLanguagePack"/>: one merged table per requested culture, built lazily on
/// that culture's first render, read in constant time from then on - at most two probes per
/// error, shape key then code, whatever the pack or assembly count.
/// </summary>
/// <remarks>
/// <para>
/// <b>Merge once, look up forever.</b> The packs are fixed at container build, so composition is
/// resolved when a culture is first rendered rather than re-walked per error: the culture's
/// parent chain is folded in (an <c>fr-CA</c> table layers <c>fr-CA</c> packs over <c>fr</c>
/// packs), and within the fold each entry carries its layer, so cross-pack precedence survives
/// the flattening. Measured against walking packs per render, the merged read is ~5× cheaper -
/// and, more to the point, it stops scaling with anything.
/// </para>
/// <para>
/// <b>Precedence, in one sentence:</b> the requested culture beats its parents, later
/// registration beats earlier within a culture, and the shape key beats the code only between
/// entries of the same layer - so a later pack that rewords a whole code takes all of its shapes,
/// which is what makes a one-entry override able to reword one message and a wholesale pack able
/// to reword a family. Registration order across assemblies is the order the composition root
/// called the <c>Add*</c> methods, exactly as it is for validators.
/// </para>
/// <para>
/// <b>An ordinary <c>Dictionary</c>, deliberately.</b> The storage benchmarks put
/// <c>FrozenDictionary</c>'s lookups within noise of <c>Dictionary</c>'s at pack sizes (3.6 ns
/// against 4.5 ns per probe at 35 entries) and its construction at 3-16× - the frozen shape earns
/// its keep on tables far larger and hotter than these.
/// </para>
/// <para>
/// <b>Culture is read per format call, never stored.</b> <c>CurrentUICulture</c> flows across
/// awaits and is set per request by localization middleware, so one singleton formatter localises
/// every request correctly. Tables are keyed by the requested culture's name, which localization
/// middleware bounds to the supported-culture list; each costs roughly a quarter microsecond and
/// a kilobyte, once. The first-use race is benign - two threads build equal tables and one wins -
/// the same posture the nested-validator arrays take.
/// </para>
/// <para>
/// Errors without a <see cref="ValidationError.MessageInfo"/> - finished-string errors - can
/// still match at the code level; their templates render with <c>{field}</c> only, argument holes
/// verbatim, per the tolerant-renderer rule.
/// </para>
/// </remarks>
public sealed class LanguagePackFormatter : ValidationMessageFormatter {

    private readonly IValidationLanguagePack[] _packs;

    private readonly ConcurrentDictionary<string, Dictionary<string, Entry>> _byCulture =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Entry(string Template, int Layer);

    /// <summary>
    /// Builds the formatter over the registered packs, in registration order.
    /// </summary>
    /// <param name="packs">
    /// Usually the container's <c>IEnumerable&lt;IValidationLanguagePack&gt;</c>, whose order is
    /// registration order - the order the layering rule is defined against.
    /// </param>
    public LanguagePackFormatter(IEnumerable<IValidationLanguagePack> packs) {
        ArgumentNullException.ThrowIfNull(packs);

        _packs = packs.ToArray();
    }

    /// <inheritdoc />
    public override string Format(in ValidationError error) {
        // An authored message - a constraint's Message = …, an Ensure's explicit message: - is
        // the application's own text and always wins. Before this, the override survived or died
        // according to whether the active pack happened to carry a bare key for that code: the
        // shipped de pack has a bare `required` and no bare `string_length`, so [Required]'s
        // override was replaced and [StringLength]'s kept, same class, same culture. Translating
        // custom text is the code's job: give the rule its own code and word it per culture.
        if (error.MessageIsAuthored) {
            return error.Message;
        }

        if (_packs.Length == 0) {
            return error.Message;
        }

        var table = _byCulture.GetOrAdd(CultureInfo.CurrentUICulture.Name, BuildTable, this);

        if (table.Count == 0) {
            return error.Message;
        }

        var info = error.MessageInfo;
        var shape = info is null ? null : ValidationMessageTemplates.KeyOf(info.Template);

        var found = table.TryGetValue(error.Code, out var byCode);

        // The shape key is the more specific claim, but only within a layer: a later pack that
        // rewrote the whole code outranks an earlier pack's shape entry, or a one-line override
        // could never reword a family.
        if (shape is not null && table.TryGetValue(shape, out var byShape) &&
            (!found || byShape.Layer >= byCode.Layer)) {
            byCode = byShape;
            found = true;
        }

        if (!found) {
            return error.Message;
        }

        return info is not null
            ? info.Render(in error, byCode.Template)
            : ValidationMessageInfo.RenderStandalone(byCode.Template, error.Field);
    }

    /// <summary>
    /// The merged table for one requested culture: its parent chain folded beneath it, each entry
    /// stamped with a strictly increasing layer so precedence survives the flattening.
    /// </summary>
    private static Dictionary<string, Entry> BuildTable(string cultureName, LanguagePackFormatter self) {
        // Parent-most first, requested culture last, so later writes are higher precedence and
        // the final overwrite per key is the winner - no per-key comparisons during the fold.
        var chain = new List<string>(3);

        for (var culture = Culture(cultureName); culture.Name.Length > 0; culture = culture.Parent) {
            chain.Add(culture.Name);
        }

        chain.Reverse();

        var table = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var layer = 0;

        foreach (var name in chain) {
            foreach (var pack in self._packs) {
                if (!string.Equals(pack.Culture, name, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                layer++;

                var templates = pack.Templates;

                for (var i = 0; i < templates.Count; i++) {
                    table[templates[i].Key] = new Entry(templates[i].Value, layer);
                }
            }
        }

        return table;
    }

    /// <summary>
    /// The culture for a name, or the invariant culture for one the platform refuses - an
    /// unrenderable name should fall through to default messages, not throw on the error path.
    /// </summary>
    private static CultureInfo Culture(string name) {
        try {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException) {
            return CultureInfo.InvariantCulture;
        }
    }
}
