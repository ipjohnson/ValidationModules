namespace ValidationModules.Constraints;

/// <summary>
/// Recurse into this property's value. On an object, validates the object; on a collection, each
/// element; on a dictionary, each value, pathed as <c>map[key]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Emits no code of its own - the errors are whatever the nested type's own constraints produce,
/// prefixed with this property's path.
/// </para>
/// <para>
/// The active profile propagates automatically: validating a <c>Pet</c> under V2 validates its
/// <c>Address</c> under V2, falling back to the aliased validator when V2 adds nothing to
/// <c>Address</c>.
/// </para>
/// <para>
/// Does not recurse into a value that failed <see cref="RequiredAttribute"/>.
/// </para>
/// </remarks>
public sealed class ValidateNestedAttribute : ValidationConstraintAttribute;
