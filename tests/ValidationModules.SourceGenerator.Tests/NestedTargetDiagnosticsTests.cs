using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// VM1501 — <c>[ValidateNested]</c> pointing at a type that has nothing to check.
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
    public void NestedTargetWithNoRules_IsVM1501() {
        var result = GeneratorHarness.Run(Model("""
            public record Address {
                public string? PostalCode { get; init; }
            }

            public record Pet {
                [Required] public string? Name { get; init; }
                [ValidateNested] public Address? Home { get; init; }
            }
            """));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1501");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Address", diagnostic.GetMessage());
        Assert.Contains("Home", diagnostic.GetMessage());
    }

    [Fact]
    public void CollectionElementWithNoRules_IsVM1501() {
        // The descent reaches the element type, so that is what the message has to name.
        var result = GeneratorHarness.Run(Model("""
            public record Toy {
                public string? Name { get; init; }
            }

            public record Pet {
                [ValidateNested] public IReadOnlyList<Toy> Toys { get; init; } = new List<Toy>();
            }
            """));

        Assert.Contains("Toy", Assert.Single(result.Diagnostics, d => d.Id == "VM1501").GetMessage());
    }

    [Fact]
    public void DictionaryValueWithNoRules_IsVM1501() {
        var result = GeneratorHarness.Run(Model("""
            public record Product {
                public string? Title { get; init; }
            }

            public record Catalogue {
                [ValidateNested] public IReadOnlyDictionary<string, Product> Items { get; init; } =
                    new Dictionary<string, Product>();
            }
            """));

        Assert.Contains("Product", Assert.Single(result.Diagnostics, d => d.Id == "VM1501").GetMessage());
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

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
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

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
    }

    [Fact]
    public void NestedTargetWhoseRulesComeFromARulesClass_IsSilent() {
        // The case a front end cannot see on its own: Address carries no attribute, and its rules
        // are declared in a different file by a different type. Getting this wrong would make
        // VM1501 fire on correct code, which is why the rules-class lookup is threaded in.
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

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
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

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
    }

    [Fact]
    public void NestedTargetWithNoRules_DropsTheDescentAndBuildsClean() {
        // The half VM1501 used to leave broken: the warning promised "descends into it and
        // validates nothing", but the emitter still wrote a call to AuthorValidator, which was
        // never generated - CS0400 inside generated code. The descent is dropped now, so the
        // behaviour matches the warning's own text.
        var result = GeneratorHarness.Run(Model("""
            public record Author {
                public string? Name { get; init; }
            }

            public record Recipe {
                [Required] public string? Title { get; init; }
                [ValidateNested] public Author? Author { get; init; }
            }
            """));

        Assert.Single(result.Diagnostics, d => d.Id == "VM1501");
        Assert.Empty(result.CompilationErrors);

        var validator = result.Sources["Sample.RecipeValidator.g.cs"];

        Assert.DoesNotContain("AuthorValidator", validator);
        Assert.Contains("Title", validator);
    }

    [Fact]
    public void TypeWhoseOnlyAskWasADroppedDescent_StillGetsAnEmptyValidator() {
        // IValidatorFor<Pet> must still resolve: the warning says the descent validates nothing,
        // not that the type stops being validatable.
        var result = GeneratorHarness.Run(Model("""
            public record Toy {
                public string? Name { get; init; }
            }

            public record Pet {
                [ValidateNested] public Toy? Favourite { get; init; }
            }
            """));

        Assert.Single(result.Diagnostics, d => d.Id == "VM1501");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Sample.PetValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void ListOfLists_IsVM1502AndBuildsClean() {
        // The element of List<List<Section>> is List<Section>: a constructed generic, which can
        // never have a generated validator. Before VM1502 the name reached EmitterOutput.TypeRef,
        // which threw, and the whole generator contributed nothing - reported only as a CS8785
        // warning, so a model-only class library said "Build succeeded" with zero validators.
        var result = GeneratorHarness.Run(Model("""
            public record Section {
                [Required] public string? Name { get; init; }
            }

            public record Document {
                [Required] public string? Title { get; init; }
                [ValidateNested] public List<List<Section>> Sections { get; init; } = new();
            }
            """));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1502");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("List", diagnostic.GetMessage());
        Assert.Contains("Sections", diagnostic.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);

        // Every other validator in the compilation is intact.
        Assert.Contains("Sample.SectionValidator.g.cs", result.Sources.Keys);
        Assert.Contains("Title", result.Sources["Sample.DocumentValidator.g.cs"]);
    }

    [Fact]
    public void ArrayOfArrays_IsVM1502AndBuildsClean() {
        var result = GeneratorHarness.Run(Model("""
            public record Section {
                [Required] public string? Name { get; init; }
            }

            public record Document {
                [ValidateNested] public Section[][] Sections { get; init; } = System.Array.Empty<Section[]>();
            }
            """));

        Assert.Single(result.Diagnostics, d => d.Id == "VM1502");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void NullableValueTypeElement_IsVM1502AndBuildsClean() {
        // The element of List<Money?> is Nullable<Money>, and the descent names its validator
        // without unwrapping - VM1502's remodelling advice applies the same way.
        var result = GeneratorHarness.Run(Model("""
            public record struct Money {
                [Required] public string? Currency { get; init; }
            }

            public record Invoice {
                [ValidateNested] public List<Money?> Lines { get; init; } = new();
            }
            """));

        Assert.Single(result.Diagnostics, d => d.Id == "VM1502");
        Assert.Empty(result.CompilationErrors);
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

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
    }
}
