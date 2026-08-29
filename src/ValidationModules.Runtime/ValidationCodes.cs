namespace ValidationModules;

/// <summary>
/// The machine-readable code vocabulary, as constants.
/// </summary>
/// <remarks>
/// Three things emit these codes - the generated validators, the FluentValidation adapter, and
/// hand-written validators - and they have to agree exactly or a client switching on
/// <see cref="ValidationError.Code"/> breaks depending on which engine found the error. Constants
/// rather than literals is what stops that drifting silently.
///
/// The values are Hardened's existing wire codes verbatim, so retargeting it onto this library
/// changes no 400-response body.
/// </remarks>
public static class ValidationCodes {

    /// <summary>A required value was missing.</summary>
    public const string Required = "required";

    /// <summary>A string fell outside its length bounds.</summary>
    public const string StringLength = "string_length";

    /// <summary>A value fell outside its range.</summary>
    public const string Range = "range";

    /// <summary>A string did not match its pattern.</summary>
    public const string Pattern = "pattern";

    /// <summary>
    /// A value was not one of the permitted set. Named for OpenAPI's <c>enum</c> keyword, which is
    /// where the code originates and what Hardened already puts on the wire.
    /// </summary>
    public const string Enum = "enum";

    /// <summary>A collection fell outside its element-count bounds.</summary>
    public const string ArrayBounds = "array_bounds";

    /// <summary>
    /// A value was not an exact multiple of its divisor. OpenAPI's <c>multipleOf</c> keyword.
    /// </summary>
    public const string MultipleOf = "multiple_of";

    /// <summary>
    /// A collection contained the same element twice. OpenAPI's <c>uniqueItems</c> keyword.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ArrayBounds"/> rather than folded into it: the collection's size was
    /// never in question, and a client attaching the message to a count input would put it in the
    /// wrong place.
    /// </remarks>
    public const string UniqueItems = "unique_items";

    /// <summary>
    /// A value could not be read as the type it was declared as - <c>?limit=abc</c> where an integer
    /// was expected.
    /// </summary>
    /// <remarks>
    /// Nothing in this library emits this one. A validator receives a typed model, so by the time it
    /// runs the conversion has already succeeded; the code belongs to whatever bound the request.
    /// It lives here anyway because the vocabulary is defined by the wire rather than by which
    /// library produced the value, and a client switching on <see cref="ValidationError.Code"/> sees
    /// this alongside the rest. Splitting it out would leave five codes in one place and the sixth
    /// somewhere a consumer has to already know to look.
    ///
    /// Distinct from the constraint codes rather than folded into one of them: the value never
    /// became the right type, so no constraint on it was evaluated at all, and reporting
    /// <see cref="Range"/> would claim one was.
    /// </remarks>
    public const string Invalid = "invalid";

    /// <summary>
    /// A value was not an email address, as <c>[EmailAddress]</c> reads one: exactly one interior
    /// <c>'@'</c> and no line breaks. See <see cref="ConstraintChecks.IsEmail"/>.
    /// </summary>
    /// <remarks>
    /// The format family gets one code each rather than sharing a <c>format</c> code, for the
    /// reason <see cref="UniqueItems"/> is not folded into <see cref="ArrayBounds"/>: a client
    /// mapping codes to its own messages wants "enter a valid email address", not "invalid
    /// format", and the field name alone cannot tell it which to say.
    /// </remarks>
    public const string Email = "email";

    /// <summary>
    /// A value was not a phone number, as <c>[Phone]</c> reads one. See
    /// <see cref="ConstraintChecks.IsPhone"/>.
    /// </summary>
    public const string Phone = "phone";

    /// <summary>
    /// A value was not an http, https or ftp URL, as <c>[Url]</c> reads one. See
    /// <see cref="ConstraintChecks.IsUrl(string)"/>.
    /// </summary>
    public const string Url = "url";

    /// <summary>
    /// A value failed <c>[CreditCard]</c>'s Luhn checksum. See
    /// <see cref="ConstraintChecks.IsCreditCard"/>.
    /// </summary>
    public const string CreditCard = "credit_card";

    /// <summary>A value was not well-formed Base64, per <c>[Base64String]</c>.</summary>
    public const string Base64 = "base64";

    /// <summary>
    /// A file name's extension was not in <c>[FileExtensions]</c>' permitted set.
    /// </summary>
    public const string FileExtension = "file_extension";

    /// <summary>
    /// A custom DataAnnotations rule failed: a <c>ValidationAttribute</c> subclass, a
    /// <c>[CustomValidation]</c> method, or <c>IValidatableObject.Validate</c>.
    /// </summary>
    /// <remarks>
    /// One code for the whole family, for the reason <see cref="Predicate"/> covers every
    /// <c>Ensure</c>: the message is the rule's own and free to change; the code is the wire
    /// contract. DataAnnotations results carry no code at all, so any per-rule value would be one
    /// this library invented - and a client switching on an invented code would break the moment
    /// the rule's author renamed anything.
    /// </remarks>
    public const string Custom = "custom";

    /// <summary>
    /// A rule declared with <c>rules.Ensure(…)</c> failed. See API-SURFACE.md §19.5.
    /// </summary>
    /// <remarks>
    /// One code for every predicate, deliberately. Slugging or hashing the expression would read
    /// better and would make widening a bound from 30 to 35 a breaking change for every client
    /// switching on this - the <i>message</i> may track the rule, because it is human-facing, but the
    /// code is a wire contract. Two predicates on one field are told apart by their messages; pass
    /// <c>code:</c> when a client needs to tell them apart programmatically, which promotes that one
    /// rule into the contract deliberately rather than by accident.
    /// </remarks>
    public const string Predicate = "predicate";
}
