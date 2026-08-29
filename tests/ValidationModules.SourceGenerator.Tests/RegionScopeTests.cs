using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// A Describe body is transcribed into <c>{RulesClass}_Rules</c> so it keeps its declaring file's
/// using directives — and a bare name that resolved inside the rules class does not resolve there.
/// </summary>
/// <remarks>
/// <para>
/// This used to surface as CS0103 inside generated code, for <b>any</b> bare reference to the rules
/// class's own members regardless of accessibility. It reads as an accessibility problem and is not
/// one: <c>internal const int Max</c> failed exactly as <c>private const int Max</c> did, because
/// the companion method is simply not in that scope.
/// </para>
/// <para>
/// So the fix is to qualify, which reads the real member. A private member cannot be reached even
/// qualified — that is VM0088 ("make it internal") — except a constant, which crosses by value
/// because C# already bakes a const at every use site.
/// </para>
/// </remarks>
public class RegionScopeTests {

    private static GeneratorHarness.Result Run(string members, string statement) => GeneratorHarness.Run($$"""
        using ValidationModules;

        namespace Sample;

        public sealed record Model {
            public int Count { get; init; }
            public string? Name { get; init; }
        }

        public sealed class ModelRules : IValidationRulesFor<Model> {
        {{members}}
            public static void Describe(ValidationRules<Model> rules, Model x) {
                {{statement}}
            }
        }
        """);

    private static string Region(string members, string statement) {
        var result = Run(members, statement);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        return result.Sources.Single(source => source.Key.Contains("_Rules")).Value;
    }

    // -- qualification -------------------------------------------------------------------------

    [Theory]
    [InlineData("    internal const int Max = 10;")]
    [InlineData("    public const int Max = 10;")]
    [InlineData("    internal static readonly int Max = 10;")]
    [InlineData("    public static int Max => 10;")]
    public void ANonPrivateMember_IsQualifiedRatherThanCopied(string member) {
        var region = Region(member, "rules.Ensure(x.Count <= Max);");

        Assert.Contains("global::Sample.ModelRules.Max", region);
    }

    /// <summary>
    /// The point of qualifying rather than copying: the region reads the same field the author's
    /// file names, so a value that changes changes for the validator too.
    /// </summary>
    [Fact]
    public void AStaticReadonlyField_IsReadRatherThanBaked() {
        var region = Region(
            "    internal static readonly int Max = 10;",
            "rules.Ensure(x.Count <= Max);");

        Assert.Contains("global::Sample.ModelRules.Max", region);
        Assert.DoesNotContain("<= 10", region);
    }

    [Fact]
    public void AnInternalMethod_IsQualified() {
        var region = Region(
            "    internal static bool Ok(Model v) => true;",
            "rules.Ensure(x.Count > 0 && Ok(x));");

        Assert.Contains("global::Sample.ModelRules.Ok(x)", region);
    }

    [Fact]
    public void AConstantUsedAsAChainBound_IsRewrittenTheSameWay() {
        // Island arguments are check text, not raw text: a private const bound bakes by value
        // exactly as it does in an Ensure condition. Carried raw, this emitted CS0103 in the
        // companion - the multi-target work is what surfaced it.
        var region = Region(
            "    private const int Max = 40;",
            "rules.Require(x.Name).Length(2, Max);");

        Assert.Contains("x.Name.Length > 40", region);
        Assert.DoesNotContain("ModelRules.Max", region);
    }

    [Fact]
    public void AnIfCondition_IsRewrittenTheSameWay() {
        // Control flow is C# now, and its conditions transcribe under the same rewrites the
        // island arguments do.
        var region = Region(
            "    internal const int Max = 10;",
            "if (x.Count <= Max) { rules.Require(x.Name); }");

        Assert.Contains("global::Sample.ModelRules.Max", region);
    }

    // -- what is left alone ---------------------------------------------------------------------

    [Fact]
    public void AMemberOfAnotherClass_IsUntouched() {
        var result = GeneratorHarness.Run("""
            using ValidationModules;

            namespace Sample;

            public static class Limits { public const int Max = 10; }

            public sealed record Model { public int Count { get; init; } }

            public sealed class ModelRules : IValidationRulesFor<Model> {
                public static void Describe(ValidationRules<Model> rules, Model x) {
                    rules.Ensure(x.Count <= Limits.Max);
                }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Limits.Max", result.Sources.Single(s => s.Key.Contains("_Rules")).Value);
    }

    [Fact]
    public void TheSubjectParameterIsNotRewritten() {
        var region = Region(string.Empty, "rules.Ensure(x.Count > 0);");

        Assert.Contains("x.Count > 0", region);
        Assert.DoesNotContain("ModelRules.x", region);
    }

    // -- private -------------------------------------------------------------------------------

    /// <summary>
    /// A private member cannot be reached even qualified. A constant is the one thing that can
    /// cross by value without the region and the author's file being able to disagree, because C#
    /// already bakes a const at every use site.
    /// </summary>
    [Theory]
    [InlineData("    private const int Max = 10;", "x.Count <= Max", "10")]
    [InlineData("    private const string Max = \"ab\";", "x.Name == Max", "\"ab\"")]
    [InlineData("    private const bool Max = true;", "x.Count > 0 == Max", "true")]
    public void APrivateConstant_IsCarriedAcrossByValue(string member, string condition, string expected) {
        var region = Region(member, $"rules.Ensure({condition});");

        Assert.Contains(expected, region);
        Assert.DoesNotContain("ModelRules.Max", region);
    }

    [Fact]
    public void APrivateMethod_IsVM0088RatherThanAnErrorInGeneratedCode() {
        var result = Run(
            "    private static bool Ok(Model v) => true;",
            "rules.Ensure(x.Count > 0 && Ok(x));");

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0088");
    }

    [Fact]
    public void APrivateStaticReadonlyField_IsVM0088() {
        var result = Run(
            "    private static readonly int Max = 10;",
            "rules.Ensure(x.Count <= Max);");

        var reported = Assert.Single(result.Diagnostics, d => d.Id == "VM0088");

        Assert.Contains("Max", reported.GetMessage());
        Assert.Contains("Make it internal", reported.GetMessage());
    }

    /// <summary>
    /// Every C# constant type reads back as the same value <b>and</b> the same type. The suffix is
    /// not decoration - without it <c>1.5</c> is a double where a decimal was meant, and <c>10</c>
    /// is an int where a double was.
    /// </summary>
    [Theory]
    [InlineData("decimal", "1.5m", "1.5m")]
    [InlineData("double", "1.5", "1.5D")]
    [InlineData("double", "10.0", "10D")]
    [InlineData("float", "1.5f", "1.5F")]
    [InlineData("long", "10L", "10L")]
    [InlineData("uint", "10U", "10U")]
    [InlineData("ulong", "10UL", "10UL")]
    [InlineData("short", "10", "10")]
    public void APrivateConstantOfAnyNumericType_IsWrittenBackWithItsType(
        string type, string literal, string expected) {

        var region = Region(
            $"    private const {type} Max = {literal};",
            "rules.Ensure(x.Count <= (double)Max);");

        Assert.Contains(expected, region);
    }

    /// <summary>
    /// Round-trip formatting, not the default. Shortest-round-trip <c>ToString</c> only became the
    /// default in .NET Core 3.0, and this generator is netstandard2.0 and may be loaded into a .NET
    /// Framework host — where the default would quietly drop the last two digits here.
    /// </summary>
    [Fact]
    public void ADoubleConstant_KeepsEveryDigit() {
        var region = Region(
            "    private const double Max = 1.2345678901234567;",
            "rules.Ensure(x.Count <= Max);");

        Assert.Contains("1.2345678901234567D", region);
    }

    /// <summary>
    /// A decimal carries its scale, and the two are the same value but not the same representation.
    /// </summary>
    [Fact]
    public void ADecimalConstant_KeepsItsScale() {
        var region = Region(
            "    private const decimal Max = 1.50m;",
            "rules.Ensure(x.Count <= (double)Max);");

        Assert.Contains("1.50m", region);
    }

    /// <summary>
    /// An enum constant arrives as its underlying number, so the cast is what makes it read back as
    /// itself. A cast rather than a member name because a value need not name one — a `[Flags]`
    /// combination is an ordinary constant.
    /// </summary>
    [Fact]
    public void AnEnumConstant_IsWrittenBackAsACast() {
        var result = GeneratorHarness.Run("""
            using ValidationModules;

            namespace Sample;

            public enum Status { Draft = 0, Active = 1 }

            public sealed record Model { public Status Status { get; init; } }

            public sealed class ModelRules : IValidationRulesFor<Model> {
                private const Status Wanted = Status.Active;

                public static void Describe(ValidationRules<Model> rules, Model x) {
                    rules.Ensure(x.Status == Wanted);
                }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "(global::Sample.Status)1",
            result.Sources.Single(source => source.Key.Contains("_Rules")).Value);
    }

    [Fact]
    public void ANullConstant_IsWrittenBackAsNull() {
        var region = Region(
            "    private const string? Missing = null;",
            "rules.Ensure(x.Name != Missing);");

        Assert.Contains("x.Name != null", region);
    }

    /// <summary>
    /// The three floating-point values that have no literal form are named instead.
    /// </summary>
    [Theory]
    [InlineData("double.NaN", "double.NaN")]
    [InlineData("double.PositiveInfinity", "double.PositiveInfinity")]
    [InlineData("double.NegativeInfinity", "double.NegativeInfinity")]
    public void AFloatingPointConstantWithNoLiteralForm_IsNamed(string literal, string expected) {
        var region = Region(
            $"    private const double Max = {literal};",
            "rules.Ensure(x.Count <= Max);");

        Assert.Contains(expected, region);
    }
}
