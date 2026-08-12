namespace ValidationModules.Constraints;

/// <summary>
/// The value must be one of a fixed set. Emits code <c>enum</c>.
/// </summary>
/// <remarks>
/// The code is <c>enum</c> rather than <c>allowed_values</c> because that is the code Hardened
/// already puts on the wire for OpenAPI enum constraints, and renaming it would break existing API
/// consumers for cosmetics. Override it per rule with <see cref="ValidationConstraintAttribute.Code"/>.
/// </remarks>
/// <example>
/// <code>
/// [AllowedValues("available", "pending", "sold")] public string Status { get; init; }
/// </code>
/// </example>
public sealed class AllowedValuesAttribute : ValidationConstraintAttribute {

    /// <summary>
    /// The permitted values. Must be compile-time constants of the property's type.
    /// </summary>
    /// <param name="values">The permitted values.</param>
    public AllowedValuesAttribute(params object[] values) {
        ArgumentNullException.ThrowIfNull(values);

        Values = values;
    }

    /// <summary>The permitted values.</summary>
    public object[] Values { get; }

    /// <summary>
    /// How string values are compared. Ordinal by default: a status code is an identifier, not
    /// prose, and culture-sensitive comparison of one is a bug waiting for a Turkish locale.
    /// </summary>
    public StringComparison Comparison { get; init; } = StringComparison.Ordinal;
}
