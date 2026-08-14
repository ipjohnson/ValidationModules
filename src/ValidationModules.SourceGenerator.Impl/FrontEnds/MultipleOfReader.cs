using System.Globalization;
using Microsoft.CodeAnalysis;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// Turns a <c>[MultipleOf]</c> divisor into an expression of the denomination the check runs in.
/// </summary>
/// <remarks>
/// <para>
/// The denomination is not always the member's own type, which is why this is not
/// <see cref="RangeBoundReader"/>. An integral member divides with <c>%</c> against an integral
/// literal. A <c>decimal</c> member does the same against a decimal literal. A <c>double</c> or
/// <c>float</c> member cannot use <c>%</c> at all - <c>0.3 % 0.01</c> is 0.00999999999999998 - so
/// its check converts to <c>decimal</c> first, and the divisor is emitted as a decimal literal even
/// though the value being tested is not one.
/// </para>
/// <para>
/// Everything is parsed at generation time, so a divisor that is malformed, negative, zero, or
/// fractional against an integral member is a build error with the property attached rather than
/// something that surfaces from generated code.
/// </para>
/// </remarks>
public static class MultipleOfReader {

    /// <summary>
    /// Rewrites a divisor against <paramref name="type"/>.
    /// </summary>
    /// <param name="type">The member's type. Nullable wrappers are unwrapped.</param>
    /// <param name="literal">
    /// The divisor as <see cref="NativeConstraintReader.Literal"/> rendered it, so the string form
    /// arrives quoted and a numeric one does not.
    /// </param>
    /// <param name="expression">The C# expression to emit as the divisor.</param>
    /// <param name="value">The parsed divisor, for the greater-than-zero check.</param>
    /// <param name="decimalDomain">True when the check has to leave binary floating point.</param>
    /// <returns>False when the divisor has no form the member's type can be checked against.</returns>
    public static bool TryResolve(
        ITypeSymbol type, string literal, out string expression, out decimal value, out bool decimalDomain) {

        expression = string.Empty;
        value = 0m;
        decimalDomain = false;

        var text = RangeBoundReader.IsQuoted(literal)
            ? RangeBoundReader.Unquote(literal)
            : literal.TrimEnd('d', 'D', 'f', 'F', 'm', 'M');

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) {
            return false;
        }

        var underlying = TypeFacts.IsNullableValueType(type)
            ? ((INamedTypeSymbol)type).TypeArguments[0]
            : type;

        switch (underlying.SpecialType) {
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
                return Integral(value, string.Empty, out expression);

            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return Integral(value, "L", out expression);

            case SpecialType.System_Decimal:
                expression = value.ToString(CultureInfo.InvariantCulture) + "m";
                return true;

            case SpecialType.System_Double:
            case SpecialType.System_Single:
                expression = value.ToString(CultureInfo.InvariantCulture) + "m";
                decimalDomain = true;
                return true;
        }

        return false;
    }

    /// <summary>Whether a member's type can carry a <c>[MultipleOf]</c> at all.</summary>
    public static bool IsSupported(ITypeSymbol type) {
        var underlying = TypeFacts.IsNullableValueType(type)
            ? ((INamedTypeSymbol)type).TypeArguments[0]
            : type;

        switch (underlying.SpecialType) {
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Decimal:
            case SpecialType.System_Double:
            case SpecialType.System_Single:
                return true;
        }

        return false;
    }

    /// <summary>
    /// A divisor for an integral member, which has to be a whole number: <c>[MultipleOf("2.5")]</c>
    /// on an <c>int</c> would emit <c>value % 2</c> if the fraction were dropped silently.
    /// </summary>
    private static bool Integral(decimal value, string suffix, out string expression) {
        expression = string.Empty;

        if (decimal.Truncate(value) != value) {
            return false;
        }

        expression = value.ToString("0", CultureInfo.InvariantCulture) + suffix;
        return true;
    }
}
