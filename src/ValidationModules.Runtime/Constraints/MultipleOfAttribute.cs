namespace ValidationModules.Constraints;

/// <summary>
/// The value must be an exact multiple of a divisor. Emits code <c>multiple_of</c>.
/// </summary>
/// <remarks>
/// <para>
/// OpenAPI's <c>multipleOf</c>. The divisor is a compile-time constant, parsed and type-checked by
/// the generator against the property's own type, and it must be greater than zero - a zero divisor
/// is a build error rather than a division by zero at run time.
/// </para>
/// <para>
/// <b>Floating-point members are checked in the decimal domain, not with <c>%</c>.</b> In binary
/// floating point <c>0.3 % 0.01</c> is 0.00999999999999998, and every one of 0.3, 1.05, 99.99 and
/// 1234.56 "fails" a naive check against <c>multipleOf: 0.01</c>. So a <c>double</c> or
/// <c>float</c> member converts to <c>decimal</c> first, which rounds to 15 significant digits and
/// cancels exactly the representation error the naive form trips over. Integral and <c>decimal</c>
/// members compile to a plain <c>%</c>, which is already exact for them.
/// </para>
/// <para>
/// The one case that has no answer is a floating-point value larger than <c>decimal</c> can hold,
/// around 7.9e28. Its spacing there is wider than any realistic divisor, so no divisor divides it
/// meaningfully; the check reports a failure rather than passing a value it cannot evaluate.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [MultipleOf(5)]
/// public int Quantity { get; init; }
///
/// [MultipleOf("0.05")]
/// public decimal Price { get; init; }
///
/// [MultipleOf(0.01)]
/// public double Ratio { get; init; }
/// </code>
/// </example>
public sealed class MultipleOfAttribute : ValidationConstraintAttribute {

    /// <summary>An integral divisor.</summary>
    public MultipleOfAttribute(int divisor) => Divisor = divisor;

    /// <summary>A long integral divisor.</summary>
    public MultipleOfAttribute(long divisor) => Divisor = divisor;

    /// <summary>A fractional divisor.</summary>
    public MultipleOfAttribute(double divisor) => Divisor = divisor;

    /// <summary>
    /// A divisor for a type with no constant form - the <c>decimal</c> case. Parsed invariantly at
    /// generation time, so a malformed divisor is a build error rather than a runtime one.
    /// </summary>
    public MultipleOfAttribute(string divisor) => Divisor = divisor;

    /// <summary>The divisor, as written. Must be greater than zero.</summary>
    public object Divisor { get; }
}
