namespace ValidationModules;

/// <summary>
/// A <see cref="ValidationMessageFormatter"/> dispatched by <see cref="ValidationError.Code"/>:
/// map exactly the codes you care about, and every other error keeps its default render. This is
/// the branch-when-you-need-to shape - a second language, or a reworded rule, is a handful of
/// mapped codes rather than a subclass of anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Translations are code on purpose.</b> A mapped delegate is compile-checked interpolation -
/// a hole that references nothing is a compile error, not a runtime format exception in one
/// culture - and pluralization or grammatical agreement is an ordinary conditional, which no
/// <c>{0}</c>-style template dialect can express. A resx- or IStringLocalizer-backed formatter is
/// a thin subclass of <see cref="ValidationMessageFormatter"/> for teams with a translation
/// pipeline; this type is the direct form.
/// </para>
/// <para>
/// User-defined codes dispatch exactly like built-ins - <c>rules.Ensure(…, code: "date_order")</c>
/// and a custom constraint's code are map keys with nothing special about them - which is the
/// reason this is keyed by string code rather than by constraint kind.
/// </para>
/// <para>
/// Build once, read forever: <see cref="Map"/> mutates until first use and the instance is
/// immutable in effect afterwards. Register it as a singleton and let
/// <c>CultureInfo.CurrentUICulture</c> vary per request inside the delegates, or build one per
/// culture and pick at the boundary - both shapes work because the map holds no culture of its
/// own.
/// </para>
/// </remarks>
public sealed class ValidationMessageMap : ValidationMessageFormatter {

    /// <summary>Renders one mapped code. Return the finished message.</summary>
    /// <param name="error">The error to render.</param>
    public delegate string MessageRenderer(in ValidationError error);

    private readonly Dictionary<string, MessageRenderer> _renderers = new(StringComparer.Ordinal);

    /// <summary>
    /// Maps one code to a renderer. Last mapping for a code wins, so a base map can be copied and
    /// selectively overridden.
    /// </summary>
    /// <param name="code">The <see cref="ValidationError.Code"/> to intercept.</param>
    /// <param name="renderer">Builds the message for every error carrying that code.</param>
    /// <returns>This map, for chaining.</returns>
    public ValidationMessageMap Map(string code, MessageRenderer renderer) {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(renderer);

        _renderers[code] = renderer;

        return this;
    }

    /// <summary>
    /// The mapped renderer's answer, or the error's own default render when the code is unmapped.
    /// </summary>
    /// <param name="error">The error to render.</param>
    public override string Format(in ValidationError error) =>
        _renderers.TryGetValue(error.Code, out var renderer)
            ? renderer(in error)
            : error.Message;
}
