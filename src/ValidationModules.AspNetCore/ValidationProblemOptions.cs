namespace ValidationModules.AspNetCore;

/// <summary>
/// How a failed validation is rendered as an HTTP response.
/// </summary>
/// <remarks>
/// Defaults match what ASP.NET Core's own model-binding failures produce, so a client or a
/// generated OpenAPI document that already understands one understands the other.
/// </remarks>
public sealed class ValidationProblemOptions {

    /// <summary>The <c>title</c> member. Deliberately the same text ASP.NET Core uses.</summary>
    public string Title { get; set; } = "One or more validation errors occurred.";

    /// <summary>The <c>type</c> member.</summary>
    public string Type { get; set; } = "https://tools.ietf.org/html/rfc9110#section-15.5.1";

    /// <summary>The status code. 400 unless a caller has a reason.</summary>
    public int StatusCode { get; set; } = 400;

    /// <summary>
    /// Whether to include each failure's machine-readable code alongside its message, under the
    /// <c>validationCodes</c> extension member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default, and worth defending. RFC 9457's <c>errors</c> object is
    /// field&#160;→&#160;human-readable strings, which is the wrong thing for a client to branch on:
    /// messages are for people, they change wording, and they localise. The codes are the stable
    /// vocabulary this library maintains - <c>required</c>, <c>string_length</c>, <c>pattern</c> -
    /// and dropping them at the HTTP boundary throws away the part a caller can actually act on.
    /// </para>
    /// <para>
    /// An extension member rather than a replacement, because <c>errors</c> is what every existing
    /// client, test suite and Swagger UI already reads. Turn this off if a strict schema rejects
    /// unknown members.
    /// </para>
    /// </remarks>
    public bool IncludeCodes { get; set; } = true;

    /// <summary>
    /// Whether <see cref="ValidationSeverity.Warning"/> and <see cref="ValidationSeverity.Info"/>
    /// failures appear in the response.
    /// </summary>
    /// <remarks>
    /// Off by default. A response is only produced when validation failed, and a warning did not
    /// fail it - including one would tell a caller their request was rejected for a reason that did
    /// not reject it.
    /// </remarks>
    public bool IncludeNonErrors { get; set; }
}
