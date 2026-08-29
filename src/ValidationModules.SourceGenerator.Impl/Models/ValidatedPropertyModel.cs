namespace ValidationModules.SourceGenerator.Impl.Models;

/// <summary>How the emitter has to walk a property.</summary>
public enum PropertyShape {
    /// <summary>A value with no structure to descend into.</summary>
    Scalar,

    /// <summary>An object with its own validator.</summary>
    Object,

    /// <summary>A collection whose elements have their own validator.</summary>
    Collection,

    /// <summary>A dictionary whose values have their own validator, pathed by key.</summary>
    Dictionary,
}

/// <summary>
/// One property of a validated type: where its errors are pathed, what shape it has, and what
/// constraints apply to it.
/// </summary>
/// <param name="PropertyName">The CLR name, used to read the value.</param>
/// <param name="FieldName">The wire name, baked into errors as a literal.</param>
/// <param name="TypeName">The fully qualified property type, for casts and nested calls.</param>
/// <param name="Shape">Whether the emitter descends into it.</param>
/// <param name="ElementTypeName">Collections only: the element type.</param>
/// <param name="ElementValidatorName">Collections and objects only: the validator to call.</param>
/// <param name="IsReferenceType">Whether a null check is needed before dereferencing.</param>
/// <param name="IsString">Whether string-specific constraints are legal on it.</param>
/// <param name="IsNullableValueType">Whether reading the value needs .Value.</param>
/// <param name="IsIndexable">Collections only: whether elements are reachable by index.</param>
/// <param name="CountAccessor">Collections only: Length for arrays, Count otherwise.</param>
/// <param name="ValidateNested">Whether [ValidateNested] was declared.</param>
/// <param name="Constraints">In evaluation order - Required first, then attribute order.</param>
/// <param name="Condition">
/// Guards the nested descent, from a <c>When</c> or <c>Unless</c> on <c>[ValidateNested]</c>. Same
/// shape as <see cref="ConstraintModel.Condition"/>: a complete boolean expression in terms of
/// <c>value</c>, negation already baked in, null when the descent is unconditional.
/// </param>
/// <param name="Polymorphism">How the descent treats subtypes of the nested type.</param>
/// <param name="Subtypes">
/// The subtypes a <see cref="PolymorphismMode.CompileTime"/> descent dispatches to, already sorted
/// most-derived first. Empty for every other mode.
/// </param>
/// <param name="DisplayName">
/// What DataAnnotations calls the member in a formatted message: <c>[Display(Name = …)]</c> when
/// present, otherwise the CLR name. Distinct from <see cref="FieldName"/>, which is the wire name
/// and additionally honours <c>[JsonPropertyName]</c> and the naming policy - a custom attribute's
/// <c>{0}</c> placeholder wants the DataAnnotations answer, resolved at build time so the runtime
/// never resolves it reflectively. Null when no constraint needs it.
/// </param>
/// <param name="NestedWalkInRegion">
/// The descent was declared only by a rules class, so the region's transcribed text owns the walk
/// and the attribute region must not emit a second one. The injected-validator machinery - the
/// field, the constructor parameter, the accessor the region call passes - is still this
/// property's, which is the reason the entry exists at all.
/// </param>
public sealed record ValidatedPropertyModel(
    string PropertyName,
    string FieldName,
    string TypeName,
    PropertyShape Shape,
    string? ElementTypeName,
    string? ElementValidatorName,
    bool IsReferenceType,
    bool IsString,
    bool IsNullableValueType,
    bool IsIndexable,
    string CountAccessor,
    bool ValidateNested,
    EquatableArray<ConstraintModel> Constraints,
    string? Condition = null,
    PolymorphismMode Polymorphism = PolymorphismMode.DeclaredOnly,
    EquatableArray<SubtypeModel> Subtypes = default,
    string? DisplayName = null,
    bool NestedWalkInRegion = false) : IEquatable<ValidatedPropertyModel>;
