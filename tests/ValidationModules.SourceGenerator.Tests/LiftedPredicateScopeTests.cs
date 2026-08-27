using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// A predicate is lifted into <c>{RulesClass}_Rules</c> so it keeps its declaring file's using
/// directives — and a name that resolved inside the rules class does not resolve there.
/// </summary>
/// <remarks>
/// <para>
/// This used to surface as CS0103 inside generated code, for <b>any</b> bare reference to the rules
/// class's own members regardless of accessibility. It reads as an accessibility problem and is not
/// one: <c>internal const int Max</c> failed exactly as <c>private const int Max</c> did, because
/// the lifted method is simply not in that scope.
/// </para>
/// <para>
/// So the fix is to qualify, which reads the real member. Copying its value across would be a second
/// thing that has to stay equal to the first, and <c>DescribedValidator&lt;T&gt;</c> runs the
/// original lambda — the two engines would have to agree by luck rather than by construction.
/// </para>
/// </remarks>
public class LiftedPredicateScopeTests {

    private static GeneratorHarness.Result Run(string members, string statement) => GeneratorHarness.Run($$"""
        using ValidationModules;

        namespace Sample;

        public sealed record Model {
            public int Count { get; init; }
            public string? Name { get; init; }
        }

        public sealed class ModelRules : IValidationRulesFor<Model> {
        {{members}}
            public void Describe(ValidationRules<Model> rules) {
                {{statement}}
            }
        }
        """);

    private static string Lifted(string members, string statement) {
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
        var lifted = Lifted(member, "rules.Ensure(x => x.Count <= Max);");

        Assert.Contains("global::Sample.ModelRules.Max", lifted);
    }

    /// <summary>
    /// The point of qualifying rather than copying: the generated engine reads the same field the
    /// described engine does, so a value that changes changes for both.
    /// </summary>
    [Fact]
    public void AStaticReadonlyField_IsReadRatherThanBaked() {
        var lifted = Lifted(
            "    internal static readonly int Max = 10;",
            "rules.Ensure(x => x.Count <= Max);");

        Assert.Contains("global::Sample.ModelRules.Max", lifted);
        Assert.DoesNotContain("<= 10", lifted);
    }

    [Fact]
    public void AnInternalMethod_IsQualified() {
        var lifted = Lifted(
            "    internal static bool Ok(Model v) => true;",
            "rules.Ensure(x => x.Count > 0 && Ok(x));");

        Assert.Contains("global::Sample.ModelRules.Ok(x)", lifted);
    }

    [Fact]
    public void ConditionsAreRewrittenTheSameWay() {
        var lifted = Lifted(
            "    internal const int Max = 10;",
            "rules.Required(x => x.Name).When(x => x.Count <= Max);");

        Assert.Contains("global::Sample.ModelRules.Max", lifted);
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
                public void Describe(ValidationRules<Model> rules) {
                    rules.Ensure(x => x.Count <= Limits.Max);
                }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Limits.Max", result.Sources.Single(s => s.Key.Contains("_Rules")).Value);
    }

    [Fact]
    public void TheLambdaParameterIsNotRewritten() {
        var lifted = Lifted(string.Empty, "rules.Ensure(x => x.Count > 0);");

        Assert.Contains("x.Count > 0", lifted);
        Assert.DoesNotContain("ModelRules.x", lifted);
    }

    // -- private -------------------------------------------------------------------------------

    /// <summary>
    /// A private member cannot be reached even qualified. A constant is the one thing that can cross
    /// by value without the two engines being able to disagree, because C# already bakes a const at
    /// every use site.
    /// </summary>
    [Theory]
    [InlineData("    private const int Max = 10;", "x.Count <= Max", "10")]
    [InlineData("    private const string Max = \"ab\";", "x.Name == Max", "\"ab\"")]
    [InlineData("    private const bool Max = true;", "x.Count > 0 == Max", "true")]
    public void APrivateConstant_IsCarriedAcrossByValue(string member, string predicate, string expected) {
        var lifted = Lifted(member, $"rules.Ensure(x => {predicate});");

        Assert.Contains(expected, lifted);
        Assert.DoesNotContain("ModelRules.Max", lifted);
    }

    [Fact]
    public void APrivateMethod_IsVM0078RatherThanAnErrorInGeneratedCode() {
        var result = Run(
            "    private static bool Ok(Model v) => true;",
            "rules.Ensure(x => x.Count > 0 && Ok(x));");

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0078");
    }

    [Fact]
    public void APrivateStaticReadonlyField_IsVM0078() {
        var result = Run(
            "    private static readonly int Max = 10;",
            "rules.Ensure(x => x.Count <= Max);");

        var reported = Assert.Single(result.Diagnostics, d => d.Id == "VM0078");

        Assert.Contains("ModelRules.Max", reported.GetMessage());
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

        var lifted = Lifted(
            $"    private const {type} Max = {literal};",
            "rules.Ensure(x => x.Count <= (double)Max);");

        Assert.Contains(expected, lifted);
    }

    /// <summary>
    /// Round-trip formatting, not the default. Shortest-round-trip <c>ToString</c> only became the
    /// default in .NET Core 3.0, and this generator is netstandard2.0 and may be loaded into a .NET
    /// Framework host — where the default would quietly drop the last two digits here.
    /// </summary>
    [Fact]
    public void ADoubleConstant_KeepsEveryDigit() {
        var lifted = Lifted(
            "    private const double Max = 1.2345678901234567;",
            "rules.Ensure(x => x.Count <= Max);");

        Assert.Contains("1.2345678901234567D", lifted);
    }

    /// <summary>
    /// A decimal carries its scale, and the two are the same value but not the same representation.
    /// </summary>
    [Fact]
    public void ADecimalConstant_KeepsItsScale() {
        var lifted = Lifted(
            "    private const decimal Max = 1.50m;",
            "rules.Ensure(x => x.Count <= (double)Max);");

        Assert.Contains("1.50m", lifted);
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

                public void Describe(ValidationRules<Model> rules) {
                    rules.Ensure(x => x.Status == Wanted);
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
        var lifted = Lifted(
            "    private const string? Missing = null;",
            "rules.Ensure(x => x.Name != Missing);");

        Assert.Contains("x.Name != null", lifted);
    }

    /// <summary>
    /// The three floating-point values that have no literal form are named instead.
    /// </summary>
    [Theory]
    [InlineData("double.NaN", "double.NaN")]
    [InlineData("double.PositiveInfinity", "double.PositiveInfinity")]
    [InlineData("double.NegativeInfinity", "double.NegativeInfinity")]
    public void AFloatingPointConstantWithNoLiteralForm_IsNamed(string literal, string expected) {
        var lifted = Lifted(
            $"    private const double Max = {literal};",
            "rules.Ensure(x => x.Count <= Max);");

        Assert.Contains(expected, lifted);
    }
}
