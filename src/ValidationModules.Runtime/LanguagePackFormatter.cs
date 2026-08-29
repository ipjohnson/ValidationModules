using System.Globalization;

namespace ValidationModules;

/// <summary>
/// The <see cref="ValidationMessageFormatter"/> over every registered
/// <see cref="IValidationLanguagePack"/>: picks a culture by walking
/// <see cref="CultureInfo.CurrentUICulture"/> and its parents, picks a template by shape key then
/// code with later-registered packs winning per key, renders it with the error's own arguments,
/// and falls through to the default render when nobody has anything to say.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layering is per key, later registration first.</b> A one-entry pack registered after a full
/// one overrides exactly that message and inherits everything else - which makes overriding the
/// same gesture as extending, and makes MSBuild's evaluation order do the right thing on its own:
/// package-delivered files are added before project items, so an app-local file lands later and
/// wins. Within one pack, the shape key is consulted before the code, because it is the more
/// specific claim; across packs, recency beats specificity, so a later pack that rewords a whole
/// code takes all of its shapes.
/// </para>
/// <para>
/// <b>Culture is read per format call, never stored.</b> <c>CurrentUICulture</c> flows across
/// awaits and is set per request by localization middleware, so one singleton formatter localises
/// every request correctly. The walk is the satellite fallback chain done in the open:
/// <c>fr-CA</c> → <c>fr</c>, stopping before invariant - the invariant answer is the default
/// render, which needs no pack.
/// </para>
/// <para>
/// Errors without a <see cref="ValidationError.MessageInfo"/> - finished-string errors - can
/// still match at the code level; their templates render with <c>{field}</c> only, argument holes
/// verbatim, per the tolerant-renderer rule.
/// </para>
/// </remarks>
public sealed class LanguagePackFormatter : ValidationMessageFormatter {

    private readonly Dictionary<string, IValidationLanguagePack[]> _byCulture;

    /// <summary>
    /// Builds the formatter over the registered packs, in registration order.
    /// </summary>
    /// <param name="packs">
    /// Usually the container's <c>IEnumerable&lt;IValidationLanguagePack&gt;</c>, whose order is
    /// registration order - the order the layering rule is defined against.
    /// </param>
    public LanguagePackFormatter(IEnumerable<IValidationLanguagePack> packs) {
        ArgumentNullException.ThrowIfNull(packs);

        var byCulture = new Dictionary<string, List<IValidationLanguagePack>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in packs) {
            if (!byCulture.TryGetValue(pack.Culture, out var list)) {
                byCulture[pack.Culture] = list = new List<IValidationLanguagePack>(1);
            }

            // Newest first, so the per-key walk below reads in override order.
            list.Insert(0, pack);
        }

        _byCulture = new Dictionary<string, IValidationLanguagePack[]>(byCulture.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in byCulture) {
            _byCulture[pair.Key] = pair.Value.ToArray();
        }
    }

    /// <inheritdoc />
    public override string Format(in ValidationError error) {
        if (_byCulture.Count == 0) {
            return error.Message;
        }

        var info = error.MessageInfo;
        var shapeKey = info is null ? null : ValidationMessageTemplates.KeyOf(info.Template);

        for (var culture = CultureInfo.CurrentUICulture;
            culture.Name.Length > 0;
            culture = culture.Parent) {
            if (!_byCulture.TryGetValue(culture.Name, out var packs)) {
                continue;
            }

            foreach (var pack in packs) {
                var template = (shapeKey is null ? null : pack.Template(shapeKey)) ?? pack.Template(error.Code);

                if (template is null) {
                    continue;
                }

                return info is not null
                    ? info.Render(in error, template)
                    : ValidationMessageInfo.RenderStandalone(template, error.Field);
            }
        }

        return error.Message;
    }
}
