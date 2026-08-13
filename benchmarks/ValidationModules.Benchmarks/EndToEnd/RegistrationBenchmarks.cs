using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using ValidationModules.Benchmarks.Models;

namespace ValidationModules.Benchmarks.EndToEnd;

/// <summary>
/// Startup and resolution: registering the generated table, building the provider, and pulling a
/// validator back out of it.
/// </summary>
/// <remarks>
/// <para>
/// Registration is paid once per process, so its absolute cost matters far less than its shape. It
/// is here because the shape is a design commitment: every entry is a factory delegate closing over
/// a static singleton, chosen so that nothing routes through <c>ActivatorUtilities</c> constructor
/// reflection. A registration cost that grew with validator count in some non-linear way, or that
/// showed reflection creeping back in, would show up here first.
/// </para>
/// <para>
/// Resolution is per-request for anything scoped, so <see cref="Resolve_Validator"/> is the one to
/// watch. It should be a dictionary lookup returning an already-constructed singleton - and the
/// comparison against <see cref="Resolve_Runner"/>, which is scoped and therefore constructed per
/// scope, is what a filter avoids by resolving once at handler construction.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.EndToEnd)]
public class RegistrationBenchmarks {
    private ServiceProvider _provider = null!;

    [GlobalSetup]
    public void Setup() {
        var services = new ServiceCollection();
        services.AddValidationModules(GeneratedValidators.All);
        services.AddValidationRunner<Customer>();

        _provider = services.BuildServiceProvider();

        // Warm the first resolution out of the measured path: MS.DI compiles a call site on first
        // use, and a benchmark that included it would be measuring that once and nothing after.
        _ = _provider.GetRequiredService<IValidatorFor<Customer>>();
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    /// <summary>
    /// Registering the whole generated table. Every entry is a
    /// <see cref="ServiceDescriptor"/> over a factory delegate, so this is a list append per
    /// validated type and nothing more.
    /// </summary>
    [Benchmark(Baseline = true, Description = "AddValidationModules over the generated table")]
    public int Register() {
        var services = new ServiceCollection();

        services.AddValidationModules(GeneratedValidators.All);

        return services.Count;
    }

    /// <summary>
    /// Registration plus building the provider, which is the whole of what validation adds to
    /// application startup.
    /// </summary>
    [Benchmark(Description = "Register + BuildServiceProvider")]
    public ServiceProvider Register_AndBuild() {
        var services = new ServiceCollection();
        services.AddValidationModules(GeneratedValidators.All);

        return services.BuildServiceProvider();
    }

    [Benchmark(Description = "Resolve IValidatorFor<T> - a singleton lookup")]
    public IValidatorFor<Customer> Resolve_Validator() =>
        _provider.GetRequiredService<IValidatorFor<Customer>>();

    /// <summary>
    /// Scoped, so this constructs a runner and enumerates the registered validators into it. What a
    /// handler pays per request if it resolves rather than holding one.
    /// </summary>
    [Benchmark(Description = "Create a scope and resolve ValidationRunner<T>")]
    public ValidationRunner<Customer> Resolve_Runner() {
        using var scope = _provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<ValidationRunner<Customer>>();
    }
}
