namespace ValidationModules;

/// <summary>
/// One entry in a table of validators to register.
/// </summary>
/// <remarks>
/// <para>
/// A factory delegate rather than a (service, implementation) type pair, so that registration never
/// goes through <c>ActivatorUtilities</c> constructor reflection.
/// </para>
/// <para>
/// Nothing generates one of these any more - the generator emits an <c>IServiceCollection</c>
/// extension instead, for the reasons in <c>RegistrationEmitter</c>'s remarks. This and
/// <see cref="Microsoft.Extensions.DependencyInjection.ValidationModulesServiceCollectionExtensions.AddValidationModules"/>
/// remain for anyone registering from a table they build themselves.
/// </para>
/// </remarks>
/// <param name="ServiceType">The closed service type, e.g. <c>IValidatorFor&lt;Pet&gt;</c>.</param>
/// <param name="Factory">Produces the instance.</param>
public readonly record struct ValidatorRegistration(
    Type ServiceType,
    Func<IServiceProvider, object> Factory);
