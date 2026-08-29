namespace ValidationModules.SourceGenerator.Impl;

/// <summary>
/// Metadata names the front-ends match on. Kept as strings and resolved through the compilation
/// rather than as <c>typeof</c>, because the generator targets netstandard2.0 and must not
/// reference the runtime it is generating code against - the consumer's version of that runtime is
/// the one that matters, not ours.
/// </summary>
public static class KnownTypes {
    public const string ConstraintsNamespace = "ValidationModules.Constraints";
    public const string DataAnnotationsNamespace = "System.ComponentModel.DataAnnotations";

    public const string GenerateValidatorAttribute = "ValidationModules.Constraints.GenerateValidatorAttribute";
    public const string CustomConstraintAttribute = "ValidationModules.Constraints.CustomConstraintAttribute";
    public const string ValidationAttribute = "System.ComponentModel.DataAnnotations.ValidationAttribute";
    public const string ValidatableObject = "System.ComponentModel.DataAnnotations.IValidatableObject";

    public const string JsonPropertyName = "System.Text.Json.Serialization.JsonPropertyNameAttribute";
    public const string DisplayAttribute = "System.ComponentModel.DataAnnotations.DisplayAttribute";

    /// <summary>Presence of this decides which registration branch is emitted. See plan §7.3.</summary>
    public const string DependencyModule = "DependencyModules.Runtime.Interfaces.IDependencyModule";


    /// <summary>Assembly-level, and part of the profile feature that is not built. See VM0019.</summary>

    /// <summary>The marker for a declarative rule class. See API-SURFACE.md §19.2.</summary>
    public const string ValidationRulesForInterface = "ValidationModules.IValidationRulesFor<T>";

}
