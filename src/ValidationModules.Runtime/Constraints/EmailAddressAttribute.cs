namespace ValidationModules.Constraints;

/// <summary>
/// The string must read as an email address: exactly one <c>'@'</c>, neither first nor last, and
/// no line breaks. Emits code <c>email</c>.
/// </summary>
/// <remarks>
/// <para>
/// The name and the semantics are <c>System.ComponentModel.DataAnnotations</c>' own, deliberately -
/// see <c>ConstraintChecks.IsEmail</c>, where the check is pinned against the BCL attribute.
/// <c>"a@b"</c> passes, because RFC 5322 permits a dotless domain and the BCL follows it. A rule
/// that wants a stricter grammar is a <see cref="PatternAttribute"/>.
/// </para>
/// <para>
/// Matching the DataAnnotations name means migrating a model is swapping a using directive, not
/// rewriting the attribute.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Required, EmailAddress] public string? Email { get; init; }
/// </code>
/// </example>
public sealed class EmailAddressAttribute : ValidationConstraintAttribute;
