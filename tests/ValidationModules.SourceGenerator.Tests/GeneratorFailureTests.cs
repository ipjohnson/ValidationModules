using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// VM5002 — an unhandled exception in an emit stage must fail the build, not surface as the
/// CS8785 warning Roslyn converts it into.
/// </summary>
/// <remarks>
/// The failure mode this closes: in a class library holding only models, nothing references a
/// generated symbol, so a generator that throws produces "Build succeeded" with zero validators
/// and every model silently validates nothing. The rc1015 trial hit it through
/// <c>[ValidateNested]</c> on a <c>List&lt;List&lt;T&gt;&gt;</c>; that trigger is now VM1502, and
/// VM5002 is the backstop for the class.
/// </remarks>
public class GeneratorFailureTests
{
    [Fact]
    public void AnEmitStageThatThrows_IsAVM5002Error()
    {
        // Two types whose names differ only in case get validator files whose hint names differ
        // only in case, and Roslyn compares hint names without regard to case, so the second
        // AddSource throws. That throw is a reachable trigger that no front end refuses, which
        // makes it the honest way to drive the backstop.
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Batch {
                [Required] public string? Name { get; init; }
            }

            public record batch {
                [Required] public string? Name { get; init; }
            }
            """
        );

        var failure = result.Diagnostics.First(d => d.Id == "VM5002");

        Assert.Equal(DiagnosticSeverity.Error, failure.Severity);
        Assert.Contains("batch", failure.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A nested fragment container <c>Order.Shared</c> and a top-level <c>Order_Shared</c> both get
    /// <c>Order_Shared_Fragments</c>, and the second container's <c>AddSource</c> throws. It used
    /// to throw outside the handler, so Roslyn reported CS8785 and dropped every generated file.
    /// </summary>
    [Fact]
    public void AGeneratedFileThatCannotBeAdded_IsAVM5002Error_AndTheRestIsStillGenerated()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class Item { public string? Sku { get; init; } }
            public sealed class Other { public string? Code { get; init; } }
            public record Pet { [Required] public string? Name { get; init; } }

            public static class Order {
                public static class Shared {
                    public static void Skus(ValidationRules<Item> rules, Item x) => rules.Require(x.Sku);
                }
            }

            public static class Order_Shared {
                public static void Codes(ValidationRules<Other> rules, Other x) => rules.Require(x.Code);
            }

            public sealed class ItemRules : IValidationRulesFor<Item> {
                public static void Describe(ValidationRules<Item> rules, Item x) => Order.Shared.Skus(rules, x);
            }

            public sealed class OtherRules : IValidationRulesFor<Other> {
                public static void Describe(ValidationRules<Other> rules, Other x) => Order_Shared.Codes(rules, x);
            }
            """
        );

        var failure = Assert.Single(result.Diagnostics, d => d.Id == "VM5002");

        Assert.Equal(DiagnosticSeverity.Error, failure.Severity);
        Assert.Contains("Sample.Order_Shared_Fragments.g.cs", failure.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CS8785");
        Assert.Contains("Sample.PetValidator.g.cs", result.Sources.Keys);
        Assert.Contains("GeneratedValidatorRegistration.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void AHealthyCompilation_ReportsNoVM5002()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Required] public string? Name { get; init; }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
    }
}
