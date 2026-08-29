using Microsoft.Extensions.DependencyInjection.Extensions;
using ValidationModules;
using ValidationModules.Naming;

// ReSharper disable once CheckNamespace - MS convention: DI extensions live in the DI namespace
// so that a consumer who has already imported it finds them without a second using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The registration calls generated code makes, and the ones a consumer makes by hand.
/// </summary>
/// <remarks>
/// The generator emits an <c>IServiceCollection</c> extension named after the assembly -
/// <c>services.AddMyAppValidators()</c> - which registers each validator and calls
/// <see cref="AddValidationRunner{T}"/> once per validated type. When DependencyModules is
/// referenced it emits a module wrapping the same body instead; which branch is taken is decided at
/// generation time and can be forced with the <c>ValidationModules_Registration</c> MSBuild
/// property.
/// </remarks>
public static class ValidationModulesServiceCollectionExtensions {

    /// <summary>
    /// Registers every validator in a table, plus the field namer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each entry registers through its factory delegate, so nothing goes through
    /// <c>ActivatorUtilities</c> constructor reflection. Validators are singletons: generated ones
    /// are stateless, and building the rule graph once rather than per call is a hard requirement.
    /// </para>
    /// <para>
    /// <b>Nothing generates one of these tables.</b> The generator emits an extension method instead
    /// - see <c>RegistrationEmitter</c> for why the table lost - so this is for a caller assembling
    /// registrations themselves, which in practice means another generator built on
    /// <c>ValidationModules.SourceGenerator.Impl</c>.
    /// </para>
    /// </remarks>
    /// <param name="services">The collection to add to.</param>
    /// <param name="registrations">The validators to register.</param>
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
    /// <para>
    /// Closed rather than open generic, deliberately: <c>AddScoped(typeof(ValidationRunner&lt;&gt;))</c>
    /// would have MS.DI construct it reflectively. The generator emits one call per validated type.
    /// </para>
    /// <para>
    /// <b>An explicit factory rather than constructor injection, and this is load-bearing under
    /// Native AOT.</b> Letting MS.DI satisfy the constructor's two <c>IEnumerable&lt;&gt;</c>
    /// parameters routes through <c>CallSiteRuntimeResolver.VisitIEnumerable</c>, which builds the
    /// backing array with <c>Array.CreateInstance(Type, int)</c> - a reflective construction over a
    /// <see cref="Type"/> known only at run time. Nothing in a typical application ever mentions
    /// <c>IAsyncValidatorFor&lt;T&gt;[]</c> statically, because most types have no business rule and
    /// the generator registers none, so ILC never emits that array type and the resolve throws
    /// <see cref="NotSupportedException"/> at run time - after a publish that reported no warning.
    /// </para>
    /// <para>
    /// Naming both closed types in the calls below puts them in front of ILC at compile time, which
    /// is what makes the array available. Rooting them any other way does not work: both
    /// <c>Array.Empty&lt;IAsyncValidatorFor&lt;T&gt;&gt;()</c> and a read <c>static readonly</c>
    /// array field were tried and still threw, because they root the array *type* without the
    /// reflection metadata <c>Array.CreateInstance</c> needs.
    /// </para>
    /// <para>
    /// Composition is unchanged - <c>GetServices</c> returns every registration in order, exactly
    /// as constructor injection did.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddValidationRunner<T>(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(ServiceDescriptor.Scoped(static provider => new ValidationRunner<T>(
            provider.GetServices<IValidatorFor<T>>(),
            provider.GetServices<IAsyncValidatorFor<T>>(),
            // The scope's own provider, so a validation pass reaches request services rather than
            // root ones - and so a Polymorphism.Runtime descent has something to resolve through.
            provider)));

        return services;
    }

}
