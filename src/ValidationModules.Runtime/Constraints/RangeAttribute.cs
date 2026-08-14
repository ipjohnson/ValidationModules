namespace ValidationModules.Constraints;

/// <summary>
/// The value must fall within bounds. Emits code <c>range</c>.
/// </summary>
/// <remarks>
/// The bounds are compile-time constants, parsed and type-checked by the generator against the
/// property's own type. The string overload exists for types with no constant form in metadata -
/// <c>decimal</c>, <c>DateTime</c>, <c>DateOnly</c>, <c>TimeSpan</c> - and is parsed invariantly
/// at generation time, so a malformed bound is a build error rather than a runtime one.
/// </remarks>
/// <example>
/// <code>
/// [Range(0, 30)]                                  public int Age { get; init; }
/// [Range(0.0, 1.0, ExclusiveMax = true)]          public double Ratio { get; init; }
/// [Range("2000-01-01", "2100-01-01")]             public DateOnly Effective { get; init; }
/// [Range(Min = 1)]                                public int Quantity { get; init; }
/// </code>
/// </example>
public sealed class RangeAttribute : ValidationConstraintAttribute {

    /// <summary>
    /// Bounds set through <see cref="Min"/> and <see cref="Max"/>, for declaring only one of them.
    /// </summary>
    /// <remarks>
    /// An absent bound emits no comparison at all, rather than a comparison against the type's
    /// extreme. The difference is visible to a caller: the composed message becomes "must be at
    /// least 1" instead of naming an upper bound nobody declared.
    /// </remarks>
    public RangeAttribute() { }

    /// <summary>Integral bounds.</summary>
    public RangeAttribute(int min, int max) {
        Min = min;
        Max = max;
    }

    /// <summary>Long integral bounds.</summary>
    public RangeAttribute(long min, long max) {
        Min = min;
        Max = max;
    }

    /// <summary>Floating-point bounds.</summary>
    public RangeAttribute(double min, double max) {
        Min = min;
        Max = max;
    }

    /// <summary>
    /// Bounds for types with no constant form - decimal, DateTime, DateOnly, TimeSpan. Parsed
    /// invariantly against the property's type at generation time.
    /// </summary>
    public RangeAttribute(string min, string max) {
        Min = min;
        Max = max;
    }

    /// <summary>The lower bound, as written. Null leaves the value unbounded below.</summary>
    public object? Min { get; init; }

    /// <summary>The upper bound, as written. Null leaves the value unbounded above.</summary>
    public object? Max { get; init; }

    /// <summary>
    /// Treat <see cref="Min"/> as exclusive. OpenAPI's <c>exclusiveMinimum</c>.
    /// </summary>
    public bool ExclusiveMin { get; init; }

    /// <summary>
    /// Treat <see cref="Max"/> as exclusive. OpenAPI's <c>exclusiveMaximum</c>.
    /// </summary>
    public bool ExclusiveMax { get; init; }
}
