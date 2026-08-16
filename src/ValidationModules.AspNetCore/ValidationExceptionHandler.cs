using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ValidationModules.AspNetCore;

/// <summary>
/// Turns a <see cref="ValidationException"/> thrown anywhere in the request into the same problem
/// response the endpoint filter produces.
/// </summary>
/// <remarks>
/// <para>
/// The filter covers what a handler was handed. This covers what a service decided further in -
/// <c>validator.ValidateAndThrow(order)</c> inside a domain method, which is a reasonable way to
/// write that code and otherwise surfaces as a 500.
/// </para>
/// <para>
/// The two paths share <see cref="ValidationProblem"/> deliberately. A service whose validation
/// failures are shaped one way when caught early and another way when thrown late is a service
/// whose clients need two parsers.
/// </para>
/// </remarks>
public sealed class ValidationExceptionHandler : IExceptionHandler {
    private readonly ValidationProblemOptions _options;

    /// <summary>Creates the handler with options resolved from the container.</summary>
    public ValidationExceptionHandler(IOptions<ValidationProblemOptions> options) {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not ValidationException validation) {
            return false;
        }

        var problem = ValidationProblem.ToProblemDetails(validation.Result, _options);

        httpContext.Response.StatusCode = problem.Status ?? _options.StatusCode;

        await httpContext.Response
            .WriteAsJsonAsync(problem, ValidationProblemJsonContext.Default.ValidationProblemDetails, "application/problem+json", cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}
