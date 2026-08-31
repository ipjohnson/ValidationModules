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
    /// Bounds set positionally or by name; either may be omitted for a single-bound constraint.
    /// </summary>
    /// <remarks>
    /// The defaults are the unbounded sentinels the property form uses, so
    /// <c>[StringLength(min: 12)]</c> and <c>[StringLength(Min = 12)]</c> read identically.
    /// Before the defaults, the one-bound named call was CS7036 and the property form was the
    /// only way to give one bound - a papercut two models hit in consecutive trials. Note the
    /// vocabulary difference from DataAnnotations: positionally, the first bound here is
    /// <paramref name="min"/>, where <c>System.ComponentModel.DataAnnotations.StringLength(50)</c>
    /// is a maximum - prefer the named form when giving one bound.
    /// </remarks>
    /// <param name="min">Shortest permitted length, inclusive. Zero means unbounded below.</param>
    /// <param name="max">Longest permitted length, inclusive. Defaults to unbounded.</param>
    public StringLengthAttribute(int min = 0, int max = int.MaxValue) {
        Min = min;
        Max = max;
    }

    /// <summary>Shortest permitted length, inclusive. Zero means unbounded below.</summary>
    public int Min { get; init; }

    /// <summary>Longest permitted length, inclusive. Defaults to unbounded.</summary>
    public int Max { get; init; } = int.MaxValue;
}
