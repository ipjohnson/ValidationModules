using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// VM5002 — an unhandled exception in an emit stage must fail the build, not surface as the
/// CS8785 warning Roslyn converts it into.
/// </summary>
/// <remarks>
/// <para>
/// The failure mode this closes: in a class library holding only models, nothing references a
/// generated symbol, so a generator that throws produces "Build succeeded" with zero validators
/// and every model silently validates nothing. The rc1015 trial hit it through
/// <c>[ValidateNested]</c> on a <c>List&lt;List&lt;T&gt;&gt;</c>; that trigger is now VM1502, and
/// VM5002 is the backstop for the class.
/// </para>
/// <para>
/// A file that is not added must not be named by a file that is, or the build also fails with a
/// C# error inside generated code that points away from the cause. The tests that make an emit
/// throw run through <see cref="GeneratorHarness.RunWithFailingEmit"/>.
/// </para>
/// </remarks>
public class GeneratorFailureTests
{
    private const string Pet = """
        using ValidationModules.Constraints;

        namespace Sample;

        public record Pet {
            [Required] public string? Name { get; init; }
        }
        """;

    /// <summary>
    /// A nested fragment container <c>Order.Shared</c> and a top-level <c>Order_Shared</c> both get
    /// <c>Order_Shared_Fragments</c>, and <c>AddSource</c> refuses the second file. It used to
    /// throw outside the handler, so Roslyn reported CS8785 and dropped every generated file.
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

        var failure = Assert.Single(Errors(result));

        Assert.Equal("VM5002", failure.Id);
        Assert.Contains("Sample.Order_Shared_Fragments.g.cs", failure.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CS8785");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Sample.PetValidator.g.cs", result.Sources.Keys);
        Assert.DoesNotContain("Sample.OtherValidator.g.cs", result.Sources.Keys);
        Assert.DoesNotContain(
            "OtherValidator",
            result.Sources["GeneratedValidatorRegistration.g.cs"]
        );
    }

    [Fact]
    public void AValidatorWhoseEmitThrows_IsNotRegistered()
    {
        var result = GeneratorHarness.RunWithFailingEmit(
            Pet
                + """

                public record Owner {
                    [Required] public string? Name { get; init; }
                }
                """,
            hint => hint == "Sample.PetValidator.g.cs"
        );

        var failure = Assert.Single(Errors(result));

        Assert.Equal("VM5002", failure.Id);
        Assert.Contains("the validator for", failure.GetMessage());
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("Sample.PetValidator.g.cs", result.Sources.Keys);
        Assert.Contains("Sample.OwnerValidator.g.cs", result.Sources.Keys);

        var registration = result.Sources["GeneratedValidatorRegistration.g.cs"];

        Assert.DoesNotContain("PetValidator", registration);
        Assert.Contains("OwnerValidator", registration);
    }

    /// <summary>
    /// A validator that nests another constructs it when no container supplies one, so it names
    /// the nested validator's class.
    /// </summary>
    [Fact]
    public void ANestedValidatorWhoseEmitThrows_TakesTheValidatorThatNestsItWithIt()
    {
        var result = GeneratorHarness.RunWithFailingEmit(
            Pet
                + """

                public record Owner {
                    [Required] public string? Name { get; init; }
                    [ValidateNested(Polymorphism.DeclaredOnly)] public Pet? Pet { get; init; }
                }

                public record Tag {
                    [Required] public string? Label { get; init; }
                }
                """,
            hint => hint == "Sample.PetValidator.g.cs"
        );

        Assert.Equal("VM5002", Assert.Single(Errors(result)).Id);
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("Sample.OwnerValidator.g.cs", result.Sources.Keys);
        Assert.Contains("Sample.TagValidator.g.cs", result.Sources.Keys);

        var registration = result.Sources["GeneratedValidatorRegistration.g.cs"];

        Assert.DoesNotContain("OwnerValidator", registration);
        Assert.Contains("TagValidator", registration);
    }

    [Fact]
    public void ACompanionWhoseEmitThrows_TakesItsValidatorWithIt()
    {
        var result = GeneratorHarness.RunWithFailingEmit(
            Pet
                + """

                public sealed record Order { public string? Number { get; init; } }

                public sealed class OrderRules : ValidationModules.IValidationRulesFor<Order> {
                    public static void Describe(ValidationModules.ValidationRules<Order> rules, Order x) {
                        rules.Require(x.Number);
                    }
                }
                """,
            hint => hint == "Sample.OrderRules_Rules.g.cs"
        );

        var failure = Assert.Single(Errors(result));

        Assert.Equal("VM5002", failure.Id);
        Assert.Contains("Sample.OrderRules_Rules.g.cs", failure.GetMessage());
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("Sample.OrderValidator.g.cs", result.Sources.Keys);
        Assert.Contains("Sample.PetValidator.g.cs", result.Sources.Keys);

        var registration = result.Sources["GeneratedValidatorRegistration.g.cs"];

        Assert.DoesNotContain("OrderValidator", registration);
        Assert.Contains("PetValidator", registration);
    }

    [Fact]
    public void AFragmentContainerWhoseEmitThrows_TakesItsCallersWithIt()
    {
        var result = GeneratorHarness.RunWithFailingEmit(
            Pet
                + """

                public sealed record Order { public string? Number { get; init; } }

                public static class Shared {
                    public static void Numbered(ValidationModules.ValidationRules<Order> rules, Order x) =>
                        rules.Require(x.Number);
                }

                public sealed class OrderRules : ValidationModules.IValidationRulesFor<Order> {
                    public static void Describe(ValidationModules.ValidationRules<Order> rules, Order x) =>
                        Shared.Numbered(rules, x);
                }
                """,
            hint => hint == "Sample.Shared_Fragments.g.cs"
        );

        Assert.Equal("VM5002", Assert.Single(Errors(result)).Id);
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("Sample.OrderRules_Rules.g.cs", result.Sources.Keys);
        Assert.DoesNotContain("Sample.OrderValidator.g.cs", result.Sources.Keys);
        Assert.Contains("Sample.PetValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void ALanguagePackWhoseEmitThrows_IsNotRegistered()
    {
        var result = GeneratorHarness.RunWithFailingEmit(
            Pet,
            hint => hint.StartsWith("LanguagePack.", StringComparison.Ordinal),
            [
                (
                    "fr.validation-messages.json",
                    """{ "culture": "fr", "templates": { "required": "{field} est obligatoire." } }"""
                ),
            ]
        );

        var failure = Assert.Single(Errors(result));

        Assert.Equal("VM5002", failure.Id);
        Assert.Contains("fr.validation-messages.json", failure.GetMessage());
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(
            "FrLanguagePack0",
            result.Sources["GeneratedValidatorRegistration.g.cs"]
        );
    }

    [Fact]
    public void AHealthyCompilation_ReportsNoVM5002()
    {
        var result = GeneratorHarness.Run(Pet);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
    }

    private static IEnumerable<Diagnostic> Errors(GeneratorHarness.Result result) =>
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
}
