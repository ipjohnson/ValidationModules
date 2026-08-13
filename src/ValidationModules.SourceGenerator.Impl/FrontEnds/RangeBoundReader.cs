using System.Globalization;
using Microsoft.CodeAnalysis;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// Turns a <c>[Range]</c> bound written as a string into an expression of the member's own type.
/// </summary>
/// <remarks>
/// <para>
/// <c>RangeAttribute</c>'s string overload exists for types with no constant form in metadata -
/// <c>decimal</c>, <c>DateTime</c>, <c>DateOnly</c>, <c>TimeSpan</c> - and its documentation
/// promises the bound is "parsed invariantly at generation time, so a malformed bound is a build
/// error rather than a runtime one". This is where that happens. Without it the bound reaches the
/// emitter still quoted and lands in a comparison as <c>value.Born &lt; "2000-01-01"</c>, which does
/// not compile - putting the error inside generated code, which plan §7.5 names as the worst place
/// for one to surface.
/// </para>
/// <para>
/// <b>Constructor calls rather than a parse at run time.</b> <c>DateOnly.Parse("2000-01-01")</c>
/// would be shorter to emit and would move the cost to every validation and the failure to run time.
/// A constructor is evaluated once into a static field by the emitter's own arrangement, reads as
/// what it is, and cannot throw.
/// </para>
/// <para>
/// <b>This project targets netstandard2.0, so <c>DateOnly</c> and <c>TimeOnly</c> do not exist
/// here.</b> They are parsed through <c>DateTime</c> and <c>TimeSpan</c> respectively and emitted as
/// text; nothing constructs one at generation time. Getting this wrong is not subtle - the generator
/// simply fails to compile - but it is the reason the two look different from the rest.
/// </para>
/// </remarks>
public static class RangeBoundReader {

    /// <summary>
    /// Rewrites a bound against <paramref name="type"/>, or reports that it does not parse.
    /// </summary>
    /// <param name="type">The member's type. Nullable wrappers are unwrapped.</param>
    /// <param name="literal">
    /// The bound as <see cref="NativeConstraintReader.Literal"/> rendered it, so a string bound
    /// arrives quoted and a numeric one does not.
    /// </param>
    /// <param name="expression">The C# expression to emit in the comparison and the message.</param>
    /// <returns>False when the bound was written as a string and does not parse as the type.</returns>
    public static bool TryResolve(ITypeSymbol type, string literal, out string expression) {
        expression = literal;

        // Only the string form needs resolving. A numeric literal already has a type the comparison
        // accepts, including int bounds against a decimal or a double member.
        if (!IsQuoted(literal)) {
            return true;
        }

        var text = Unquote(literal);
        var underlying = TypeFacts.IsNullableValueType(type)
            ? ((INamedTypeSymbol)type).TypeArguments[0]
            : type;

        switch (underlying.SpecialType) {
            case SpecialType.System_Decimal:
                return Numeric(text, "m", out expression);

            case SpecialType.System_Double:
                return Numeric(text, string.Empty, out expression);

            case SpecialType.System_Single:
                return Numeric(text, "f", out expression);

            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
                return Integral(text, string.Empty, out expression);

            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return Integral(text, "L", out expression);

            case SpecialType.System_DateTime:
                return DateTimeBound(text, out expression);
        }

        switch (underlying.ToDisplayString()) {
            case "System.DateOnly":
                return DateOnly(text, out expression);

            case "System.TimeOnly":
                return TimeOnly(text, out expression);

            case "System.TimeSpan":
                return TimeSpanBound(text, out expression);

            case "System.DateTimeOffset":
                return DateTimeOffsetBound(text, out expression);
        }

        return false;
    }

    private static bool IsQuoted(string literal) =>
        literal.Length >= 2 && literal[0] == '"' && literal[literal.Length - 1] == '"';

    private static string Unquote(string literal) =>
        literal.Substring(1, literal.Length - 2).Replace("\\\\", "\\").Replace("\\\"", "\"");

    private static bool Numeric(string text, string suffix, out string expression) {
        expression = string.Empty;

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
            return false;
        }

        expression = value.ToString(CultureInfo.InvariantCulture) + suffix;
        return true;
    }

    private static bool Integral(string text, string suffix, out string expression) {
        expression = string.Empty;

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) {
            return false;
        }

        expression = value.ToString(CultureInfo.InvariantCulture) + suffix;
        return true;
    }

    private static bool TryDate(string text, out DateTime value) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static bool DateTimeBound(string text, out string expression) {
        expression = string.Empty;

        if (!TryDate(text, out var value)) {
            return false;
        }

        // Unspecified rather than the parsed Kind: a bound written "2000-01-01" carries no zone, and
        // silently anchoring it to the build machine's would make the same source mean two things.
        expression =
            $"new global::System.DateTime({value.Year}, {value.Month}, {value.Day}, " +
            $"{value.Hour}, {value.Minute}, {value.Second}, {value.Millisecond}, " +
            "global::System.DateTimeKind.Unspecified)";

        return true;
    }

    private static bool DateOnly(string text, out string expression) {
        expression = string.Empty;

        if (!TryDate(text, out var value)) {
            return false;
        }

        expression = $"new global::System.DateOnly({value.Year}, {value.Month}, {value.Day})";
        return true;
    }

    private static bool TimeOnly(string text, out string expression) {
        expression = string.Empty;

        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) ||
            value < TimeSpan.Zero || value.Days > 0) {
            return false;
        }

        expression =
            $"new global::System.TimeOnly({value.Hours}, {value.Minutes}, {value.Seconds}, {value.Milliseconds})";

        return true;
    }

    private static bool TimeSpanBound(string text, out string expression) {
        expression = string.Empty;

        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value)) {
            return false;
        }

        expression =
            $"new global::System.TimeSpan({value.Days}, {value.Hours}, {value.Minutes}, " +
            $"{value.Seconds}, {value.Milliseconds})";

        return true;
    }

    private static bool DateTimeOffsetBound(string text, out string expression) {
        expression = string.Empty;

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) {
            return false;
        }

        expression =
            $"new global::System.DateTimeOffset({value.Year}, {value.Month}, {value.Day}, " +
            $"{value.Hour}, {value.Minute}, {value.Second}, {value.Millisecond}, " +
            $"new global::System.TimeSpan({value.Offset.Hours}, {value.Offset.Minutes}, 0))";

        return true;
    }
}
