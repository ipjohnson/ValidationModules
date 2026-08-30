namespace ValidationModules.Constraints;

/// <summary>
/// The string's digits - dashes and spaces skipped - must pass the Luhn mod-10 checksum. Emits
/// code <c>credit_card</c>.
/// </summary>
/// <remarks>
/// The name and the semantics are <c>System.ComponentModel.DataAnnotations</c>' own - see
/// <c>ConstraintChecks.IsCreditCard</c>, where the check is pinned against the BCL attribute - so
/// migrating a model is swapping a using directive.
/// </remarks>
/// <example>
/// <code>
/// [CreditCard] public string? CardNumber { get; init; }
/// </code>
/// </example>
public sealed class CreditCardAttribute : ValidationConstraintAttribute;
