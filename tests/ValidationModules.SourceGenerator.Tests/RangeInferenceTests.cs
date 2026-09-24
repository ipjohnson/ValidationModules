using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The range methods are overload pairs - <c>TValue value</c> beside <c>TValue? value</c> - so
/// type inference reads the member rather than the bound literals alone.
/// </summary>
/// <remarks>
/// C# infers nothing from a non-nullable argument to a <c>TValue?</c> parameter, so with only
/// the nullable form <c>rules.Range(x.Latitude, -90, 90)</c> fixed <c>TValue</c> to <c>int</c>
/// from the literals and failed as a CS1503 blaming the value, plus VM3001 because the call
/// never bound. The rc1014 trial filed that cascade as a major. These tests pin every call
/// shape through the pair: the plain overload wins for non-nullable members, the nullable
/// overload for nullable members, and no shape is ambiguous.
/// </remarks>
public class RangeInferenceTests
{
    private static string Rules(string body) =>
        $$"""
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
    public void EveryCallShape_BindsAndTranscribesClean(string statement)
    {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    /// <summary>
    /// <c>For</c> anchors a chain on the member's own type, so the chained range methods pair the
    /// same way: a non-nullable receiver beside the nullable one.
    /// </summary>
    [Theory]
    [InlineData("rules.For(x.Latitude).Range(-90, 90);", "x.Latitude < -90 || x.Latitude > 90")]
    [InlineData("rules.For(x.Age).RangeAtLeast(0);", "x.Age < 0")]
    [InlineData("rules.For(x.Ratio).RangeAtMost(1);", "x.Ratio > 1")]
    [InlineData(
        "rules.For(x.Start).Range(new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1));",
        "x.Start < new"
    )]
    [InlineData("rules.For(x.BatteryKwh).Range(10, 300);", "x.BatteryKwh.Value < 10")]
    public void AChainForStarts_TakesTheRangeMethods(string statement, string expected)
    {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Contains(expected, result.Sources["Sample.TelemetryRules_Rules.g.cs"]);
    }

    /// <summary>
    /// The chain overloads are additive, and <c>For</c> is unchanged, so every anchor it started
    /// before is the same type. Compiled at C# 12, which has no <c>OverloadResolutionPriority</c>
    /// to settle an ambiguity, so every chain here has to resolve on its own.
    /// </summary>
    [Fact]
    public void ForAnchors_KeepTheirTypes_AndEveryChainResolves_OnCSharp12()
    {
        var tree = CSharpSyntaxTree.ParseText(
            """
            using System.Collections.Generic;
            using ValidationModules;

            namespace Sample;

            public interface IMeasured<TValue> {
                TValue Reading { get; }
            }

            public sealed record Part {
                public string? Sku { get; init; }
            }

            public sealed record Box {
                public double Weight { get; init; }
                public double? Share { get; init; }
                public int Count { get; init; }
                public long? Units { get; init; }
                public string? Label { get; init; }
                public Part? Main { get; init; }
                public IReadOnlyList<Part>? Parts { get; init; }
            }

            public sealed class BoxRules : IValidationRulesFor<Box> {
                public static void Describe(ValidationRules<Box> rules, Box x) {
                    rules.For(x.Weight).Range(0, 100).MultipleOf(0.5);
                    rules.For(x.Share).Range(0, 1).MultipleOf(0.25);
                    rules.For(x.Count).RangeAtLeast(1).RangeAtMost(9).MultipleOf(3);
                    rules.For(x.Units).MultipleOf(2);
                    rules.For(x.Label).Require().Length(1, 5);
                    rules.For(x.Main).Nested();
                    rules.For(x.Parts).Count(1, 3).Each();
                }
            }

            public static class MeasuredRules {
                public static void Standard<T, TValue>(ValidationRules<T> rules, T x)
                    where T : IMeasured<TValue> {
                    rules.For(x.Reading);
                }
            }
            """,
            new CSharpParseOptions(LanguageVersion.CSharp12),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var compilation = CSharpCompilation.Create(
            "ForAnchors",
            new[] { tree },
            GeneratorHarness.ReferencesIncluding(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        Assert.Empty(
            compilation
                .GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
        );

        var model = compilation.GetSemanticModel(tree);
        var calls = tree.GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(call => (IMethodSymbol)model.GetSymbolInfo(call).Symbol!)
            .ToList();

        Assert.Equal(
            [
                "double",
                "double?",
                "int",
                "long?",
                "string?",
                "Sample.Part?",
                "System.Collections.Generic.IReadOnlyList<Sample.Part>?",
                "TValue",
            ],
            calls
                .Where(method => method.Name == "For")
                .Select(method =>
                    ((INamedTypeSymbol)method.ReturnType).TypeArguments[1].ToDisplayString()
                )
        );

        // A nullable chain still binds the MultipleOf overload that names its type, not the
        // generic one beside it.
        Assert.Equal(
            ["double", "double", "int", "long"],
            calls
                .Where(method => method.Name == "MultipleOf")
                .Select(method => method.Parameters.Single().Type.ToDisplayString())
        );
        Assert.Equal(
            [2, 1, 2, 1],
            calls
                .Where(method => method.Name == "MultipleOf")
                .Select(method => method.ReducedFrom!.TypeParameters.Length)
        );
    }

    /// <summary>
    /// The shape the rc1014 trial hit: a non-nullable double with raw int bounds. The plain
    /// overload lets the member's type into inference, and the literals convert to it.
    /// </summary>
    [Fact]
    public void NonNullableMember_RawIntBounds_InferFromTheMember()
    {
        var result = GeneratorHarness.Run(Rules("        rules.Range(x.Latitude, -90, 90);"));

        Assert.Empty(result.CompilationErrors);

        var region = result.Sources["Sample.TelemetryRules_Rules.g.cs"];

        Assert.Contains("\"latitude\"", region);
        Assert.Contains("x.Latitude < -90", region);
    }

    /// <summary>
    /// The .Value-plus-raw-bounds cascade the trial filed: it used to be CS1503 plus VM3001.
    /// Through the plain overload it now binds, so VM3104 can say the one true fix and the
    /// reader compiles the rule against the member itself.
    /// </summary>
    [Fact]
    public void ValueUnwrap_WithRawBounds_NowBindsAndIsCorrected()
    {
        var result = GeneratorHarness.Run(
            Rules("        rules.Range(x.BatteryKwh.Value, 10, 300);")
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Single(result.Diagnostics, d => d.Id == "VM3104");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3001");

        var region = result.Sources["Sample.TelemetryRules_Rules.g.cs"];

        Assert.Contains("\"batteryKwh\"", region);
        Assert.DoesNotContain("batteryKwh.value", region);
    }

    /// <summary>
    /// <c>Require</c> cannot grow the same twin - the reference-type overload's <c>TValue?</c>
    /// is annotation-only, so the twin is CS0111 - so it has an <c>object?</c> catch-all
    /// instead. The bare spelling binds through it, typed spellings never reach it, and VM3101
    /// is the only error on the line - no CS0452 about the wrong overload, no VM3001.
    /// </summary>
    [Fact]
    public void RequireOnANonNullableValueType_Bare_IsVM3101Alone()
    {
        var result = GeneratorHarness.Run(Rules("        rules.Require(x.Age);"));

        Assert.Empty(result.CompilationErrors);
        Assert.Single(result.Diagnostics, d => d.Id == "VM3101");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3001");
    }
}
