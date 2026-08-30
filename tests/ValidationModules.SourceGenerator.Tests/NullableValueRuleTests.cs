using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// VM0093: a rule value written as <c>x.Member.Value</c>. The builder's parameters are already
/// nullable, so the unwrap is never needed, and in the rc1013 trial it produced two majors from
/// one habit: unsuffixed bound literals inferring the wrong <c>TValue</c> (an opaque CS1503 plus
/// VM0070), and a compiling rule whose wire path was <c>batteryKwh.value</c>.
/// </summary>
public class NullableValueRuleTests {

    private static string Rules(string body) => $$"""
        using ValidationModules;

        namespace Sample;

        public sealed record Vehicle {
            public decimal? BatteryKwh { get; init; }
        }

        public sealed class VehicleRules : IValidationRulesFor<Vehicle> {
            public static void Describe(ValidationRules<Vehicle> rules, Vehicle x) {
        {{body}}
            }
        }
        """;

    [Fact]
    public void ValueUnwrap_ReportsVM0093_NamingTheFix() {
        var result = GeneratorHarness.Run(Rules("        rules.Range(x.BatteryKwh.Value, 10m, 300m);"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0093");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("x.BatteryKwh.Value", diagnostic.GetMessage());
        Assert.Contains("write 'x.BatteryKwh'", diagnostic.GetMessage());
    }

    /// <summary>
    /// The unwrap is corrected as well as reported: the rule compiles against the member itself,
    /// so the wire path is <c>batteryKwh</c> and the message leaf stops being <c>value</c>.
    /// </summary>
    [Fact]
    public void ValueUnwrap_DerivesTheMemberPath_NotTheValueHop() {
        var result = GeneratorHarness.Run(Rules("        rules.Range(x.BatteryKwh.Value, 10m, 300m);"));

        Assert.Empty(result.CompilationErrors);

        var region = Assert.Single(result.Sources, pair => pair.Key.Contains("Rules")).Value;

        Assert.Contains("\"batteryKwh\"", region);
        Assert.DoesNotContain("batteryKwh.value", region);
    }

    /// <summary>
    /// The unsuffixed-literal shape: <c>TValue</c> infers <c>int</c> from the bounds, the decimal
    /// <c>.Value</c> does not convert, and the call never binds. VM0070 alone said only
    /// "unresolvable"; VM0093 beside it names the habit that caused it.
    /// </summary>
    [Fact]
    public void ValueUnwrap_WithUnsuffixedLiterals_ReportsVM0093BesideVM0070() {
        var result = GeneratorHarness.Run(Rules("        rules.Range(x.BatteryKwh.Value, 10, 300);"));

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0070");
        var unwrap = Assert.Single(result.Diagnostics, d => d.Id == "VM0093");
        Assert.Contains("x.BatteryKwh", unwrap.GetMessage());
    }

    [Fact]
    public void NullableValue_WithoutTheUnwrap_IsClean() {
        var result = GeneratorHarness.Run(Rules("        rules.Range(x.BatteryKwh, 10m, 300m);"));

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0093");
    }

    /// <summary>
    /// <c>.Value</c> on something that is not a subject path - here a local - is not this
    /// diagnostic's business; VM0071 already owns "not a member path".
    /// </summary>
    [Fact]
    public void ValueOnANonSubjectPath_IsNotVM0093() {
        var result = GeneratorHarness.Run(Rules("""
                decimal? local = 5m;
                rules.Range(local.Value, 10m, 300m);
        """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0093");
    }
}
