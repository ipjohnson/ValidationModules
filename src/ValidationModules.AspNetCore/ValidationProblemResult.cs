using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ValidationModules.AspNetCore;

/// <summary>
/// Writes a validation problem response using this package's own serialiser metadata.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than <c>Results.Problem(problem)</c>.</b> That overload serialises
/// through the application's configured <c>JsonSerializerOptions</c>. In a Native AOT app those
/// options resolve types through the consumer's own <c>JsonSerializerContext</c>, which knows about
/// the consumer's DTOs and has never heard of <see cref="ProblemDetails"/> - so the write throws
/// <see cref="NotSupportedException"/> and the request ends as a 500 with an empty body.
/// </para>
/// <para>
/// It fails only when published, only on the failure path, and only for the AOT consumers this
/// package exists to serve, so it is worth being blunt about: found by publishing a real minimal
/// API and posting an invalid body to it. No amount of in-process testing would have shown it,
/// because under the JIT the reflection fallback quietly succeeds.
/// </para>
/// <para>
/// Writing through <see cref="ValidationProblemJsonContext"/> makes the response independent of
/// however the consumer has configured JSON. That is a feature rather than a compromise: RFC 9457
/// fixes these member names, so there is nothing here a naming policy should be reshaping.
/// </para>
/// </remarks>
internal sealed class ValidationProblemResult : IResult {
    private readonly ValidationProblemDetails _problem;
    private readonly int _statusCode;

    internal ValidationProblemResult(ValidationProblemDetails problem, int statusCode) {
        _problem = problem;
        _statusCode = statusCode;
    }

    public async Task ExecuteAsync(HttpContext httpContext) {
        ArgumentNullException.ThrowIfNull(httpContext);

        httpContext.Response.StatusCode = _problem.Status ?? _statusCode;

        await httpContext.Response.WriteAsJsonAsync(
            _problem,
            ValidationProblemJsonContext.Default.ValidationProblemDetails,
            "application/problem+json",
            httpContext.RequestAborted).ConfigureAwait(false);
    }
}
