using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ValidationModules.AspNetCore.Tests;

/// <summary>
/// What <c>AddValidationProblemDetails()</c> puts in the container.
/// </summary>
/// <remarks>
/// The behaviour these registrations produce is covered end to end by <c>ApiDemo.Tests</c>, over real
/// HTTP. What is pinned here is the shape of the container itself, because both faults this guards
/// against are invisible from the outside: a missing fallback renderer only shows up as a 500 on an
/// exception nothing else handles, and a duplicated handler only shows up as work done twice.
/// </remarks>
public class RegistrationTests {

    [Fact]
    public void RegistersAProblemDetailsFallback_SoUseExceptionHandlerCanBuild() {
        var services = new ServiceCollection().AddValidationProblemDetails();

        Assert.Contains(services, d => d.ServiceType == typeof(IProblemDetailsService));
    }

    [Fact]
    public void RegistersTheValidationHandler() {
        var services = new ServiceCollection().AddValidationProblemDetails();

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IExceptionHandler)
                 && d.ImplementationType == typeof(ValidationExceptionHandler));
    }

    /// <summary>
    /// A library and its host can both call this. Before <c>TryAddEnumerable</c>, that put two of
    /// each handler in the chain - harmless until one of them does work that is not idempotent.
    /// </summary>
    [Fact]
    public void CallingItTwice_AddsEachHandlerOnce() {
        var services = new ServiceCollection()
            .AddValidationProblemDetails()
            .AddValidationProblemDetails();

        var handlers = services
            .Where(d => d.ServiceType == typeof(IExceptionHandler))
            .Select(d => d.ImplementationType)
            .ToList();

        Assert.Equal(handlers.Count, handlers.Distinct().Count());
    }

    [Fact]
    public void ConfigureIsApplied() {
        var provider = new ServiceCollection()
            .AddValidationProblemDetails(options => options.StatusCode = StatusCodes.Status422UnprocessableEntity)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<ValidationProblemOptions>>();

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, options.Value.StatusCode);
    }
}
