namespace ValidationModules.Constraints;

/// <summary>
/// The string's length must fall within bounds. Emits code <c>string_length</c>.
/// </summary>
/// <remarks>
/// The one positional argument is the maximum, as it is on
/// <c>System.ComponentModel.DataAnnotations.StringLengthAttribute</c>, so <c>[StringLength(50)]</c>
/// means the same under either using directive. The minimum is <see cref="Min"/>, which
/// DataAnnotations calls <c>MinimumLength</c>.
/// </remarks>
/// <example>
/// <code>
/// [StringLength(100, Min = 1)] public string Name { get; init; }
/// [StringLength(500)]          public string? Notes { get; init; }
/// [StringLength(Min = 8)]      public string? Passphrase { get; init; }
/// </code>
/// </example>
public sealed class StringLengthAttribute : ValidationConstraintAttribute
{
    /// <summary>
    /// Bounds set through <see cref="Min"/> and <see cref="Max"/>, for a minimum with no maximum.
    /// </summary>
    public StringLengthAttribute() { }

    /// <summary>
    /// The maximum, with <see cref="Min"/> named beside it when there is a minimum as well.
    /// </summary>
    /// <remarks>
    /// There is no two-argument overload. The first argument used to be the minimum, and a
    /// <c>(min, max)</c> overload would keep that order working beside a one-argument form that
    /// now means the maximum. Without it, <c>[StringLength(3, 40)]</c> is a compile error whose
    /// rewrite is <c>[StringLength(40, Min = 3)]</c>.
    /// </remarks>
    /// <param name="max">Longest permitted length, inclusive.</param>
    public StringLengthAttribute(int max)
    {
        Max = max;
    }

    /// <summary>Shortest permitted length, inclusive. Zero means unbounded below.</summary>
    public int Min { get; init; }

    /// <summary>Longest permitted length, inclusive. Defaults to unbounded.</summary>
    public int Max { get; init; } = int.MaxValue;
}
