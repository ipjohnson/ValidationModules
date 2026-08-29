using System.Globalization;
using System.Text;

namespace ValidationModules;

/// <summary>
/// The ingredients of a constraint's default message: the template and the constraint's own
/// arguments. Shared, not per-error - the generator emits one <c>static readonly</c> instance per
/// constraint site, and the parameterless constraints share the singletons declared here, so a
/// failing pass stores a reference and composes nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the message became data.</b> An error that stores a composed string is in one language
/// forever, decided at the moment of failure. One that stores <c>(template, args)</c> alongside
/// <see cref="ValidationError.Field"/>, <see cref="ValidationError.Code"/> and
/// <see cref="ValidationError.Value"/> renders on read: the default English in a log line, a
/// translation at the HTTP boundary, the code and arguments verbatim for a client that renders its
/// own text. Same shape as structured logging - the template travels, the rendering is the
/// reader's.
/// </para>
/// <para>
/// <b>A class, deliberately.</b> The whole design rests on instances being shared: per-site
/// statics for parameterized constraints (the arguments are compile-time constants, boxed once at
/// static initialization), singletons for parameterless ones. A struct inlined into every
/// <see cref="ValidationError"/> would copy three references per error and box per failure.
/// </para>
/// <para>
/// <b>Holes.</b> <c>{field}</c> and <c>{0}</c>…<c>{9}</c>, with <c>{{</c> and <c>}}</c> as
/// escapes; anything else brace-shaped renders verbatim rather than throwing, because the failure
/// path is the wrong place to discover a malformed template. Under
/// <see cref="DataAnnotationsHoles"/> the dialect is DataAnnotations' own - <c>{0}</c> is the
/// field, <c>{1}</c>… are this instance's arguments - so a resx template written for
/// <c>Validator.TryValidateObject</c> renders unchanged.
/// </para>
/// <para>
/// Arguments format with <see cref="CultureInfo.InvariantCulture"/> by default, exactly as the
/// composed helpers always did; <see cref="Render(in ValidationError, IFormatProvider?)"/> takes a
/// format provider for a reader that wants culture-formatted bounds beside translated text.
/// <see cref="ValidationError.Value"/> is never rendered here - permissiveness belongs to an
/// installed <see cref="ValidationMessageFormatter"/>, not to the default.
/// </para>
/// </remarks>
public sealed class ValidationMessageInfo {

    private static readonly object[] None = [];

    private readonly object[] _args;

    /// <summary>
    /// Creates the message info for one constraint shape.
    /// </summary>
    /// <param name="template">The template, holes included. See the class remarks.</param>
    /// <param name="args">
    /// The constraint's arguments in hole order - bounds, a divisor, a joined permitted set.
    /// Constants at every generated site, so the boxing happens once, at static initialization.
    /// </param>
    public ValidationMessageInfo(string template, params object[]? args) {
        ArgumentNullException.ThrowIfNull(template);

        Template = template;
        _args = args is { Length: > 0 } ? args : None;
    }

    /// <summary>The template rendered when no <see cref="Provider"/> is set.</summary>
    public string Template { get; }

    /// <summary>The constraint's arguments, in hole order. Empty rather than null.</summary>
    public IReadOnlyList<object> Args => _args;

    /// <summary>
    /// Supplies the template per render instead of <see cref="Template"/> - the resx shape, where
    /// the accessor property must be read under the current culture every time. Null for the
    /// ordinary baked-template case.
    /// </summary>
    public IValidationMessageProvider? Provider { get; init; }

    /// <summary>
    /// Renders holes in DataAnnotations' dialect: <c>{0}</c> is the field and <c>{1}</c>… are
    /// <see cref="Args"/>. Set on infos read from DataAnnotations attributes whose author wrote an
    /// <c>ErrorMessage</c> or resource template against that convention.
    /// </summary>
    public bool DataAnnotationsHoles { get; init; }

    /// <summary>Shared info for <c>[Required]</c>.</summary>
    public static readonly ValidationMessageInfo Required = new(ValidationMessageTemplates.Required);

    /// <summary>Shared info for <c>[UniqueItems]</c>.</summary>
    public static readonly ValidationMessageInfo UniqueItems = new(ValidationMessageTemplates.UniqueItems);

    /// <summary>Shared info for <c>[Pattern]</c>. The pattern is deliberately not an argument.</summary>
    public static readonly ValidationMessageInfo Pattern = new(ValidationMessageTemplates.Pattern);

    /// <summary>Shared info for <c>[EmailAddress]</c>.</summary>
    public static readonly ValidationMessageInfo Email = new(ValidationMessageTemplates.Email);

    /// <summary>Shared info for <c>[Phone]</c>.</summary>
    public static readonly ValidationMessageInfo Phone = new(ValidationMessageTemplates.Phone);

    /// <summary>Shared info for <c>[Url]</c>.</summary>
    public static readonly ValidationMessageInfo Url = new(ValidationMessageTemplates.Url);

    /// <summary>Shared info for <c>[CreditCard]</c>.</summary>
    public static readonly ValidationMessageInfo CreditCard = new(ValidationMessageTemplates.CreditCard);

    /// <summary>Shared info for <c>[Base64String]</c>.</summary>
    public static readonly ValidationMessageInfo Base64 = new(ValidationMessageTemplates.Base64);

    /// <summary>Shared info for a custom constraint that declared no message of its own.</summary>
    public static readonly ValidationMessageInfo Custom = new(ValidationMessageTemplates.Custom);

    /// <summary>
    /// Renders the default message for <paramref name="error"/>: template holes filled, arguments
    /// formatted with the given provider, <see cref="ValidationError.Value"/> never included.
    /// </summary>
    /// <param name="error">The error to render. Its <see cref="ValidationError.Field"/> fills <c>{field}</c>.</param>
    /// <param name="formatProvider">
    /// Formats the arguments. Null means <see cref="CultureInfo.InvariantCulture"/>, which is what
    /// the default <see cref="ValidationError.Message"/> read passes - a default message is closer
    /// to a wire format than to prose, and two machines reading one log agree on it.
    /// </param>
    public string Render(in ValidationError error, IFormatProvider? formatProvider = null) {
        var template = Provider?.Template(in error) ?? Template;

        return RenderTemplate(
            template, Leaf(error.Field), _args, DataAnnotationsHoles,
            formatProvider ?? CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Renders an alternate template - a language pack's - with this info's own arguments. The
    /// override dialect is always the library's (<c>{field}</c>, <c>{0}</c>…), whatever dialect
    /// the baked template used: packs are authored against the key inventory, not against
    /// DataAnnotations' conventions.
    /// </summary>
    /// <param name="error">The error being rendered. Its field fills <c>{field}</c>.</param>
    /// <param name="template">The replacement template, holes included.</param>
    /// <param name="formatProvider">Formats the arguments; null means invariant.</param>
    public string Render(in ValidationError error, string template, IFormatProvider? formatProvider = null) {
        ArgumentNullException.ThrowIfNull(template);

        return RenderTemplate(
            template, Leaf(error.Field), _args, daHoles: false,
            formatProvider ?? CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Renders a template for an error that carries no info - a finished-string error a language
    /// pack matched at the code level. Only <c>{field}</c> can be filled; argument holes render
    /// verbatim, per the tolerant-renderer rule.
    /// </summary>
    internal static string RenderStandalone(string template, string field, IFormatProvider? formatProvider = null) =>
        RenderTemplate(template, Leaf(field), None, daHoles: false, formatProvider ?? CultureInfo.InvariantCulture);

    /// <summary>
    /// The last segment of a field path: <c>toys[3].name</c> renders as "name must …", never
    /// "toys[3].name must …". This is the composed helpers' historical behavior - they built the
    /// message from the site's own field name before the path was prepended - kept because the
    /// path already lives in <see cref="ValidationError.Field"/> and repeating it in prose reads
    /// like an address label, not a sentence.
    /// </summary>
    private static string Leaf(string field) {
        var dot = field.LastIndexOf('.');

        return dot < 0 ? field : field.Substring(dot + 1);
    }

    /// <summary>
    /// The hole filler. Tolerant by design - an unknown or out-of-range hole renders verbatim,
    /// because a failing validation is the wrong moment to throw over a template typo - and
    /// exact-size by design: the common path allocates the argument strings and one final string,
    /// nothing else. The composed helpers this replaced were a single <c>string.Concat</c>, and a
    /// render that cost several times that would have moved the price rather than deferred it.
    /// </summary>
    private static string RenderTemplate(
        string template, string field, object[] args, bool daHoles, IFormatProvider formatProvider) {
        // Pass 1: locate and resolve the holes into locals. Every template this library writes
        // carries at most three substitutions ({field} and two arguments), so four slots cover the
        // realistic shapes without touching the heap; a hand-written template beyond that takes
        // the builder fallback and merely costs more.
        var count = 0;
        int start0 = 0, length0 = 0, start1 = 0, length1 = 0, start2 = 0, length2 = 0, start3 = 0, length3 = 0;
        string? replacement0 = null, replacement1 = null, replacement2 = null, replacement3 = null;

        for (var i = 0; i < template.Length; i++) {
            var current = template[i];
            int holeLength;
            string replacement;

            if (current == '{' && i + 1 < template.Length && template[i + 1] == '{') {
                (holeLength, replacement) = (2, "{");
            }
            else if (current == '}' && i + 1 < template.Length && template[i + 1] == '}') {
                (holeLength, replacement) = (2, "}");
            }
            else if (current == '{' && template.IndexOf('}', i + 1) is var close && close >= 0 &&
                Resolve(template.AsSpan(i + 1, close - i - 1), field, args, daHoles, formatProvider) is { } resolved) {
                (holeLength, replacement) = (close - i + 1, resolved);
            }
            else {
                continue;
            }

            switch (count) {
                case 0: (start0, length0, replacement0) = (i, holeLength, replacement); break;
                case 1: (start1, length1, replacement1) = (i, holeLength, replacement); break;
                case 2: (start2, length2, replacement2) = (i, holeLength, replacement); break;
                case 3: (start3, length3, replacement3) = (i, holeLength, replacement); break;
                default: return RenderSlow(template, field, args, daHoles, formatProvider);
            }

            count++;
            i += holeLength - 1;
        }

        if (count == 0) {
            return template;
        }

        var length = template.Length
            + replacement0!.Length - length0
            + (count > 1 ? replacement1!.Length - length1 : 0)
            + (count > 2 ? replacement2!.Length - length2 : 0)
            + (count > 3 ? replacement3!.Length - length3 : 0);

        // Pass 2: one exact-size string, filled left to right. The state tuple is copied, not
        // captured, so the whole render allocates the argument strings and this result.
        return string.Create(
            length,
            (template, count, start0, length0, replacement0, start1, length1, replacement1,
                start2, length2, replacement2, start3, length3, replacement3),
            static (span, state) => {
                var read = 0;
                var written = 0;

                Fill(span, state.template, state.start0, state.length0, state.replacement0!, ref read, ref written);
                if (state.count > 1) {
                    Fill(span, state.template, state.start1, state.length1, state.replacement1!, ref read, ref written);
                }

                if (state.count > 2) {
                    Fill(span, state.template, state.start2, state.length2, state.replacement2!, ref read, ref written);
                }

                if (state.count > 3) {
                    Fill(span, state.template, state.start3, state.length3, state.replacement3!, ref read, ref written);
                }

                state.template.AsSpan(read).CopyTo(span[written..]);
            });
    }

    private static void Fill(
        Span<char> span, string template, int start, int holeLength, string replacement,
        ref int read, ref int written) {
        template.AsSpan(read, start - read).CopyTo(span[written..]);
        written += start - read;
        replacement.CopyTo(span[written..]);
        written += replacement.Length;
        read = start + holeLength;
    }

    /// <summary>
    /// The rare-shape fallback: more holes than the scratch buffer. Correctness over exactness.
    /// </summary>
    private static string RenderSlow(
        string template, string field, object[] args, bool daHoles, IFormatProvider formatProvider) {
        var builder = new StringBuilder(template.Length + 32);

        for (var i = 0; i < template.Length; i++) {
            var current = template[i];

            if (current == '{' && i + 1 < template.Length && template[i + 1] == '{') {
                builder.Append('{');
                i++;
                continue;
            }

            if (current == '}' && i + 1 < template.Length && template[i + 1] == '}') {
                builder.Append('}');
                i++;
                continue;
            }

            if (current != '{') {
                builder.Append(current);
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0) {
                builder.Append(current);
                continue;
            }

            if (Resolve(template.AsSpan(i + 1, close - i - 1), field, args, daHoles, formatProvider) is { } replacement) {
                builder.Append(replacement);
                i = close;
            }
            else {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// A hole's replacement text, or null when the hole is not one this info can fill and should
    /// render verbatim.
    /// </summary>
    private static string? Resolve(
        ReadOnlySpan<char> hole, string field, object[] args, bool daHoles, IFormatProvider formatProvider) {
        if (hole.SequenceEqual("field")) {
            return field;
        }

        if (hole.Length != 1 || hole[0] is < '0' or > '9') {
            return null;
        }

        var position = hole[0] - '0';

        // DataAnnotations' own convention puts the field at {0} and shifts the arguments up one.
        if (daHoles) {
            if (position == 0) {
                return field;
            }

            position--;
        }

        if (position >= args.Length) {
            return null;
        }

        var argument = args[position];

        return argument is IFormattable formattable
            ? formattable.ToString(null, formatProvider)
            : argument.ToString() ?? string.Empty;
    }
}
