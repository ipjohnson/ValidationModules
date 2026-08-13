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
/// See API-SURFACE.md §19.6 and §19.7.
/// </param>
public sealed record ValidatedTypeModel(
    string Namespace,
    string TypeName,
    string QualifiedTypeName,
    string ValidatorName,
    EquatableArray<ValidatedPropertyModel> Properties,
    EquatableArray<string> AppliedRules = default) : IEquatable<ValidatedTypeModel>;

/// <summary>Which registration shape the assembly gets. See plan §7.3.</summary>
public enum RegistrationMode {
    /// <summary>Emit a complete IDependencyModule.</summary>
    DependencyModules,

    /// <summary>Emit a static table of factories plus AddValidationModules.</summary>
    ServiceCollection,

    /// <summary>Emit neither; the consumer wires validators up themselves.</summary>
    None,
}
