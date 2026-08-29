using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Control flow in a Describe body is C# - <c>if</c>/<c>else</c>, <c>switch</c>, computation - and
/// transcribes positionally into the region, with islands expanded where they stand.
/// </summary>
/// <remarks>
/// This replaces the <c>When</c>/<c>Unless</c> surface and its condition hoisting. Hoisting existed
/// so two engines would evaluate a condition the same number of times; with one engine, what the
/// code says is the spec - a condition evaluates where written, every time it is reached.
/// </remarks>
public class ControlFlowTests {

    private static GeneratorHarness.Result Run(string body, string extraTypes = "") => GeneratorHarness.Run($$"""
        using System;
        using ValidationModules;

        namespace Sample;

        public sealed record Claim {
            public bool IsAuto { get; init; }
            public bool IsExpedited { get; init; }
            public int Tier { get; init; }
            public string? Plate { get; init; }
            public string? Reason { get; init; }
            public string? Reference { get; init; }
        }
        {{extraTypes}}
        public sealed class ClaimRules : IValidationRulesFor<Claim> {
            public static void Describe(ValidationRules<Claim> rules, Claim x) {
        {{body}}
            }
        }
        """);

    private static string Region(string body, string extraTypes = "") {
        var result = Run(body, extraTypes);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        return result.Sources.Single(source => source.Key.Contains("_Rules")).Value;
    }

    // -- if/else -------------------------------------------------------------------------------

    [Fact]
    public void AnIfGuardedChain_ExpandsInsideTheBranch() {
        var region = Region("""
                    if (x.IsExpedited) {
                        rules.Require(x.Reason).Length(2, 500);
                    }
            """);

        Assert.Contains("if (x.IsExpedited) {", region);
        Assert.Contains("var missingReason = string.IsNullOrWhiteSpace(x.Reason);", region);
        Assert.Contains("!missingReason && (x.Reason is not null && (x.Reason.Length < 2 || x.Reason.Length > 500))", region);
    }

    [Fact]
    public void ElseBranches_CarryTheirOwnIslands() {
        var region = Region("""
                    if (x.IsAuto) {
                        rules.Require(x.Plate);
                    } else if (x.Tier > 2) {
                        rules.Require(x.Reference);
                    } else {
                        rules.Require(x.Reason);
                    }
            """);

        Assert.Contains("if (x.IsAuto) {", region);
        Assert.Contains("} else if (x.Tier > 2) {", region);
        Assert.Contains("} else {", region);
        Assert.Contains("ReportRequired(ctx, \"plate\")", region);
        Assert.Contains("ReportRequired(ctx, \"reference\")", region);
        Assert.Contains("ReportRequired(ctx, \"reason\")", region);
    }

    [Fact]
    public void ASwitchStatement_CarriesIslandsPerSection() {
        var region = Region("""
                    switch (x.Tier) {
                        case 1:
                            rules.Require(x.Reason);
                            break;
                        default:
                            rules.Require(x.Reference);
                            break;
                    }
            """);

        Assert.Contains("switch (x.Tier) {", region);
        Assert.Contains("case 1:", region);
        Assert.Contains("ReportRequired(ctx, \"reason\")", region);
        Assert.Contains("ReportRequired(ctx, \"reference\")", region);
    }

    // -- semantics are C# ----------------------------------------------------------------------

    /// <summary>
    /// The old surface hoisted each distinct condition to one evaluation per pass, so two engines
    /// would agree. One engine, one rule: a condition written twice evaluates twice.
    /// </summary>
    [Fact]
    public void AConditionWrittenTwice_EvaluatesWhereWritten() {
        var region = Region(
            """
                    if (Gate.Open()) {
                        rules.Require(x.Reason);
                    }
                    if (Gate.Open()) {
                        rules.Require(x.Reference);
                    }
            """,
            """

            public static class Gate {
                public static bool Open() => true;
            }
            """);

        Assert.Equal(2, region.Split("Gate.Open()").Length - 1);
    }

    [Fact]
    public void ComputationFeedingACondition_Transcribes() {
        var region = Region("""
                    var digits = x.Plate?.Length ?? 0;
                    if (digits > 3) {
                        rules.Length(x.Plate, 4, 10);
                    }
            """);

        Assert.Contains("var digits = x.Plate?.Length ?? 0;", region);
        Assert.Contains("if (digits > 3) {", region);
    }

    [Fact]
    public void AnEnsureUnderAnIf_IsGuardedByTheBranchAlone() {
        var region = Region("""
                    if (x.IsExpedited) {
                        rules.Ensure(x.Tier >= 2, code: "expedite_tier");
                    }
            """);

        Assert.Contains("if (x.IsExpedited) {", region);
        Assert.Contains("if (!(x.Tier >= 2) && ctx.Report(\"tier\", \"expedite_tier\", \"tier >= 2.\").ShouldStop)", region);
    }

    /// <summary>
    /// An early return ends this rules class's checks and nothing else - the region is a method,
    /// and the attribute region and other rules classes still run.
    /// </summary>
    [Fact]
    public void AnEarlyReturn_EndsTheRegionWithContinue() {
        var region = Region("""
                    if (x.IsAuto) {
                        return;
                    }
                    rules.Require(x.Reason);
            """);

        Assert.Contains("return global::ValidationModules.ValidationFlow.Continue;", region);
        Assert.Contains("ReportRequired(ctx, \"reason\")", region);
    }

    /// <summary>
    /// Statements keep body order through mixed control flow - the body is the validator.
    /// </summary>
    [Fact]
    public void BodyOrder_IsEmissionOrder() {
        var region = Region("""
                    rules.Require(x.Plate);
                    if (x.IsExpedited) {
                        rules.Require(x.Reason);
                    }
                    rules.Require(x.Reference);
            """);

        var plate = region.IndexOf("\"plate\"", StringComparison.Ordinal);
        var reason = region.IndexOf("\"reason\"", StringComparison.Ordinal);
        var reference = region.IndexOf("\"reference\"", StringComparison.Ordinal);

        Assert.True(plate < reason && reason < reference);
    }
}
