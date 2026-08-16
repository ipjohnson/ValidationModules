using Microsoft.Extensions.DependencyInjection.Extensions;
using ValidationModules;
using ValidationModules.AspNetCore;

// ReSharper disable once CheckNamespace - MS convention: DI extensions live in the DI namespace, so
// a consumer who has already imported it finds them without a second using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Wires validation responses into an ASP.NET Core application.
/// </summary>
public static class ValidationModulesAspNetCoreExtensions {

    /// <summary>
    /// Registers the problem-response options and the handler that maps a thrown
    /// <see cref="ValidationException"/> onto them.
    /// </summary>
    /// <remarks>
    /// Optional for the endpoint filter, which falls back to the defaults, and needed only to
    /// change the response shape or to catch validation exceptions thrown deeper in. The exception
    /// handler also needs <c>app.UseExceptionHandler()</c> in the pipeline, which is ASP.NET Core's
    /// requirement rather than ours.
    /// </remarks>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configure">Adjusts the response shape.</param>
    public static IServiceCollection AddValidationProblemDetails(
        this IServiceCollection services,
        Action<ValidationProblemOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ValidationProblemOptions>();

        if (configure is not null) {
            services.Configure(configure);
        }

        services.TryAddSingleton<ValidationExceptionHandler>();
        services.AddExceptionHandler<ValidationExceptionHandler>();

        return services;
    }
}
