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
