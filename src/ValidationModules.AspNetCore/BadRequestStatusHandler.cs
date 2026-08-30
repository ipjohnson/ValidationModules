using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ValidationModules.AspNetCore;

/// <summary>
/// Restores the status code an exception already carries, so a request the client got wrong does not
/// come back as a server fault.
/// </summary>
/// <remarks>
/// <para>
/// A body that never parsed raises <see cref="BadHttpRequestException"/>, which carries
/// <c>StatusCode = 400</c>. The exception-handling middleware sets 500 before running any handler and
/// never reads that property, so without this the request ends as a 500 - and under the empty-lambda
/// spelling the guide used to recommend, as a 500 whose message is about a 404.
/// </para>
/// <para>
/// <b>The two framework versions need different handling, and the difference is not cosmetic.</b>
/// From .NET 9 the middleware renders through <c>IProblemDetailsService</c> using whatever status the
/// response currently holds, so setting the status and <em>declining</em> is enough: the application's
/// own problem-details customisation still runs, and this changes nothing but the number. On .NET 8
/// the middleware passes <c>Status = DefaultStatusCode</c> into the problem document itself, which
/// overwrites the response status again - so there the exception has to be handled outright, and the
/// body is written here.
/// </para>
/// <para>
/// An application that wants its own shape for these can register its own
/// <see cref="IExceptionHandler"/> before calling <c>AddValidationProblemDetails()</c>; handlers run
/// in registration order and the first to handle wins.
/// </para>
/// </remarks>
internal sealed class BadRequestStatusHandler : IExceptionHandler {

    /// <inheritdoc/>
    public
#if NET9_0_OR_GREATER
        ValueTask<bool>
#else
        async ValueTask<bool>
#endif
        TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not BadHttpRequestException badRequest) {
#if NET9_0_OR_GREATER
            return ValueTask.FromResult(false);
#else
            return false;
#endif
        }

        httpContext.Response.StatusCode = badRequest.StatusCode;

#if NET9_0_OR_GREATER
        // Declining leaves the body to whatever the application configured, status now correct.
        return ValueTask.FromResult(false);
#else
        var problem = new ProblemDetails {
            Status = badRequest.StatusCode,
            Title = ReasonFor(badRequest.StatusCode),
            Type = TypeFor(badRequest.StatusCode),
        };

        await httpContext.Response
            .WriteAsJsonAsync(
                problem, ValidationProblemJsonContext.Default.ProblemDetails,
                "application/problem+json", cancellationToken)
            .ConfigureAwait(false);

        return true;
#endif
    }

#if !NET9_0_OR_GREATER
    private static string ReasonFor(int statusCode) => statusCode switch {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status413PayloadTooLarge => "Payload Too Large",
        StatusCodes.Status415UnsupportedMediaType => "Unsupported Media Type",
        _ => "Request Error",
    };

    private static string TypeFor(int statusCode) => statusCode switch {
        StatusCodes.Status400BadRequest => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        StatusCodes.Status413PayloadTooLarge => "https://tools.ietf.org/html/rfc9110#section-15.5.14",
        StatusCodes.Status415UnsupportedMediaType => "https://tools.ietf.org/html/rfc9110#section-15.5.16",
        _ => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
    };
#endif
}
