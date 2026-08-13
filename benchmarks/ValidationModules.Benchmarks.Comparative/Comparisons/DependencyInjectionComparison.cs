using BenchmarkDotNet.Attributes;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using ValidationModules.Benchmarks.Comparative.Engines;
using ValidationModules.Benchmarks.Comparative.Models;

namespace ValidationModules.Benchmarks.Comparative.Comparisons;

/// <summary>
/// Getting validators into a container, and getting one back out per request.
/// </summary>
/// <remarks>
/// <para>
/// The registration halves measure two different designs, not two implementations of one.
/// ValidationModules registers a generated table of factory delegates, each closing over a static
/// singleton - every closed type named in emitted source, nothing discovered. FluentValidation's
/// documented registration scans an assembly for <c>AbstractValidator&lt;T&gt;</c> subclasses and
/// registers what it finds, which is convenient and is the thing §2 of the plan rules out: assembly
/// scanning does not survive trimming, and the open-generic registration it produces resolves
/// through MS.DI's reflective activation.
/// </para>
/// <para>
/// Both are paid once at startup, so read them for shape rather than for absolute cost. The
/// resolution benchmarks are the per-request ones and matter more.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(ComparativeCategories.DependencyInjection)]
public class DependencyInjectionComparison {
    private ServiceProvider _vmProvider = null!;
    private ServiceProvider _fvProvider = null!;

    /// <summary>
    /// Registered explicitly rather than from <c>GeneratedValidators.All</c>: this assembly also
    /// carries the DataAnnotations model set, which the front-end generates validators for too, and
    /// registering those would charge ValidationModules for types the comparison does not use.
    /// </summary>
    private static ValidatorRegistration[] Registrations() => [
        new(typeof(IValidatorFor<Customer>), static _ => CustomerValidator.Instance),
        new(typeof(IValidatorFor<Address>), static _ => AddressValidator.Instance),
        new(typeof(IValidatorFor<OrderLine>), static _ => OrderLineValidator.Instance),
        new(typeof(IValidatorFor<Order>), static _ => OrderValidator.Instance),
        new(typeof(IValidatorFor<Basket>), static _ => BasketValidator.Instance),
    ];

    [GlobalSetup]
    public void Setup() {
        var vmServices = new ServiceCollection();
        vmServices.AddValidationModules(Registrations());
        _vmProvider = vmServices.BuildServiceProvider();

        var fvServices = new ServiceCollection();
        fvServices.AddValidatorsFromAssemblyContaining<CustomerFluentValidator>();
        _fvProvider = fvServices.BuildServiceProvider();

        // Warm the first resolution out of the measured path in both containers: MS.DI compiles a
        // call site on first use, and measuring that once would say nothing about steady state.
        _ = _vmProvider.GetRequiredService<IValidatorFor<Customer>>();
        using var scope = _fvProvider.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<IValidator<Customer>>();
    }

    [GlobalCleanup]
    public void Cleanup() {
        _vmProvider.Dispose();
        _fvProvider.Dispose();
    }

    // ---- Startup ---------------------------------------------------------------------------------

    [Benchmark(Baseline = true, Description = "ValidationModules - register the generated table")]
    public ServiceProvider Vm_Register() {
        var services = new ServiceCollection();
        services.AddValidationModules(Registrations());

        return services.BuildServiceProvider();
    }

    [Benchmark(Description = "FluentValidation - AddValidatorsFromAssemblyContaining (scans)")]
    public ServiceProvider Fv_Register_Scanning() {
        var services = new ServiceCollection();
        services.AddValidatorsFromAssemblyContaining<CustomerFluentValidator>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The same registration written out by hand, which is what an AOT-facing FluentValidation
    /// codebase has to do instead of scanning. Against the benchmark above, the difference is the
    /// scan; against the ValidationModules one, what is left is the container doing the same work
    /// for both.
    /// </summary>
    [Benchmark(Description = "FluentValidation - explicit registration, no scan")]
    public ServiceProvider Fv_Register_Explicit() {
        var services = new ServiceCollection();
        services.AddScoped<IValidator<Customer>>(static _ => CustomerFluentValidator.Instance);
        services.AddScoped<IValidator<Address>>(static _ => AddressFluentValidator.Instance);
        services.AddScoped<IValidator<OrderLine>>(static _ => OrderLineFluentValidator.Instance);
        services.AddScoped<IValidator<Order>>(static _ => OrderFluentValidator.Instance);
        services.AddScoped<IValidator<Basket>>(static _ => BasketFluentValidator.Instance);

        return services.BuildServiceProvider();
    }

    // ---- Per request -----------------------------------------------------------------------------

    /// <summary>
    /// Singleton, because generated validators are stateless - so this is a lookup returning an
    /// instance that already exists.
    /// </summary>
    [Benchmark(Description = "ValidationModules - resolve IValidatorFor<T> (singleton)")]
    public IValidatorFor<Customer> Vm_Resolve() =>
        _vmProvider.GetRequiredService<IValidatorFor<Customer>>();

    /// <summary>
    /// Scoped, which is what <c>AddValidatorsFromAssemblyContaining</c> registers by default, so a
    /// scope has to exist for the resolution to be legal. The scope is part of the cost a request
    /// pays and is included deliberately.
    /// </summary>
    [Benchmark(Description = "FluentValidation - scope + resolve IValidator<T> (scoped)")]
    public IValidator<Customer> Fv_Resolve() {
        using var scope = _fvProvider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IValidator<Customer>>();
    }
}
