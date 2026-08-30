namespace ValidationModules.Constraints;

/// <summary>
/// The string must read as a phone number: after stripping every <c>'+'</c>, trailing whitespace,
/// and a trailing extension (<c>ext.</c>, <c>ext</c> or <c>x</c> followed by digits), it must
/// contain at least one digit and nothing but digits, whitespace and <c>- . ( )</c>. Emits code
/// <c>phone</c>.
/// </summary>
/// <remarks>
/// The name and the semantics are <c>System.ComponentModel.DataAnnotations</c>' own - see
/// <c>ConstraintChecks.IsPhone</c>, where the check is pinned against the BCL attribute - so
/// migrating a model is swapping a using directive.
/// </remarks>
/// <example>
/// <code>
/// [Phone] public string? Contact { get; init; }
/// </code>
/// </example>
public sealed class PhoneAttribute : ValidationConstraintAttribute;
