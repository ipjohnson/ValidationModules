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

        return RenderTemplate(template, Leaf(error.Field), formatProvider ?? CultureInfo.InvariantCulture);
    }

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
    /// The hole filler. Tolerant by design: an unknown or out-of-range hole renders verbatim,
    /// because a failing validation is the wrong moment to throw over a template typo.
    /// </summary>
    private string RenderTemplate(string template, string field, IFormatProvider formatProvider) {
        var builder = new StringBuilder(template.Length + 24);

        for (var i = 0; i < template.Length; i++) {
            var current = template[i];

            if (current == '}' && i + 1 < template.Length && template[i + 1] == '}') {
                builder.Append('}');
                i++;
                continue;
            }

            if (current != '{') {
                builder.Append(current);
                continue;
            }

            if (i + 1 < template.Length && template[i + 1] == '{') {
                builder.Append('{');
                i++;
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0) {
                builder.Append(current);
                continue;
            }

            var hole = template.AsSpan(i + 1, close - i - 1);

            if (TryFillHole(builder, hole, field, formatProvider)) {
                i = close;
            }
            else {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    private bool TryFillHole(StringBuilder builder, ReadOnlySpan<char> hole, string field, IFormatProvider formatProvider) {
        if (hole.SequenceEqual("field")) {
            builder.Append(field);
            return true;
        }

        if (hole.Length != 1 || hole[0] is < '0' or > '9') {
            return false;
        }

        var position = hole[0] - '0';

        // DataAnnotations' own convention puts the field at {0} and shifts the arguments up one.
        if (DataAnnotationsHoles) {
            if (position == 0) {
                builder.Append(field);
                return true;
            }

            position--;
        }

        if (position >= _args.Length) {
            return false;
        }

        var argument = _args[position];
        builder.Append(argument is IFormattable formattable
            ? formattable.ToString(null, formatProvider)
            : argument.ToString());

        return true;
    }
}
