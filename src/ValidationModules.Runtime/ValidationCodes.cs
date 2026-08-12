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
}
