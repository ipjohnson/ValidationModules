namespace ValidationModules.Constraints;

/// <summary>
/// The value must be present. Emits code <c>required</c>.
/// </summary>
/// <remarks>
/// <para>
/// A failed <c>[Required]</c> suppresses every other constraint on the same field, so a null value
/// produces one error rather than one per rule. The generator emits this as an <c>else if</c> chain
/// rather than leaving it to each rule to early-return on the wrong type.
/// </para>
/// <para>
/// On a string, whitespace-only counts as missing unless <see cref="AllowEmptyStrings"/> is set.
/// That matches DataAnnotations, which trims before testing, and matches what Hardened's
/// <c>RequiredRule</c> already does - so neither incumbent's users are surprised.
/// </para>
/// </remarks>
public sealed class RequiredAttribute : ValidationConstraintAttribute {

    /// <summary>
    /// When true, only null fails - an empty or whitespace-only string passes.
    /// </summary>
    public bool AllowEmptyStrings { get; init; }
}
