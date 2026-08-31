using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The range methods are overload pairs - <c>TValue value</c> beside <c>TValue? value</c> - so
/// type inference reads the member rather than the bound literals alone.
/// </summary>
/// <remarks>
/// C# infers nothing from a non-nullable argument to a <c>TValue?</c> parameter, so with only
/// the nullable form <c>rules.Range(x.Latitude, -90, 90)</c> fixed <c>TValue</c> to <c>int</c>
/// from the literals and failed as a CS1503 blaming the value, plus VM0070 because the call
/// never bound. The rc1014 trial filed that cascade as a major. These tests pin every call
/// shape through the pair: the plain overload wins for non-nullable members, the nullable
/// overload for nullable members, and no shape is ambiguous.
/// </remarks>
public class RangeInferenceTests {

    private static string Rules(string body) => $$"""
        using System;
        using ValidationModules;

        namespace Sample;

        public sealed record Telemetry {
            public double Latitude { get; init; }
            public float Ratio { get; init; }
            public int Age { get; init; }
            public decimal? BatteryKwh { get; init; }
            public DateOnly Start { get; init; }
        }

        public sealed class TelemetryRules : IValidationRulesFor<Telemetry> {
            public static void Describe(ValidationRules<Telemetry> rules, Telemetry x) {
        {{body}}
            }
        }
        """;

    [Theory]
    [InlineData("rules.Range(x.Latitude, -90, 90);")]
    [InlineData("rules.Range(x.Latitude, -90.0, 90.0);")]
    [InlineData("rules.Range(x.Ratio, 0, 1);")]
    [InlineData("rules.Range(x.Age, 0, 30);")]
    [InlineData("rules.Range(x.BatteryKwh, 10, 300);")]
    [InlineData("rules.Range(x.BatteryKwh, 10m, 300m);")]
    [InlineData("rules.RangeAtLeast(x.Latitude, -90);")]
    [InlineData("rules.RangeAtMost(x.Latitude, 90);")]
    [InlineData("rules.RangeAtLeast(x.BatteryKwh, 10);")]
    [InlineData("rules.RangeAtMost(x.BatteryKwh, 300);")]
    [InlineData("rules.Range(x.Start, new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1));")]
    public void EveryCallShape_BindsAndTranscribesClean(string statement) {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    /// <summary>
    /// The shape the rc1014 trial hit: a non-nullable double with raw int bounds. The plain
    /// overload lets the member's type into inference, and the literals convert to it.
    /// </summary>
    [Fact]
    public void NonNullableMember_RawIntBounds_InferFromTheMember() {
        var result = GeneratorHarness.Run(Rules("        rules.Range(x.Latitude, -90, 90);"));

        Assert.Empty(result.CompilationErrors);

        var region = result.Sources["Sample.TelemetryRules_Rules.g.cs"];

        Assert.Contains("\"latitude\"", region);
        Assert.Contains("x.Latitude < -90", region);
    }

    /// <summary>
    /// The .Value-plus-raw-bounds cascade the trial filed: it used to be CS1503 plus VM0070.
    /// Through the plain overload it now binds, so VM0093 can say the one true fix and the
    /// reader compiles the rule against the member itself.
    /// </summary>
    [Fact]
    public void ValueUnwrap_WithRawBounds_NowBindsAndIsCorrected() {
        var result = GeneratorHarness.Run(Rules("        rules.Range(x.BatteryKwh.Value, 10, 300);"));

        Assert.Empty(result.CompilationErrors);
        Assert.Single(result.Diagnostics, d => d.Id == "VM0093");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0070");

        var region = result.Sources["Sample.TelemetryRules_Rules.g.cs"];

        Assert.Contains("\"batteryKwh\"", region);
        Assert.DoesNotContain("batteryKwh.value", region);
    }

    /// <summary>
    /// <c>Require</c> cannot grow the same twin - the reference-type overload's <c>TValue?</c>
    /// is annotation-only, so the twin is CS0111 - so it has an <c>object?</c> catch-all
    /// instead. The bare spelling binds through it, typed spellings never reach it, and VM0090
    /// is the only error on the line - no CS0452 about the wrong overload, no VM0070.
    /// </summary>
    [Fact]
    public void RequireOnANonNullableValueType_Bare_IsVM0090Alone() {
        var result = GeneratorHarness.Run(Rules("        rules.Require(x.Age);"));

        Assert.Empty(result.CompilationErrors);
        Assert.Single(result.Diagnostics, d => d.Id == "VM0090");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0070");
    }
}
