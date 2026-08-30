namespace ValidationModules.Constraints;

/// <summary>
/// The value must not be any of a fixed set. Emits code <c>enum</c>, the same code
/// <see cref="AllowedValuesAttribute"/> emits.
/// </summary>
/// <remarks>
/// The name is <c>System.ComponentModel.DataAnnotations</c>' own, so migrating a model is swapping
/// a using directive. It compiles as <see cref="AllowedValuesAttribute"/>'s negation rather than as
/// a kind of its own, which is also how the DataAnnotations bridge reads the BCL attribute - one
/// membership check, two directions, one code. Override the code per rule with
/// <see cref="ValidationConstraintAttribute.Code"/> when a client needs to tell the two apart.
/// </remarks>
/// <example>
/// <code>
/// [DeniedValues("admin", "root", "system")] public string? Username { get; init; }
/// </code>
/// </example>
public sealed class DeniedValuesAttribute : ValidationConstraintAttribute {

    /// <summary>
    /// The rejected values. Must be compile-time constants of the property's type.
    /// </summary>
    /// <param name="values">The rejected values.</param>
    public DeniedValuesAttribute(params object[] values) {
        ArgumentNullException.ThrowIfNull(values);

        Values = values;
    }

    /// <summary>The rejected values.</summary>
    public object[] Values { get; }
}
