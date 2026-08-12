namespace ValidationModules;

/// <summary>
/// One entry in the table a generated assembly emits when DependencyModules is not referenced.
/// </summary>
/// <remarks>
/// A factory delegate rather than a (service, implementation) type pair, so that registration never
/// goes through <c>ActivatorUtilities</c> constructor reflection. Generated validators are
/// parameterless singletons, so in practice the factory closes over a static <c>Instance</c> field.
/// </remarks>
/// <param name="ServiceType">The closed service type, e.g. <c>IValidatorFor&lt;Pet&gt;</c>.</param>
/// <param name="Factory">Produces the instance. Usually returns a static singleton.</param>
/// <param name="Profile">The profile this validator implements, or null for the default.</param>
public readonly record struct ValidatorRegistration(
    Type ServiceType,
    Func<IServiceProvider, object> Factory,
    Type? Profile = null);
