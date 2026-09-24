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
public static class RangeBoundReader
{
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
    public static bool TryResolve(ITypeSymbol type, string literal, out string expression) =>
        TryResolve(type, literal, out expression, out _);

    /// <summary>
    /// Rewrites a bound against <paramref name="type"/>, and hands back the value it parsed.
    /// </summary>
    /// <param name="type">The member's type. Nullable wrappers are unwrapped.</param>
    /// <param name="literal">
    /// The bound as <see cref="NativeConstraintReader.Literal"/> rendered it.
    /// </param>
    /// <param name="expression">The C# expression to emit in the comparison and the message.</param>
    /// <param name="value">
    /// The bound's value, comparable with the other bound of the same member: a <c>decimal</c> for
    /// every numeric type, a <c>DateTime</c> for <c>DateTime</c> and <c>DateOnly</c>, a
    /// <c>TimeSpan</c> for <c>TimeSpan</c> and <c>TimeOnly</c>, and a <c>DateTimeOffset</c>. Null
    /// when the bound is an expression with no value here, such as <c>double.PositiveInfinity</c>
    /// or a number outside <c>decimal</c>'s range.
    /// </param>
    /// <returns>False when the bound was written as a string and does not parse as the type.</returns>
    public static bool TryResolve(
        ITypeSymbol type,
        string literal,
        out string expression,
        out IComparable? value
    )
    {
        expression = literal;
        value = null;

        var underlying = TypeFacts.IsNullableValueType(type)
            ? ((INamedTypeSymbol)type).TypeArguments[0]
            : type;

        var suffix = SuffixFor(underlying);

        // An unquoted bound arrives having lost its type. NativeConstraintReader renders an integral
        // constant with Convert.ToString, so a `long` bound of 0 is emitted as the text `0` - and C#
        // re-reads that literal by its *value*, making it `int`, while `4294967295` becomes `uint`.
        //
        // The comparison tolerates that and the report call does not, which is why it went unnoticed:
        //
        //     if (value.Limit < 0 || value.Limit > 4294967295)   // both widen to long - fine
        //         ctx.AddRange("limit", 0, 4294967295);          // AddRange<T>(T, T) - CS0411
        //
        // Inference does not widen. It needs one T for both arguments, and neither int nor uint
        // converts implicitly to the other. So a bound is re-emitted carrying the member's own type
        // rather than trusted to keep it, which is the same normalisation the quoted path below does
        // and for the same reason.
        if (!IsQuoted(literal))
        {
            return Retype(underlying, suffix, literal, out expression, out value);
        }

        var text = Unquote(literal);

        if (suffix is not null)
        {
            return IsIntegral(underlying)
                ? Integral(text, suffix, out expression, out value)
                : Numeric(text, suffix, out expression, out value);
        }

        switch (underlying.SpecialType)
        {
            case SpecialType.System_DateTime:
                return DateTimeBound(text, out expression, out value);
        }

        switch (underlying.ToDisplayString())
        {
            case "System.DateOnly":
                return DateOnly(text, out expression, out value);

            case "System.TimeOnly":
                return TimeOnly(text, out expression, out value);

            case "System.TimeSpan":
                return TimeSpanBound(text, out expression, out value);

            case "System.DateTimeOffset":
                return DateTimeOffsetBound(text, out expression, out value);
        }

        return false;
    }

    /// <summary>
    /// The literal suffix that pins a bound to <paramref name="underlying"/>, or null when the
    /// member is not a numeric type and there is nothing to pin it to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every numeric type gets one that makes its bounds unambiguous, because the point is that both
    /// bounds of a range agree with each other - and a bound is resolved one at a time, with no
    /// sight of its sibling, so agreement has to come from the member rather than from comparing the
    /// two. <c>u</c>, <c>L</c> and <c>UL</c> are what stop a pair straddling int/uint or long/ulong.
    /// </para>
    /// <para>
    /// The int family takes no suffix: every value those types can hold is already an <c>int</c>
    /// literal, so the pair agrees without one and the emitted text stays as it reads in the source.
    /// </para>
    /// </remarks>
    private static string? SuffixFor(ITypeSymbol underlying)
    {
        switch (underlying.SpecialType)
        {
            case SpecialType.System_Decimal:
                return "m";

            case SpecialType.System_Double:
                return "d";

            case SpecialType.System_Single:
                return "f";

            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
                return string.Empty;

            case SpecialType.System_UInt32:
                return "u";

            case SpecialType.System_Int64:
                return "L";

            case SpecialType.System_UInt64:
                return "UL";

            default:
                return null;
        }
    }

    private static bool IsIntegral(ITypeSymbol underlying)
    {
        switch (underlying.SpecialType)
        {
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Re-emits an unquoted numeric bound carrying <paramref name="suffix"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parsed rather than suffixed textually: a bound outside the target's range, or one written as
    /// <c>double.PositiveInfinity</c>, has no form in that type at all. For <c>decimal</c> that has
    /// to be reported - C# has no implicit conversion from double or float, so
    /// <c>[Range(0.5, 9.99)] decimal Price</c> emitting <c>price &lt; 0.5</c> failed the consumer's
    /// build on CS0019 - and returning false routes it to VM1103 instead.
    /// </para>
    /// <para>
    /// Everywhere else an unparseable bound is left exactly as written, which is what it was before
    /// and still compiles: <c>double.PositiveInfinity</c> is already an expression of the member's
    /// type. A fractional bound on an integral member is left alone for a blunter reason - there is
    /// no such thing as <c>0.5L</c>.
    /// </para>
    /// </remarks>
    private static bool Retype(
        ITypeSymbol underlying,
        string? suffix,
        string literal,
        out string expression,
        out IComparable? value
    )
    {
        expression = literal;
        value = null;

        if (suffix is null)
        {
            return true;
        }

        var text = literal.TrimEnd('d', 'D', 'f', 'F', 'm', 'M', 'l', 'L', 'u', 'U');

        if (
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        )
        {
            value = parsed;
        }

        if (IsIntegral(underlying))
        {
            if (
                long.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var whole
                )
            )
            {
                expression = whole.ToString(CultureInfo.InvariantCulture) + suffix;
            }

            return true;
        }

        if (value is null)
        {
            return underlying.SpecialType != SpecialType.System_Decimal;
        }

        expression = parsed.ToString(CultureInfo.InvariantCulture) + suffix;
        return true;
    }

    internal static bool IsQuoted(string literal) =>
        literal.Length >= 2 && literal[0] == '"' && literal[literal.Length - 1] == '"';

    internal static string Unquote(string literal) =>
        literal.Substring(1, literal.Length - 2).Replace("\\\\", "\\").Replace("\\\"", "\"");

    private static bool Numeric(
        string text,
        string suffix,
        out string expression,
        out IComparable? value
    )
    {
        expression = string.Empty;
        value = null;

        if (
            !decimal.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed
            )
        )
        {
            return false;
        }

        expression = parsed.ToString(CultureInfo.InvariantCulture) + suffix;
        value = parsed;
        return true;
    }

    private static bool Integral(
        string text,
        string suffix,
        out string expression,
        out IComparable? value
    )
    {
        expression = string.Empty;
        value = null;

        if (
            !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        )
        {
            return false;
        }

        expression = parsed.ToString(CultureInfo.InvariantCulture) + suffix;
        value = (decimal)parsed;
        return true;
    }

    /// <summary>
    /// Parses a date without reference to the build machine's zone.
    /// </summary>
    /// <remarks>
    /// <c>AdjustToUniversal</c>, because <c>None</c> converts a bound written with <c>Z</c> or an
    /// offset to the machine's local clock, and one commit would then build different validators on
    /// machines in different zones. With it, such a bound keeps its instant and comes back in UTC
    /// with <see cref="DateTimeKind.Utc"/>, and a bound written without a zone is left as written,
    /// with <see cref="DateTimeKind.Unspecified"/>.
    /// </remarks>
    private static bool TryDate(string text, out DateTime value) =>
        DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal,
            out value
        );

    private static bool DateTimeBound(string text, out string expression, out IComparable? value)
    {
        expression = string.Empty;
        value = null;

        if (!TryDate(text, out var parsed))
        {
            return false;
        }

        // A bound written "2000-01-01" carries no zone and stays Unspecified, where anchoring it to
        // any zone would make the same source mean two things. One written with a zone is an
        // instant, which TryDate has already put in UTC.
        var kind = parsed.Kind == DateTimeKind.Utc ? "Utc" : "Unspecified";

        expression =
            $"new global::System.DateTime({parsed.Year}, {parsed.Month}, {parsed.Day}, "
            + $"{parsed.Hour}, {parsed.Minute}, {parsed.Second}, {parsed.Millisecond}, "
            + $"global::System.DateTimeKind.{kind})";
        value = parsed;

        return true;
    }

    private static bool DateOnly(string text, out string expression, out IComparable? value)
    {
        expression = string.Empty;
        value = null;

        if (!TryDate(text, out var parsed))
        {
            return false;
        }

        expression = $"new global::System.DateOnly({parsed.Year}, {parsed.Month}, {parsed.Day})";
        value = parsed.Date;
        return true;
    }

    private static bool TimeOnly(string text, out string expression, out IComparable? value)
    {
        expression = string.Empty;
        value = null;

        if (
            !TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed)
            || parsed < TimeSpan.Zero
            || parsed.Days > 0
        )
        {
            return false;
        }

        expression =
            $"new global::System.TimeOnly({parsed.Hours}, {parsed.Minutes}, {parsed.Seconds}, {parsed.Milliseconds})";
        value = parsed;

        return true;
    }

    private static bool TimeSpanBound(string text, out string expression, out IComparable? value)
    {
        expression = string.Empty;
        value = null;

        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        expression =
            $"new global::System.TimeSpan({parsed.Days}, {parsed.Hours}, {parsed.Minutes}, "
            + $"{parsed.Seconds}, {parsed.Milliseconds})";
        value = parsed;

        return true;
    }

    /// <summary>A <c>DateTimeOffset</c> bound, read as UTC when it is written without an offset.</summary>
    /// <remarks>
    /// <c>AssumeUniversal</c>, because <c>None</c> gives a bound written without an offset the
    /// build machine's offset for that date, and one commit would then build different validators
    /// on machines in different zones. A bound written with an offset keeps it.
    /// </remarks>
    private static bool DateTimeOffsetBound(
        string text,
        out string expression,
        out IComparable? value
    )
    {
        expression = string.Empty;
        value = null;

        if (
            !DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed
            )
        )
        {
            return false;
        }

        expression =
            $"new global::System.DateTimeOffset({parsed.Year}, {parsed.Month}, {parsed.Day}, "
            + $"{parsed.Hour}, {parsed.Minute}, {parsed.Second}, {parsed.Millisecond}, "
            + $"new global::System.TimeSpan({parsed.Offset.Hours}, {parsed.Offset.Minutes}, 0))";
        value = parsed;

        return true;
    }
}
