using Microsoft.AspNetCore.Diagnostics;
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
    /// <para>
    /// Optional for the endpoint filter, which falls back to the defaults, and needed only to
    /// change the response shape or to catch validation exceptions thrown deeper in. The exception
    /// handler also needs <c>app.UseExceptionHandler()</c> in the pipeline, which is ASP.NET Core's
    /// requirement rather than ours.
    /// </para>
    /// <para>
    /// This also registers ASP.NET Core's own problem-details service, because an exception handler
    /// that declines an exception has to leave it somewhere. Without a fallback renderer,
    /// <c>UseExceptionHandler()</c> refuses to build at all, and the empty-lambda spelling that gets
    /// reached for instead installs a branch pipeline that returns 404 - which the middleware then
    /// reports as a 500 about a 404, burying the real fault. Registering the fallback here means the
    /// no-argument spelling works, and a malformed body stays the 400 it already was.
    /// </para>
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

        // The fallback for everything this package's handler declines. Its own registrations are
        // TryAdd, so an application that has already configured problem details keeps its own.
        services.AddProblemDetails();

        // TryAddEnumerable rather than AddExceptionHandler: a library and its host both calling this
        // would otherwise put two of each handler in the chain.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IExceptionHandler, BadRequestStatusHandler>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IExceptionHandler, ValidationExceptionHandler>());

        return services;
    }
}
