namespace ValidationModules.Constraints;

/// <summary>
/// Opts a type into validator generation when it carries no constraints of its own.
/// </summary>
/// <remarks>
/// Any constraint on any member already implies generation, so this is only needed when the rules
/// live in an overlay, when the type is purely a <see cref="ValidateNestedAttribute"/> target, or
/// when <c>IValidatorFor&lt;T&gt;</c> should be injectable regardless.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
public sealed class GenerateValidatorAttribute : Attribute {

    /// <summary>
    /// Restricts generation to these profiles. Null means every profile the type's rules mention.
    /// </summary>
    public Type[]? Profiles { get; init; }
}
