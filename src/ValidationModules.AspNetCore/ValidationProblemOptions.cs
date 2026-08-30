namespace ValidationModules.AspNetCore;

/// <summary>
/// How a failed validation is rendered as an HTTP response.
/// </summary>
/// <remarks>
/// Defaults match what ASP.NET Core's own model-binding failures produce, so a client or a
/// generated OpenAPI document that already understands one understands the other.
/// </remarks>
public sealed class ValidationProblemOptions {

    private string? _type;

    /// <summary>The <c>title</c> member. Deliberately the same text ASP.NET Core uses.</summary>
    public string Title { get; set; } = "One or more validation errors occurred.";

    /// <summary>
    /// The <c>type</c> member. Unless set explicitly, it follows <see cref="StatusCode"/> to the
    /// matching RFC 9110 section - a 422 body must not point at the definition of 400.
    /// </summary>
    public string Type {
        get => _type ?? TypeFor(StatusCode);
        set => _type = value;
    }

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

    /// <summary>
    /// Renders each failure's message for the response body, in place of the default render.
    /// Null - the default - keeps <see cref="ValidationError.Message"/> exactly as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the HTTP boundary's read-side hook for the structured error model: errors carry
    /// data, and the reader decides the text. A <see cref="ValidationMessageMap"/> here is how a
    /// second language reaches the <c>errors</c> object - the formatter runs inside the request,
    /// after localization middleware has set <c>CurrentUICulture</c>, so a map that reads the
    /// ambient culture localises per request with nothing else configured.
    /// </para>
    /// <para>
    /// The <c>validationCodes</c> extension is untouched by design - it is the stable vocabulary,
    /// and rendering is exactly the thing it must not depend on. And this hook is also the one
    /// place a response can opt into echoing <see cref="ValidationError.Value"/>, which no default
    /// ever does - that decision rides with the formatter that makes it.
    /// </para>
    /// </remarks>
    public ValidationMessageFormatter? MessageFormatter { get; set; }

    /// <summary>
    /// These options with the container's <see cref="ValidationMessageFormatter"/> filled in,
    /// when none was set explicitly - which is what makes a registered language pack localize
    /// problem details with no options code at all. An explicit formatter always wins, and
    /// options that already have one (or a container that has none) come back unchanged.
    /// </summary>
    internal ValidationProblemOptions WithFormatterFrom(IServiceProvider services) {
        if (MessageFormatter is not null ||
            services.GetService(typeof(ValidationMessageFormatter)) is not ValidationMessageFormatter formatter) {
            return this;
        }

        var copy = Copy();
        copy.MessageFormatter = formatter;
        return copy;
    }

    /// <summary>
    /// These options with <see cref="StatusCode"/> replaced - the per-endpoint override
    /// <c>Validate&lt;T&gt;(statusCode: …)</c> rides on. A <see cref="Type"/> that was never set
    /// explicitly keeps following the new status; one that was set stays, because an explicit
    /// value is the author's decision whatever the status.
    /// </summary>
    internal ValidationProblemOptions WithStatusCode(int statusCode) {
        if (statusCode == StatusCode) {
            return this;
        }

        var copy = Copy();
        copy.StatusCode = statusCode;
        return copy;
    }

    /// <summary>
    /// A member-for-member copy. The <c>_type</c> backing field is carried rather than the
    /// <see cref="Type"/> property, so a derived type link survives the copy instead of freezing
    /// into an explicit value.
    /// </summary>
    private ValidationProblemOptions Copy() => new() {
        Title = Title,
        _type = _type,
        StatusCode = StatusCode,
        IncludeCodes = IncludeCodes,
        IncludeNonErrors = IncludeNonErrors,
        MessageFormatter = MessageFormatter,
    };

    /// <summary>
    /// The RFC 9110 section for a client-error status, the same table ASP.NET Core's
    /// <c>ProblemDetailsDefaults</c> keeps. A status outside it maps to <c>about:blank</c>, which
    /// RFC 9457 defines as "the problem is the status code" - never a link that contradicts it.
    /// </summary>
    private static string TypeFor(int statusCode) => statusCode switch {
        400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        401 => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        403 => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        404 => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        405 => "https://tools.ietf.org/html/rfc9110#section-15.5.6",
        406 => "https://tools.ietf.org/html/rfc9110#section-15.5.7",
        408 => "https://tools.ietf.org/html/rfc9110#section-15.5.9",
        409 => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        412 => "https://tools.ietf.org/html/rfc9110#section-15.5.13",
        413 => "https://tools.ietf.org/html/rfc9110#section-15.5.14",
        415 => "https://tools.ietf.org/html/rfc9110#section-15.5.16",
        422 => "https://tools.ietf.org/html/rfc9110#section-15.5.21",
        426 => "https://tools.ietf.org/html/rfc9110#section-15.5.22",
        500 => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        _ => "about:blank",
    };
}
