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
    string? Condition = null) : IEquatable<ValidatedPropertyModel>;
