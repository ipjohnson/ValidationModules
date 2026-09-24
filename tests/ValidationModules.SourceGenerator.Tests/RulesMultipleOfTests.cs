using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>rules.MultipleOf</c> emits the check <c>[MultipleOf]</c> emits for the same member, whichever
/// of its three overloads the call bound to.
/// </summary>
/// <remarks>
/// The long and decimal overloads divide with <c>%</c>, so their divisor can be transcribed as
/// written. The double overload's check runs in the decimal domain and takes a decimal divisor, and
/// transcribing <c>0.25</c> as written failed with CS1503 inside generated code.
/// </remarks>
public class RulesMultipleOfTests
{
    private static string Rules(string body) =>
        $$"""
            using ValidationModules;

            namespace Sample;

            public static class Steps {
                public static readonly double Quarter = 0.25;
            }

            public sealed record Mix {
                public double Ratio { get; init; }
                public double? Share { get; init; }
                public float Scale { get; init; }
                public int Quantity { get; init; }
                public long? Units { get; init; }
                public decimal Price { get; init; }
            }

            public sealed class MixRules : IValidationRulesFor<Mix> {
                public static void Describe(ValidationRules<Mix> rules, Mix x) {
            {{body}}
                }
            }
            """;

    [Theory]
    [InlineData("rules.MultipleOf(x.Ratio, 0.25);")]
    [InlineData("rules.For(x.Share).MultipleOf(0.25);")]
    [InlineData("rules.MultipleOf(x.Scale, 0.5);")]
    [InlineData("rules.MultipleOf(x.Quantity, 0.5);")]
    [InlineData("rules.MultipleOf(x.Ratio, Steps.Quarter);")]
    [InlineData("rules.MultipleOf(x.Quantity, 5);")]
    [InlineData("rules.MultipleOf(x.Units, 5);")]
    [InlineData("rules.MultipleOf(x.Price, 0.05m);")]
    public void EveryOverload_BindsAndTranscribesClean(string statement)
    {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void DoubleDivisor_IsTheDecimalLiteralTheAttributeWouldEmit()
    {
        var result = GeneratorHarness.Run(Rules("        rules.MultipleOf(x.Ratio, 0.1);"));

        var region = result.Sources["Sample.MixRules_Rules.g.cs"];

        Assert.Contains("IsMultipleOf(x.Ratio, 0.1m)", region);
    }

    [Fact]
    public void NonConstantDoubleDivisor_IsConvertedWhereTheCheckReadsIt()
    {
        var result = GeneratorHarness.Run(
            Rules("        rules.MultipleOf(x.Ratio, Steps.Quarter);")
        );

        var region = result.Sources["Sample.MixRules_Rules.g.cs"];

        Assert.Contains("IsMultipleOf(x.Ratio, (decimal)(Steps.Quarter))", region);
    }

    [Fact]
    public void IntegralDivisor_KeepsTheModuloForm()
    {
        var result = GeneratorHarness.Run(Rules("        rules.MultipleOf(x.Quantity, 5);"));

        var region = result.Sources["Sample.MixRules_Rules.g.cs"];

        Assert.Contains("x.Quantity % 5L != 0", region);
    }

    /// <summary>
    /// A constant divisor gets the attribute path's check as well as its literal. Zero reached the
    /// emitter as <c>% 0</c>, which is CS0020 inside generated code for an integral member.
    /// </summary>
    [Theory]
    [InlineData("rules.MultipleOf(x.Quantity, 0);")]
    [InlineData("rules.MultipleOf(x.Ratio, -0.5);")]
    [InlineData("rules.MultipleOf(x.Price, 0m);")]
    public void ZeroOrNegativeDivisor_IsVM1104(string statement)
    {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        Assert.Contains(result.Diagnostics, d => d.Id == "VM1104");
        Assert.Empty(result.CompilationErrors);
    }
}
