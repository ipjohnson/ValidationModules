namespace ValidationModules.Constraints;

/// <summary>
/// The string must be well-formed Base64, as <c>Convert.FromBase64String</c> reads it, whitespace
/// included. Emits code <c>base64</c>.
/// </summary>
/// <remarks>
/// The name and the semantics are <c>System.ComponentModel.DataAnnotations</c>' own - see
/// <c>ConstraintChecks.IsBase64</c> - so migrating a model is swapping a using directive.
/// <c>Base64String</c> rather than <c>Base64</c>, because a name that is almost the
/// DataAnnotations name is a trap.
/// </remarks>
/// <example>
/// <code>
/// [Base64String] public string? Signature { get; init; }
/// </code>
/// </example>
public sealed class Base64StringAttribute : ValidationConstraintAttribute;
