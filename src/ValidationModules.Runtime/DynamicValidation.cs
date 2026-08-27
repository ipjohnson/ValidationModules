namespace ValidationModules;

/// <summary>
/// The descent generated code writes for <c>Polymorphism.Runtime</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no fallback, and that is the design.</b> Not to the compile-time switch, not to the
/// declared type. A validator that behaved one way with a container and another way without one
/// would be a context-dependent silent change - the same class of problem as coverage that depends
/// on assembly layout. If it is Runtime, it is Runtime all the way, and a missing provider throws
/// naming the property and the fix.
/// </para>
/// <para>
/// Called once per descent, never per rule. Generated code resolves through here and calls the
/// adapter; it does not reach for <c>GetService</c> at a constraint site.
/// </para>
/// </remarks>
public static class DynamicValidation {

    /// <summary>Validates <paramref name="value"/> with a validator for its runtime type.</summary>
    /// <param name="context">The descent's context, already pushed to the property's path.</param>
    /// <param name="value">The nested value.</param>
    /// <param name="property">The property being descended into, for the exception message.</param>
    /// <param name="owner">The type declaring that property, for the exception message.</param>
    public static ValidationFlow Validate(ref ValidationContext context, object value, string property, string owner) =>
        Resolve(context.Services, value, property, owner).Validate(ref context, value);

    /// <summary>The boolean form, for a caller that already holds the services.</summary>
    public static bool IsValid(IServiceProvider? services, object value, string property, string owner) =>
        Resolve(services, value, property, owner).IsValid(value);

    private static IDynamicValidator Resolve(
        IServiceProvider? services, object value, string property, string owner) {

        ArgumentNullException.ThrowIfNull(value);

        if (services is null) {
            throw new InvalidOperationException(
                $"'{property}' on {owner} is declared [ValidateNested(Polymorphism.Runtime)], which resolves a " +
                "validator for the value's runtime type from the container - but this validation pass carries no " +
                "services. Validate through ValidationRunner<T> resolved from the container, or construct the " +
                "ValidationErrorCollector with an IServiceProvider. Polymorphism.CompileTime needs no container.");
        }

        if (services.GetService(typeof(DynamicValidatorRegistry)) is not DynamicValidatorRegistry registry) {
            throw new InvalidOperationException(
                $"'{property}' on {owner} needs DynamicValidatorRegistry, which no registered assembly has added. " +
                "Call the generated Add<Assembly>Validators() extension at the composition root.");
        }

        var type = value.GetType();

        // An adapter is emitted for every validated type, including ones with nothing of their own
        // to check, so a miss cannot mean "nothing to validate" - only that the assembly declaring
        // this type never registered.
        return registry.Find(type)
            ?? throw new InvalidOperationException(
                $"No validator is registered for {type}, reached through '{property}' on {owner}. The assembly " +
                $"declaring {type} registers one through its generated Add<Assembly>Validators() extension; call " +
                "it at the composition root.");
    }
}
