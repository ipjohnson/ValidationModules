using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The delegate arguments of <c>rules.Pattern</c> and <c>rules.Apply</c>, which generated code
/// calls by name.
/// </summary>
/// <remarks>
/// A method group is the documented form, and a lambda whose whole body calls one static method
/// is read as that method. Before this, a lambda reached the emitter as its own symbol, whose
/// name is empty, and generated <c>global::ProductRules.()</c> - CS1001 inside a generated file.
/// A private method generated a call the generated class cannot make - CS0122, where VM3004
/// names the method and says to make it internal.
/// </remarks>
public class RulesDelegateArgumentTests
{
    private static string Rules(
        string body,
        string checkModifier = "internal",
        string patternModifier = "internal"
    ) =>
        $$"""
            using System;
            using System.Text.RegularExpressions;
            using ValidationModules;

            namespace Sample;

            public sealed record Product {
                public string? Sku { get; init; }
            }

            public static class Factories {
                public static readonly Func<Regex> Sku = () => new Regex("^[A-Z]{3}$");
            }

            public sealed class ProductRules : IValidationRulesFor<Product> {
                public static void Describe(ValidationRules<Product> rules, Product x) {
            {{body}}
                }

                {{patternModifier}} static Regex SkuPattern() => new("^[A-Z]{3}$");

                internal static Regex Build(string pattern) => new(pattern);

                {{checkModifier}} static ValidationFlow Check(ref ValidationContext context, Product value) =>
                    ValidationFlow.Continue;
            }
            """;

    [Theory]
    [InlineData("rules.Pattern(x.Sku, SkuPattern);")]
    [InlineData("rules.Pattern(x.Sku, () => SkuPattern());")]
    [InlineData("rules.Pattern(x.Sku, static () => SkuPattern());")]
    [InlineData("rules.Pattern(x.Sku, () => { return SkuPattern(); });")]
    [InlineData("rules.For(x.Sku).Pattern(() => SkuPattern());")]
    [InlineData("rules.Apply(Check);")]
    [InlineData("rules.Apply((ref ValidationContext c, Product v) => Check(ref c, v));")]
    public void AMethodGroupOrALambdaCallingOne_TranscribesClean(string statement)
    {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void APatternLambda_CallsTheMethodItWraps()
    {
        var result = GeneratorHarness.Run(
            Rules("        rules.Pattern(x.Sku, () => SkuPattern());")
        );

        Assert.Contains(
            "global::Sample.ProductRules.SkuPattern()",
            result.Sources["Sample.ProductRules_Rules.g.cs"]
        );
    }

    [Fact]
    public void AnApplyLambda_CallsTheMethodItWraps()
    {
        var result = GeneratorHarness.Run(
            Rules("        rules.Apply((ref ValidationContext c, Product v) => Check(ref c, v));")
        );

        Assert.Contains(
            "global::Sample.ProductRules.Check(ref context, value)",
            result.Sources["Sample.ProductValidator.g.cs"]
        );
    }

    [Theory]
    [InlineData(
        "rules.Pattern(x.Sku, () => new Regex(\"^[A-Z]{3}$\"));",
        "rules.Pattern(x.Sku, SkuPattern)"
    )]
    [InlineData(
        "rules.Pattern(x.Sku, () => Build(\"^[A-Z]{3}$\"));",
        "rules.Pattern(x.Sku, SkuPattern)"
    )]
    [InlineData("rules.Pattern(x.Sku, Factories.Sku);", "rules.Pattern(x.Sku, SkuPattern)")]
    [InlineData("rules.For(x.Sku).Pattern(() => new Regex(\"x\"));", ".Pattern(SkuPattern)")]
    [InlineData(
        "rules.Apply((ref ValidationContext context, Product value) => ValidationFlow.Continue);",
        "rules.Apply(Check)"
    )]
    [InlineData(
        "rules.Apply((ref ValidationContext c, Product v) => Check(ref c, new Product()));",
        "rules.Apply(Check)"
    )]
    public void ADelegateThatNamesNoStaticMethod_IsVM3008(string statement, string replacement)
    {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        var reported = Assert.Single(result.Diagnostics, d => d.Id == "VM3008");

        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
        Assert.Contains(replacement, reported.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3001");
        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("rules.Pattern(x.Sku, SkuPattern);", "ProductRules.SkuPattern")]
    [InlineData("rules.Pattern(x.Sku, () => SkuPattern());", "ProductRules.SkuPattern")]
    [InlineData("rules.Apply(Check);", "ProductRules.Check")]
    [InlineData(
        "rules.Apply((ref ValidationContext c, Product v) => Check(ref c, v));",
        "ProductRules.Check"
    )]
    public void APrivateMethod_IsVM3004(string statement, string method)
    {
        var result = GeneratorHarness.Run(
            Rules($"        {statement}", checkModifier: "private", patternModifier: "private")
        );

        var reported = Assert.Single(result.Diagnostics, d => d.Id == "VM3004");

        Assert.Contains($"'{method}'", reported.GetMessage());
        Assert.Contains("internal", reported.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// The shape the issue reported, which is how a <c>[GeneratedRegex]</c> is usually written.
    /// The harness runs this generator alone, so the partial has no implementation and CS8795 is
    /// expected beside the diagnostic; CS0122 inside the generated file is what must not appear.
    /// </summary>
    [Fact]
    public void APrivateGeneratedRegex_IsVM3004RatherThanAnErrorInGeneratedCode()
    {
        var result = GeneratorHarness.Run(
            """
            using System.Text.RegularExpressions;
            using ValidationModules;

            namespace Sample;

            public sealed record Product {
                public string? Sku { get; init; }
            }

            public sealed partial class ProductRules : IValidationRulesFor<Product> {
                public static void Describe(ValidationRules<Product> rules, Product x) {
                    rules.Pattern(x.Sku, SkuPattern);
                }

                [GeneratedRegex("^[A-Z]{3}-[0-9]{4}$")]
                private static partial Regex SkuPattern();
            }
            """
        );

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3004");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS0122");
    }
}
