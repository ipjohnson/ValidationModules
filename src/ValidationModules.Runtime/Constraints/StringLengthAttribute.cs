namespace ValidationModules.Constraints;

/// <summary>
/// The string's length must fall within bounds. Emits code <c>string_length</c>.
/// </summary>
/// <example>
/// <code>
/// [StringLength(min: 1, max: 100)] public string Name { get; init; }
/// [StringLength(Max = 500)]        public string? Notes { get; init; }
/// </code>
/// </example>
public sealed class StringLengthAttribute : ValidationConstraintAttribute {

    /// <summary>
    /// Bounds set through <see cref="Min"/> and <see cref="Max"/>, for declaring only one of them.
    /// </summary>
    public StringLengthAttribute() { }

    /// <summary>
    /// Bounds set positionally.
    /// </summary>
    /// <param name="min">Shortest permitted length, inclusive.</param>
    /// <param name="max">Longest permitted length, inclusive.</param>
    public StringLengthAttribute(int min, int max) {
        Min = min;
        Max = max;
    }

    /// <summary>Shortest permitted length, inclusive. Zero means unbounded below.</summary>
    public int Min { get; init; }

    /// <summary>Longest permitted length, inclusive. Defaults to unbounded.</summary>
    public int Max { get; init; } = int.MaxValue;
}
