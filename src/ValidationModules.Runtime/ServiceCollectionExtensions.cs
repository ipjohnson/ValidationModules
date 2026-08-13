using Microsoft.Extensions.DependencyInjection.Extensions;
using ValidationModules;
using ValidationModules.Naming;

// ReSharper disable once CheckNamespace - MS convention: DI extensions live in the DI namespace
// so that a consumer who has already imported it finds them without a second using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers generated validators when DependencyModules is not in play.
/// </summary>
/// <remarks>
/// When DependencyModules <i>is</i> referenced, the generator emits a complete
/// <c>IDependencyModule</c> instead and this is not needed. Which branch is taken is decided at
/// generation time and can be forced with the <c>ValidationModules_Registration</c> MSBuild
/// property.
/// </remarks>
public static class ValidationModulesServiceCollectionExtensions {

    /// <summary>
    /// Registers every validator in a generated table, plus the field namer.
    /// </summary>
    /// <remarks>
    /// Each entry registers through its factory delegate, so nothing goes through
    /// <c>ActivatorUtilities</c> constructor reflection. Validators are singletons: generated ones
    /// are stateless, and building the rule graph once rather than per call is a hard requirement.
    /// </remarks>
    /// <param name="services">The collection to add to.</param>
    /// <param name="registrations">Typically <c>GeneratedValidators.All</c> from the consuming assembly.</param>
    public static IServiceCollection AddValidationModules(
        this IServiceCollection services,
        IReadOnlyList<ValidatorRegistration> registrations) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registrations);

        for (var i = 0; i < registrations.Count; i++) {
            var registration = registrations[i];

            services.Add(new ServiceDescriptor(
                registration.ServiceType,
                registration.Factory,
                ServiceLifetime.Singleton));
        }

        // TryAdd so that a consumer who registered their own naming policy before calling this
        // keeps it. The adapter resolves this to stay in step with the baked-in literals.
        services.TryAddSingleton<IValidationFieldNamer>(CamelCaseFieldNamer.Instance);

        return services;
    }

    /// <summary>
    /// Registers a <see cref="ValidationRunner{T}"/> for one validated type.
    /// </summary>
    /// <remarks>
    /// Closed rather than open generic, deliberately: <c>AddScoped(typeof(ValidationRunner&lt;&gt;))</c>
    /// would have MS.DI construct it reflectively. The generator emits one call per validated type.
    /// </remarks>
    public static IServiceCollection AddValidationRunner<T>(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ValidationRunner<T>>();

        return services;
    }

    /// <summary>
    /// Registers a rules class to be run by <see cref="DescribedValidator{T}"/>, for when the source
    /// generator has not compiled it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entry point for the generator-less path: a rules class emitted by another generator, or
    /// hand-written in an assembly this package's generator does not run over. Where our generator
    /// <i>did</i> compile the class it also registered the validator it emitted, and calling this as
    /// well registers a second, slower validator for the same type - <see cref="ValidationRunner{T}"/>
    /// merges every registered <see cref="IValidatorFor{T}"/>, so every error would appear twice.
    /// Within one compilation that is VM0074.
    /// </para>
    /// <para>
    /// Singleton, so <see cref="IValidationRulesFor{T}.Describe"/> runs once per process. The
    /// factory resolves <see cref="IValidatorProvider"/> optionally, because only a declaration that
    /// descends into a nested type needs one.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The validated type.</typeparam>
    /// <typeparam name="TRules">The rules class.</typeparam>
    public static IServiceCollection AddDescribedValidator<T, TRules>(this IServiceCollection services)
        where TRules : IValidationRulesFor<T>, new() {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IValidatorFor<T>>(provider => new DescribedValidator<T>(
            new TRules(),
            provider.GetService<IValidatorProvider>(),
            provider.GetService<IValidationFieldNamer>()));

        services.TryAddSingleton<IValidationFieldNamer>(CamelCaseFieldNamer.Instance);

        return services;
    }
}
