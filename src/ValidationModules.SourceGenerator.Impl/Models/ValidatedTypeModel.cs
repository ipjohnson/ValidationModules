namespace ValidationModules.SourceGenerator.Impl.Models;

/// <summary>
/// The IR: one validated type, flattened to what the emitter needs and nothing else.
/// </summary>
/// <remarks>
/// Every front-end produces this and the emitter consumes only this, which is what keeps
/// attribute-declared, DataAnnotations-declared and spec-declared validators identical in field
/// paths, codes and error shapes. It holds no Roslyn symbols: symbols are not value-equatable and
/// keeping one alive across an incremental-generator boundary roots an entire compilation.
/// </remarks>
/// <param name="Namespace">Where the validator is emitted.</param>
/// <param name="TypeName">The validated type's simple name.</param>
/// <param name="QualifiedTypeName">Its fully qualified name, for the interface and parameter.</param>
/// <param name="ValidatorName">The generated class name.</param>
/// <param name="Properties">In source order.</param>
/// <param name="AppliedRules">
/// Hand-written rules attached by <c>rules.Apply(…)</c>, as fully qualified method names. They own
/// no property, so they run after every property has been walked, in declaration order.
/// </param>
/// <param name="IsPublic">
/// Whether the validated type is visible outside its assembly, which decides whether the emitted
/// validator is <c>public</c> or <c>internal</c>. A public validator over an internal type is
/// CS0051 - "parameter type is less accessible than method" - reported inside generated code, which
/// is the worst place for a consumer to meet it. Internal models are ordinary rather than exotic,
/// so this is not an edge case.
/// </param>
/// <param name="ImplementsValidatableObject">
/// Whether the emitted validator calls <c>IValidatableObject.Validate</c> - true only when the
/// type implements it <i>and</i> the DataAnnotations front end is on. Emitted last and gated on
/// nothing else having failed, which is <c>Validator.TryValidateObject</c>'s sequencing. A type
/// carrying it also loses the straight-line <c>IsValid</c>, for the reason applied rules do.
/// </param>
/// <param name="Regions">
/// The rules-class regions this validator calls, ordered by rules-class name (ordinal). Each is a
/// method in a companion file carrying the rules class's own using directives; the validator calls
/// it after the attribute-declared checks, passing the injected validator arrays its descents
/// need. A type with any region loses the straight-line <c>IsValid</c> - regions carry free-form
/// computation and reporter calls a boolean path with no collector cannot always project.
/// </param>
public sealed record ValidatedTypeModel(
    string Namespace,
    string TypeName,
    string QualifiedTypeName,
    string ValidatorName,
    EquatableArray<ValidatedPropertyModel> Properties,
    EquatableArray<string> AppliedRules = default,
    bool IsPublic = true,
    bool ImplementsValidatableObject = false,
    EquatableArray<RegionModel> Regions = default) : IEquatable<ValidatedTypeModel>;

/// <summary>
/// One transcribed rules-class region: where its method lives and which of the validator's
/// injected sets it takes, in parameter order.
/// </summary>
public sealed record RegionModel(
    string CompanionQualifiedName,
    string MethodName,
    EquatableArray<string> ValidatorAccessors = default) : IEquatable<RegionModel>;

/// <summary>Which registration shape the assembly gets. See plan §7.3.</summary>
public enum RegistrationMode {
    /// <summary>Emit a complete IDependencyModule.</summary>
    DependencyModules,

    /// <summary>Emit a static table of factories plus AddValidationModules.</summary>
    ServiceCollection,

    /// <summary>Emit neither; the consumer wires validators up themselves.</summary>
    None,
}
