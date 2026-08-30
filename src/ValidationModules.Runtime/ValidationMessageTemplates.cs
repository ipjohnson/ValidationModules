namespace ValidationModules;

/// <summary>
/// The default message templates, one per constraint shape. Holes are <c>{field}</c> and
/// <c>{0}</c>…<c>{9}</c>; <see cref="ValidationMessageInfo"/> fills them at render time.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>static readonly</c> fields, not <c>const</c>, and that is load-bearing.</b> A const
/// string is inlined into every consumer assembly that references it, which is exactly the
/// per-site duplication the composed helpers were built to avoid - measured at 107 of the 313
/// native bytes per constraint when messages were emitted as literals. A field reference keeps
/// each template existing once, here.
/// </para>
/// <para>
/// <b>Every shape decision is made before render.</b> Between / at-least / at-most and the
/// singular-plural switch are separate templates rather than logic in the renderer: the emitter
/// picks the variant at build time (the bounds are constants), and the Report* helpers pick it at
/// failure time for hand-written calls. Render itself is a dumb hole-filler, which is what makes
/// the default output byte-identical to the strings the helpers used to compose.
/// </para>
/// <para>
/// Wording changes here are message changes, not wire changes - the codes in
/// <see cref="ValidationCodes"/> are the contract, and the docs say so. They still break tests
/// that pin exact text, deliberately: a reworded default should be a decision, not drift.
/// </para>
/// </remarks>
public static class ValidationMessageTemplates {

    /// <summary>A required value was missing.</summary>
    public static readonly string Required = "{field} is required.";

    /// <summary>String length, both bounds declared.</summary>
    public static readonly string StringLengthBetween = "{field} must be between {0} and {1} characters.";

    /// <summary>String length, both bounds declared, upper bound of one.</summary>
    public static readonly string StringLengthBetweenSingular = "{field} must be between {0} and {1} character.";

    /// <summary>String length, upper bound only.</summary>
    public static readonly string StringLengthAtMost = "{field} must be at most {0} characters.";

    /// <summary>String length, upper bound of one.</summary>
    public static readonly string StringLengthAtMostSingular = "{field} must be at most {0} character.";

    /// <summary>String length, lower bound only.</summary>
    public static readonly string StringLengthAtLeast = "{field} must be at least {0} characters.";

    /// <summary>String length, lower bound of one.</summary>
    public static readonly string StringLengthAtLeastSingular = "{field} must be at least {0} character.";

    /// <summary>Element count, both bounds declared.</summary>
    public static readonly string ItemCountBetween = "{field} must be between {0} and {1} items.";

    /// <summary>Element count, both bounds declared, upper bound of one.</summary>
    public static readonly string ItemCountBetweenSingular = "{field} must be between {0} and {1} item.";

    /// <summary>Element count, upper bound only.</summary>
    public static readonly string ItemCountAtMost = "{field} must be at most {0} items.";

    /// <summary>Element count, upper bound of one.</summary>
    public static readonly string ItemCountAtMostSingular = "{field} must be at most {0} item.";

    /// <summary>Element count, lower bound only.</summary>
    public static readonly string ItemCountAtLeast = "{field} must be at least {0} items.";

    /// <summary>Element count, lower bound of one.</summary>
    public static readonly string ItemCountAtLeastSingular = "{field} must be at least {0} item.";

    /// <summary>Range, both bounds inclusive.</summary>
    public static readonly string RangeBetween = "{field} must be between {0} and {1}.";

    /// <summary>Range, exclusive lower and inclusive upper bound.</summary>
    public static readonly string RangeGreaterAndAtMost = "{field} must be greater than {0} and at most {1}.";

    /// <summary>Range, inclusive lower and exclusive upper bound.</summary>
    public static readonly string RangeAtLeastAndLess = "{field} must be at least {0} and less than {1}.";

    /// <summary>Range, both bounds exclusive.</summary>
    public static readonly string RangeGreaterAndLess = "{field} must be greater than {0} and less than {1}.";

    /// <summary>Range, inclusive lower bound only.</summary>
    public static readonly string RangeAtLeast = "{field} must be at least {0}.";

    /// <summary>Range, exclusive lower bound only.</summary>
    public static readonly string RangeGreaterThan = "{field} must be greater than {0}.";

    /// <summary>Range, inclusive upper bound only.</summary>
    public static readonly string RangeAtMost = "{field} must be at most {0}.";

    /// <summary>Range, exclusive upper bound only.</summary>
    public static readonly string RangeLessThan = "{field} must be less than {0}.";

    /// <summary>A value was not an exact multiple of its divisor.</summary>
    public static readonly string MultipleOf = "{field} must be a multiple of {0}.";

    /// <summary>A collection contained the same element twice.</summary>
    public static readonly string UniqueItems = "{field} must not contain duplicate items.";

    /// <summary>A string did not match its pattern. The pattern itself is deliberately absent.</summary>
    public static readonly string Pattern = "{field} is not in the required format.";

    /// <summary>A value was not in the permitted set. <c>{0}</c> is the joined set.</summary>
    public static readonly string AllowedValues = "{field} must be one of: {0}.";

    /// <summary>
    /// A value was in the forbidden set. <c>{0}</c> is the joined <i>forbidden</i> set - which is
    /// why this is its own template rather than <see cref="AllowedValues"/> reused: telling the
    /// caller to enter one of the values they must not enter was a bug, not a message.
    /// </summary>
    public static readonly string DeniedValues = "{field} must not be one of: {0}.";

    /// <summary>A flags value carried a bit outside the defined set. <c>{0}</c> is the defined flags.</summary>
    public static readonly string EnumFlags = "{field} must be a combination of: {0}.";

    /// <summary>A value was not an email address.</summary>
    public static readonly string Email = "{field} is not a valid email address.";

    /// <summary>A value was not a phone number.</summary>
    public static readonly string Phone = "{field} is not a valid phone number.";

    /// <summary>A value was not an http, https or ftp URL.</summary>
    public static readonly string Url = "{field} is not a valid http, https or ftp URL.";

    /// <summary>A value failed the credit card checksum.</summary>
    public static readonly string CreditCard = "{field} is not a valid credit card number.";

    /// <summary>A value was not well-formed Base64.</summary>
    public static readonly string Base64 = "{field} is not a valid Base64 string.";

    /// <summary>A file name's extension was not in the permitted set. <c>{0}</c> is the joined set.</summary>
    public static readonly string FileExtension = "{field} must have one of these file extensions: {0}.";

    /// <summary>A custom constraint failed and declared no message of its own.</summary>
    public static readonly string Custom = "{field} is invalid.";

    /// <summary>
    /// Every template above, by its stable shape key - <c>string_length.at_most</c>,
    /// <c>range.greater_and_less</c>, <c>enum.denied</c>. The key vocabulary language packs are
    /// authored against: the first segment is the wire code, the second
    /// exists only for the four codes whose sentence varies with their arguments.
    /// </summary>
    /// <remarks>
    /// A shape key is a contract the way a code is: its argument list is append-only, and a
    /// change to what a hole means is a new key, never a mutation. The map is built from the
    /// fields, so rewording any template's text changes nothing here - which is the whole point
    /// of keys that are not wording.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> TemplatesByKey { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) {
            ["required"] = Required,
            ["string_length.between"] = StringLengthBetween,
            ["string_length.between_singular"] = StringLengthBetweenSingular,
            ["string_length.at_most"] = StringLengthAtMost,
            ["string_length.at_most_singular"] = StringLengthAtMostSingular,
            ["string_length.at_least"] = StringLengthAtLeast,
            ["string_length.at_least_singular"] = StringLengthAtLeastSingular,
            ["array_bounds.between"] = ItemCountBetween,
            ["array_bounds.between_singular"] = ItemCountBetweenSingular,
            ["array_bounds.at_most"] = ItemCountAtMost,
            ["array_bounds.at_most_singular"] = ItemCountAtMostSingular,
            ["array_bounds.at_least"] = ItemCountAtLeast,
            ["array_bounds.at_least_singular"] = ItemCountAtLeastSingular,
            ["range.between"] = RangeBetween,
            ["range.greater_and_at_most"] = RangeGreaterAndAtMost,
            ["range.at_least_and_less"] = RangeAtLeastAndLess,
            ["range.greater_and_less"] = RangeGreaterAndLess,
            ["range.at_least"] = RangeAtLeast,
            ["range.greater_than"] = RangeGreaterThan,
            ["range.at_most"] = RangeAtMost,
            ["range.less_than"] = RangeLessThan,
            ["multiple_of"] = MultipleOf,
            ["unique_items"] = UniqueItems,
            ["pattern"] = Pattern,
            ["enum"] = AllowedValues,
            ["enum.denied"] = DeniedValues,
            ["enum.flags"] = EnumFlags,
            ["email"] = Email,
            ["phone"] = Phone,
            ["url"] = Url,
            ["credit_card"] = CreditCard,
            ["base64"] = Base64,
            ["file_extension"] = FileExtension,
            ["custom"] = Custom,
        };

    /// <summary>The key vocabulary, for pack tooling and the coverage check.</summary>
    public static IReadOnlyCollection<string> KnownKeys => TemplatesByKey.Keys as IReadOnlyCollection<string> ?? [.. TemplatesByKey.Keys];

    private static readonly Dictionary<string, string> KeysByTemplate = BuildReverse();

    private static Dictionary<string, string> BuildReverse() {
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in TemplatesByKey) {
            reverse[pair.Value] = pair.Key;
        }

        return reverse;
    }

    /// <summary>
    /// The shape key for one of the templates above, or null for a template this library did not
    /// write - a custom Message, a resx-backed provider's text. Built from the fields at first
    /// use, so it survives any rewording of the defaults; nothing here ever compares against
    /// wording a caller supplied.
    /// </summary>
    /// <param name="template">The template to identify - usually <c>ValidationMessageInfo.Template</c>.</param>
    public static string? KeyOf(string? template) =>
        template is not null && KeysByTemplate.TryGetValue(template, out var key) ? key : null;
}
