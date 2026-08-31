using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>[Range]</c> bounds whose two literals do not land on the same C# type.
/// </summary>
/// <remarks>
/// <para>
/// A bound's type is thrown away on the way out. <c>NativeConstraintReader.Scalar</c> renders an
/// integral constant with <c>Convert.ToString</c>, so a <c>long</c> bound of 0 is emitted as the
/// text <c>0</c> - and C# then re-types that literal by its <i>value</i>. <c>0</c> becomes
/// <c>int</c>; <c>4294967295</c> does not fit <c>int</c> and becomes <c>uint</c>.
/// </para>
/// <para>
/// <b>The comparison tolerates that and the report call does not</b>, which is why it survived
/// review. Each bound is emitted twice:
/// </para>
/// <code>
/// if (value.Limit &lt; 0 || value.Limit &gt; 4294967295)      // widens to long - fine
///     ctx.ReportRange("limit", 0, 4294967295);              // ReportRange&lt;T&gt;(T, T) - CS0411
/// </code>
/// <para>
/// Generic inference does not widen. It needs one <c>T</c> for both arguments, and neither
/// <c>int</c> nor <c>uint</c> converts implicitly to the other, so the call cannot be inferred.
/// Only pairs that straddle a type boundary bite: <c>[Range(0, 23)]</c> is two <c>int</c>s and is
/// fine, which is why this is rare rather than universal.
/// </para>
/// <para>
/// <see cref="ValidationContextExtensions.ReportRangeAtLeast{T}"/> and <c>ReportRangeAtMost</c> take a
/// single bound, so they have nothing to reconcile and are unaffected.
/// </para>
/// </remarks>
public class RangeBoundLiteralTypeTests {

    /// <summary>
    /// The reported case: 0 is int, 4294967295 is uint, and ReportRange needs them to agree.
    /// </summary>
    [Fact]
    public void Generate_BoundsStraddlingIntAndUInt_EmitsCompilableCode() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Limits {
                [Range(0, 4294967295)]
                public long IndexDiveLimit { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void Generate_BoundsStraddlingIntAndUInt_OnANullableMember_EmitsCompilableCode() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Limits {
                [Range(0, 4294967295)]
                public long? IndexDiveLimit { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// Every pair here has a lower bound C# types as <c>int</c> and an upper bound it does not:
    /// 2147483648 through 4294967295 is <c>uint</c>, above that is <c>long</c>. The int/long pairs
    /// already worked - int converts implicitly to long, so inference had a best common type - and
    /// are included so a fix cannot regress them.
    /// </summary>
    [Theory]
    [InlineData("0", "2147483648")]
    [InlineData("0", "4294967295")]
    [InlineData("1", "3000000000")]
    [InlineData("0", "5000000000")]
    [InlineData("0", "9223372036854775807")]
    [InlineData("-1", "4294967295")]
    public void Generate_MixedWidthIntegralBounds_EmitCompilableCode(string min, string max) {
        var result = GeneratorHarness.Run($$"""
            using ValidationModules.Constraints;

            namespace Api;

            public record Limits {
                [Range({{min}}, {{max}})]
                public long Value { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// The same straddle reaching a member whose own type is not integral at all. The comparison
    /// widens both bounds to double happily; the report call still has to pick one T.
    /// </summary>
    [Fact]
    public void Generate_MixedWidthBoundsOnADoubleMember_EmitsCompilableCode() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Limits {
                [Range(0, 4294967295)]
                public double Ratio { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// The ordinary case, which must keep working and must keep reading as it did.
    /// </summary>
    [Fact]
    public void Generate_TwoIntBounds_AreUnchanged() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Limits {
                [Range(0, 23)]
                public int Hour { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("new global::ValidationModules.ValidationMessageInfo(global::ValidationModules.ValidationMessageTemplates.RangeBetween, 0, 23)", result.Sources["Api.LimitsValidator.g.cs"]);
    }

    /// <summary>
    /// Both bounds carry the member's width, so the pair agrees regardless of what each literal
    /// would have been read as on its own.
    /// </summary>
    [Fact]
    public void Generate_MixedWidthBounds_SuffixBothBoundsToTheMembersType() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Limits {
                [Range(0, 4294967295)]
                public long IndexDiveLimit { get; init; }
            }
            """);

        var source = result.Sources["Api.LimitsValidator.g.cs"];

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("new global::ValidationModules.ValidationMessageInfo(global::ValidationModules.ValidationMessageTemplates.RangeBetween, 0L, 4294967295L)", source);
    }

    /// <summary>
    /// A fractional member keeps the conversion the decimal path already established: bounds have to
    /// carry <c>m</c> or the comparison itself fails, because C# has no implicit double-to-decimal.
    /// </summary>
    [Fact]
    public void Generate_DecimalMemberWithIntegralBounds_StillRedenominates() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Money {
                [Range(0, 4294967295)]
                public decimal Amount { get; init; }
            }
            """);

        var source = result.Sources["Api.MoneyValidator.g.cs"];

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("0m", source);
        Assert.Contains("4294967295m", source);
    }

    /// <summary>
    /// A bound with no form in the member's type must still route to VM1103 rather than emit
    /// something uncompilable - the guard Redenominate already had, kept for the widened path.
    /// </summary>
    [Fact]
    public void Generate_NonFiniteBoundOnADecimalMember_IsReportedNotEmitted() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Money {
                [Range(0, double.PositiveInfinity)]
                public decimal Amount { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(result.Diagnostics, d => d.Id == "VM1103");
    }

    /// <summary>
    /// Float and double members keep their own suffixes rather than acquiring an integral one.
    /// </summary>
    [Theory]
    [InlineData("float", "f")]
    [InlineData("double", "")]
    public void Generate_FractionalMemberWithMixedWidthBounds_EmitsCompilableCode(
        string memberType, string _) {
        var result = GeneratorHarness.Run($$"""
            using ValidationModules.Constraints;

            namespace Api;

            public record Limits {
                [Range(0, 4294967295)]
                public {{memberType}} Value { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }
}
