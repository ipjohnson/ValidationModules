using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The allowed-values constraint from all three places it can be written: the native
/// <c>[AllowedValues]</c>, DataAnnotations' <c>[AllowedValues]</c> and <c>[DeniedValues]</c>, and a
/// rules class's <c>AllowedValues</c>. Each reads every value it was given, compares strings the
/// way the attribute says, and names enum values the way a reader would.
/// </summary>
public class AllowedValuesTests
{
    private const string Templates = "global::ValidationModules.ValidationMessageTemplates";

    private static string Validator(GeneratorHarness.Result result) =>
        result.Sources.Single(pair => pair.Key.EndsWith("Validator.g.cs")).Value;

    private static string Rules(string body) =>
        $$"""
            using ValidationModules;

            namespace Sample;

            public enum Tier { Free, Pro, Enterprise }

            public static class Modes {
                public const string Road = "road";
                public static readonly string Sea = "sea";
                public static readonly string[] All = { "road", "rail" };
            }

            public sealed record Shipment {
                public string? Kind { get; init; }
                public string? Mode { get; init; }
                public Tier Tier { get; init; }
                public System.Collections.Generic.IReadOnlyList<string>? Tags { get; init; }
            }

            public sealed class ShipmentRules : IValidationRulesFor<Shipment> {
                public static void Describe(ValidationRules<Shipment> rules, Shipment x) {
            {{body}}
                }
            }
            """;

    private static string Region(GeneratorHarness.Result result) =>
        result.Sources["Sample.ShipmentRules_Rules.g.cs"];

    [Fact]
    public void Comparison_ComparesStringsTheWayItSays()
    {
        var result = GeneratorHarness.Run(
            """
            using System;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Account {
                [AllowedValues("active", "pending", Comparison = StringComparison.OrdinalIgnoreCase)]
                public string? Status { get; init; }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "!string.Equals(value.Status, \"active\", global::System.StringComparison.OrdinalIgnoreCase)",
            Validator(result)
        );
    }

    /// <summary>
    /// Ordinal is the default, and what <c>==</c> on a string already is, so asking for it keeps the
    /// operator rather than paying for a call.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(", Comparison = StringComparison.Ordinal")]
    public void OrdinalComparison_KeepsTheOperator(string comparison)
    {
        var result = GeneratorHarness.Run(
            $$"""
            using System;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Account {
                [AllowedValues("active", "pending"{{comparison}})]
                public string? Status { get; init; }
            }
            """
        );

        var emitted = Validator(result);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("value.Status != \"active\"", emitted);
        Assert.DoesNotContain("string.Equals", emitted);
    }

    [Fact]
    public void ComparisonOnAMemberThatIsNotAString_IsVM1203()
    {
        var result = GeneratorHarness.Run(
            """
            using System;
            using ValidationModules.Constraints;

            namespace Sample;

            public enum Tier { Free, Pro }

            public sealed record Plan {
                [AllowedValues(Tier.Pro, Comparison = StringComparison.OrdinalIgnoreCase)]
                public Tier Tier { get; init; }
            }
            """
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1203");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("'Tier'", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("string.Equals", Validator(result));
    }

    [Theory]
    [InlineData("AllowedValues", "AllowedValues")]
    [InlineData("DeniedValues", "DeniedValues")]
    public void DataAnnotationsEnumValues_ReadAsMemberNames(string attribute, string template)
    {
        var result = GeneratorHarness.Run(
            $$"""
            namespace Sample;

            public enum Tier { Free, Pro, Enterprise }

            public sealed record PlanChoice {
                [System.ComponentModel.DataAnnotations.{{attribute}}(Tier.Pro, Tier.Enterprise)]
                public Tier Tier { get; init; }
            }
            """
        );

        var emitted = Validator(result);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains($"{Templates}.{template}, \"Pro, Enterprise\"", emitted);
        Assert.DoesNotContain("\"global::", emitted);
    }

    [Theory]
    [InlineData("rules.For(x.Kind).AllowedValues(\"parcel\", \"pallet\");")]
    [InlineData("rules.For(x.Kind).AllowedValues(new[] { \"parcel\", \"pallet\" });")]
    [InlineData("rules.AllowedValues(x.Kind, [\"parcel\", \"pallet\"]);")]
    [InlineData("rules.AllowedValues(x.Kind, new[] { \"parcel\", \"pallet\" });")]
    [InlineData("rules.AllowedValues(x.Kind, new string[] { \"parcel\", \"pallet\" });")]
    [InlineData("rules.AllowedValues(x.Kind, allowed: [\"parcel\", \"pallet\"]);")]
    public void EveryFormOfTheSet_IsReadInFull(string statement)
    {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        var region = Region(result);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Contains("x.Kind != \"parcel\" && x.Kind != \"pallet\"", region);
        Assert.Contains("\"parcel, pallet\"", region);
    }

    /// <summary>
    /// A constant reads in the message as what it holds, and an enum value as its member's name.
    /// </summary>
    [Fact]
    public void Displays_AreReadFromTheConstants()
    {
        var result = GeneratorHarness.Run(
            Rules(
                """
                        rules.AllowedValues(x.Mode, [Modes.Road, "rail"]);
                        rules.AllowedValues(x.Tier, [Tier.Pro, Tier.Enterprise]);
                """
            )
        );

        var region = Region(result);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("\"road, rail\"", region);
        Assert.Contains("\"Pro, Enterprise\"", region);
    }

    [Theory]
    [InlineData("rules.AllowedValues(x.Mode, [Modes.Sea, \"rail\"]);", "Modes.Sea")]
    [InlineData("rules.For(x.Mode).AllowedValues(Modes.Sea, \"rail\");", "Modes.Sea")]
    [InlineData("rules.AllowedValues(x.Mode, [.. Modes.All]);", ".. Modes.All")]
    public void AValueThatIsNotAConstant_IsVM3108(string statement, string value)
    {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3108");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains($"'{value}'", diagnostic.GetMessage());
        Assert.Contains("Declare it const", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ASetHeldInAField_IsVM3108()
    {
        var result = GeneratorHarness.Run(Rules("        rules.AllowedValues(x.Mode, Modes.All);"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3108");

        Assert.Contains("'Modes.All'", diagnostic.GetMessage());
        Assert.Contains("Write the values out as constants", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("rules.AllowedValues(x.Mode, []);")]
    [InlineData("rules.For(x.Mode).AllowedValues();")]
    public void AnEmptySetInARulesClass_IsVM3109(string statement)
    {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3109");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.StartsWith("AllowedValues on 'Mode' lists no values", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("ValidationModules.Constraints")]
    [InlineData("System.ComponentModel.DataAnnotations")]
    public void AnEmptyAllowedValuesAttribute_IsVM3109(string vocabulary)
    {
        var result = GeneratorHarness.Run(
            $$"""
            namespace Sample;

            public sealed record Shipment {
                [{{vocabulary}}.AllowedValues]
                public string? Mode { get; init; }

                [{{vocabulary}}.Required]
                public string? Kind { get; init; }
            }
            """
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3109");

        Assert.StartsWith("[AllowedValues] on 'Mode' lists no values", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// Denying nothing is what an empty list of denied values says, so it is not reported.
    /// </summary>
    [Fact]
    public void AnEmptyDeniedValuesAttribute_IsNotReported()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Shipment {
                [DeniedValues]
                public string? Mode { get; init; }

                [Required]
                public string? Kind { get; init; }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3109");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void AfterAStringEach_TheSetChecksEachElement()
    {
        var result = GeneratorHarness.Run(
            Rules("        rules.Each(x.Tags).AllowedValues(\"a\", \"b\");")
        );

        var region = Region(result);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("!= \"a\" &&", region);
        Assert.Contains("[{", region);
    }
}
