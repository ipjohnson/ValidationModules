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
/// <para>
/// <b>Internal for the same reason as the filter</b> - it is registered by
/// <c>AddValidationProblemDetails()</c> and never named by a consumer, so its constructor is not
/// worth pinning into 1.0.0.
/// </para>
/// </remarks>
internal sealed class ValidationExceptionHandler : IExceptionHandler {
    private readonly ValidationProblemOptions _options;

    /// <summary>
    /// Creates the handler with options resolved from the container.
    /// </summary>
    /// <remarks>
    /// public on an internal type for the same reason as the filter: the container constructs it
    /// through <c>ActivatorUtilities</c>, which only considers public constructors. The type is
    /// internal, so this pins nothing.
    /// </remarks>
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

        var problem = ValidationProblem.ToProblemDetails(
            validation.Result, _options.WithFormatterFrom(httpContext.RequestServices));

        httpContext.Response.StatusCode = problem.Status ?? _options.StatusCode;

        await httpContext.Response
            .WriteAsJsonAsync(problem, ValidationProblemJsonContext.Default.ValidationProblemDetails, "application/problem+json", cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}
