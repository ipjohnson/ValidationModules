namespace ValidationModules;

/// <summary>
/// Structural validation for <typeparamref name="T"/>: the constraints declared on the type,
/// flattened into straight-line code by the source generator.
/// </summary>
/// <remarks>
/// <para>
/// Generated implementations are stateless, hold no dependencies, and are registered as
/// singletons. A pass that finds nothing allocates nothing.
/// </para>
/// <para>
/// The name is <c>IValidatorFor&lt;T&gt;</c> rather than <c>IValidator&lt;T&gt;</c> because
/// FluentValidation owns the latter, and any codebase using the adapter will have both namespaces
/// imported.
/// </para>
/// </remarks>
/// <typeparam name="T">The type being validated.</typeparam>
public interface IValidatorFor<in T> {

    /// <summary>
    /// Validates <paramref name="value"/>, adding any failures to <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Errors are added in declaration order and every constraint is evaluated - there is no
    /// first-failure exit. The one exception is <c>[Required]</c>, which suppresses the remaining
    /// constraints on the same field.
    /// </remarks>
    /// <param name="context">Accumulates failures and carries the current field path.</param>
    /// <param name="value">The value to validate.</param>
    void Validate(ref ValidationContext context, T value);
}
