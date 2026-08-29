using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// VM0007 — <c>[ValidateNested]</c> pointing at a type that has nothing to check.
/// </summary>
/// <remarks>
/// This one was declared, released, and reported by nothing for the whole pre-1.0 line, which is
/// exactly the failure it describes: a promise that reads as kept and is not. Most of the tests
/// below are the silent half, because a warning that fires where it should not is worse than one
/// that never fires at all — the author's only remedy would be to delete a correct attribute.
/// </remarks>
public class NestedTargetDiagnosticsTests {

    private static string Model(string body) => $$"""
        using System.Collections.Generic;
        using ValidationModules;
        using ValidationModules.Constraints;

        namespace Sample;

        {{body}}
        """;

    [Fact]
    public void NestedTargetWithNoRules_IsVM0007() {
        var result = GeneratorHarness.Run(Model("""
            public record Address {
                public string? PostalCode { get; init; }
            }

            public record Pet {
                [Required] public string? Name { get; init; }
                [ValidateNested] public Address? Home { get; init; }
            }
            """));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0007");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Address", diagnostic.GetMessage());
        Assert.Contains("Home", diagnostic.GetMessage());
    }

    [Fact]
    public void CollectionElementWithNoRules_IsVM0007() {
        // The descent reaches the element type, so that is what the message has to name.
        var result = GeneratorHarness.Run(Model("""
            public record Toy {
                public string? Name { get; init; }
            }

            public record Pet {
                [ValidateNested] public IReadOnlyList<Toy> Toys { get; init; } = new List<Toy>();
            }
            """));

        Assert.Contains("Toy", Assert.Single(result.Diagnostics, d => d.Id == "VM0007").GetMessage());
    }

    [Fact]
    public void DictionaryValueWithNoRules_IsVM0007() {
        var result = GeneratorHarness.Run(Model("""
            public record Product {
                public string? Title { get; init; }
            }

            public record Catalogue {
                [ValidateNested] public IReadOnlyDictionary<string, Product> Items { get; init; } =
                    new Dictionary<string, Product>();
            }
            """));

        Assert.Contains("Product", Assert.Single(result.Diagnostics, d => d.Id == "VM0007").GetMessage());
    }

    [Fact]
    public void NestedTargetWithConstraints_IsSilent() {
        var result = GeneratorHarness.Run(Model("""
            public record Address {
                [Required] public string? PostalCode { get; init; }
            }

            public record Pet {
                [ValidateNested] public Address? Home { get; init; }
            }
            """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0007");
    }

    [Fact]
    public void NestedTargetWithGenerateValidator_IsSilent() {
        // The opt-in exists precisely for a type whose rules are not attributes, so honouring it
        // here is what stops the warning contradicting the attribute.
        var result = GeneratorHarness.Run(Model("""
            [GenerateValidator]
            public record Address {
                public string? PostalCode { get; init; }
            }

            public record Pet {
                [ValidateNested] public Address? Home { get; init; }
            }
            """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0007");
    }

    [Fact]
    public void NestedTargetWhoseRulesComeFromARulesClass_IsSilent() {
        // The case a front end cannot see on its own: Address carries no attribute, and its rules
        // are declared in a different file by a different type. Getting this wrong would make
        // VM0007 fire on correct code, which is why the rules-class lookup is threaded in.
        var result = GeneratorHarness.Run(Model("""
            public record Address {
                public string? PostalCode { get; init; }
            }

            public sealed class AddressRules : IValidationRulesFor<Address> {
                public static void Describe(ValidationRules<Address> rules, Address x) {
                    rules.Require(x.PostalCode);
                }
            }

            public record Pet {
                [ValidateNested] public Address? Home { get; init; }
            }
            """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0007");
    }

    [Fact]
    public void NestedTargetThatOnlyDescendsFurther_IsSilent() {
        // Address has no constraints of its own but does carry [ValidateNested], so it gets a
        // validator and the descent through it reaches something real.
        var result = GeneratorHarness.Run(Model("""
            public record Region {
                [Required] public string? Code { get; init; }
            }

            public record Address {
                [ValidateNested] public Region? Region { get; init; }
            }

            public record Pet {
                [ValidateNested] public Address? Home { get; init; }
            }
            """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0007");
    }

    [Fact]
    public void NestedTargetFromAnotherAssembly_IsSilent() {
        // string is the stand-in for any type this compilation does not declare: it may carry a
        // validator generated in its own assembly, which is invisible from here. A false negative
        // is the safe direction for a warning.
        var result = GeneratorHarness.Run(Model("""
            public record Pet {
                [Required] public string? Name { get; init; }
                [ValidateNested] public System.Text.StringBuilder? Builder { get; init; }
            }
            """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0007");
    }
}
