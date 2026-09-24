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
public class NestedTargetDiagnosticsTests
{
    private static string Model(string body) =>
        $$"""
            using System.Collections.Generic;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            {{body}}
            """;

    [Fact]
    public void NestedTargetWithNoRules_IsVM1501()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Address {
                    public string? PostalCode { get; init; }
                }

                public record Pet {
                    [Required] public string? Name { get; init; }
                    [ValidateNested] public Address? Home { get; init; }
                }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1501");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Address", diagnostic.GetMessage());
        Assert.Contains("Home", diagnostic.GetMessage());
        Assert.Contains("[ValidateNested]", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("[System.ComponentModel.DataAnnotations.Display(Name = \"Street\")]")]
    [InlineData("[System.ComponentModel.DataAnnotations.Key]")]
    [InlineData(
        "[System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Text)]"
    )]
    [InlineData("[System.ComponentModel.DataAnnotations.Editable(false)]")]
    [InlineData("[System.ComponentModel.DataAnnotations.ScaffoldColumn(false)]")]
    [InlineData("[System.ComponentModel.DataAnnotations.Compare(nameof(Other))]")]
    [InlineData("[System.ComponentModel.DataAnnotations.EnumDataType(typeof(Kind))]")]
    public void NestedTargetWhoseOnlyDataAnnotationsAttributeCompilesToNothing_IsVM1501(
        string attribute
    )
    {
        // Each of these describes the property or is reported rather than compiled, so the type
        // gets no validator. A descent kept for it would call an AddressValidator that is never
        // generated, which fails with CS0400 inside generated code.
        var result = GeneratorHarness.Run(
            Model(
                $$"""
                public enum Kind { Home, Work }

                public record Address {
                    {{attribute}}
                    public string? Street { get; init; }
                    public string? Other { get; init; }
                }

                public record Customer {
                    [Required] public string? Name { get; init; }
                    [ValidateNested] public Address? Home { get; init; }
                }
                """
            )
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1501");
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("AddressValidator", result.Sources["Sample.CustomerValidator.g.cs"]);
    }

    [Fact]
    public void NestedTargetWithACompiledDataAnnotationsConstraint_IsSilent()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Address {
                    [System.ComponentModel.DataAnnotations.Required]
                    public string? Street { get; init; }
                }

                public sealed record Customer {
                    [ValidateNested] public Address? Home { get; init; }
                }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("AddressValidator", result.Sources["Sample.CustomerValidator.g.cs"]);
    }

    [Fact]
    public void NestedTargetWithOnlyDataAnnotationsConstraintsUnderIgnore_IsVM1501()
    {
        // Under Ignore the DataAnnotations constraint produces no rule, so Address gets no
        // validator for the descent to call.
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Address {
                    [System.ComponentModel.DataAnnotations.Required]
                    public string? Street { get; init; }
                }

                public record Customer {
                    [Required] public string? Name { get; init; }
                    [ValidateNested] public Address? Home { get; init; }
                }
                """
            ),
            ("ValidationModules_DataAnnotations", "Ignore")
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1501");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void CollectionElementWithNoRules_IsVM1501()
    {
        // The descent reaches the element type, so that is what the message has to name.
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Toy {
                    public string? Name { get; init; }
                }

                public record Pet {
                    [ValidateNested] public IReadOnlyList<Toy> Toys { get; init; } = new List<Toy>();
                }
                """
            )
        );

        Assert.Contains(
            "Toy",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1501").GetMessage()
        );
    }

    [Fact]
    public void DictionaryValueWithNoRules_IsVM1501()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Product {
                    public string? Title { get; init; }
                }

                public record Catalogue {
                    [ValidateNested] public IReadOnlyDictionary<string, Product> Items { get; init; } =
                        new Dictionary<string, Product>();
                }
                """
            )
        );

        Assert.Contains(
            "Product",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1501").GetMessage()
        );
    }

    [Fact]
    public void NestedTargetWithConstraints_IsSilent()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Address {
                    [Required] public string? PostalCode { get; init; }
                }

                public record Pet {
                    [ValidateNested] public Address? Home { get; init; }
                }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
    }

    [Fact]
    public void NestedTargetWithGenerateValidator_IsSilent()
    {
        // The opt-in exists precisely for a type whose rules are not attributes, so honouring it
        // here is what stops the warning contradicting the attribute.
        var result = GeneratorHarness.Run(
            Model(
                """
                [GenerateValidator]
                public record Address {
                    public string? PostalCode { get; init; }
                }

                public record Pet {
                    [ValidateNested] public Address? Home { get; init; }
                }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
    }

    [Fact]
    public void NestedTargetWhoseRulesComeFromARulesClass_IsSilent()
    {
        // The case a front end cannot see on its own: Address carries no attribute, and its rules
        // are declared in a different file by a different type. Getting this wrong would make
        // VM1501 fire on correct code, which is why the rules-class lookup is threaded in.
        var result = GeneratorHarness.Run(
            Model(
                """
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
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
    }

    [Fact]
    public void NestedTargetThatOnlyDescendsFurther_IsSilent()
    {
        // Address has no constraints of its own but does carry [ValidateNested], so it gets a
        // validator and the descent through it reaches something real.
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Region {
                    [Required] public string? Code { get; init; }
                }

                public record Address {
                    [ValidateNested] public Region? Region { get; init; }
                }

                public record Pet {
                    [ValidateNested] public Address? Home { get; init; }
                }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
    }

    [Fact]
    public void NestedTargetWithNoRules_DropsTheDescentAndBuildsClean()
    {
        // The half VM1501 used to leave broken: the warning promised "descends into it and
        // validates nothing", but the emitter still wrote a call to AuthorValidator, which was
        // never generated - CS0400 inside generated code. The descent is dropped now, so the
        // behaviour matches the warning's own text.
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Author {
                    public string? Name { get; init; }
                }

                public record Recipe {
                    [Required] public string? Title { get; init; }
                    [ValidateNested] public Author? Author { get; init; }
                }
                """
            )
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1501");
        Assert.Empty(result.CompilationErrors);

        var validator = result.Sources["Sample.RecipeValidator.g.cs"];

        Assert.DoesNotContain("AuthorValidator", validator);
        Assert.Contains("Title", validator);
    }

    [Fact]
    public void TypeWhoseOnlyAskWasADroppedDescent_StillGetsAnEmptyValidator()
    {
        // IValidatorFor<Pet> must still resolve: the warning says the descent validates nothing,
        // not that the type stops being validatable.
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Toy {
                    public string? Name { get; init; }
                }

                public record Pet {
                    [ValidateNested] public Toy? Favourite { get; init; }
                }
                """
            )
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1501");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Sample.PetValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void ListOfLists_IsVM1502AndBuildsClean()
    {
        // The element of List<List<Section>> is List<Section>: a constructed generic, which can
        // never have a generated validator. Before VM1502 the name reached EmitterOutput.TypeRef,
        // which threw, and the whole generator contributed nothing - reported only as a CS8785
        // warning, so a model-only class library said "Build succeeded" with zero validators.
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Section {
                    [Required] public string? Name { get; init; }
                }

                public record Document {
                    [Required] public string? Title { get; init; }
                    [ValidateNested] public List<List<Section>> Sections { get; init; } = new();
                }
                """
            )
        );

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
    public void ArrayOfArrays_IsVM1502AndBuildsClean()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Section {
                    [Required] public string? Name { get; init; }
                }

                public record Document {
                    [ValidateNested] public Section[][] Sections { get; init; } = System.Array.Empty<Section[]>();
                }
                """
            )
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1502");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void NullableValueTypeElement_IsVM1502AndBuildsClean()
    {
        // The element of List<Money?> is Nullable<Money>, and the descent names its validator
        // without unwrapping - VM1502's remodelling advice applies the same way.
        var result = GeneratorHarness.Run(
            Model(
                """
                public record struct Money {
                    [Required] public string? Currency { get; init; }
                }

                public record Invoice {
                    [ValidateNested] public List<Money?> Lines { get; init; } = new();
                }
                """
            )
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1502");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void NestedTargetFromAnotherAssemblyWithNoValidator_IsVM1505AndBuildsClean()
    {
        // System.Text has no StringBuilderValidator, so a kept descent would fail with CS0234
        // inside generated code.
        var result = GeneratorHarness.Run(
            Model(
                """
                public record Document {
                    [Required] public string? Title { get; init; }
                    [ValidateNested(Polymorphism.DeclaredOnly)] public System.Text.StringBuilder? Body { get; init; }
                }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1505");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("StringBuilderValidator", diagnostic.GetMessage());
        Assert.Contains("[ValidateNested] on 'Body'", diagnostic.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Title", result.Sources["Sample.DocumentValidator.g.cs"]);
    }

    private const string ReferencedAddress = """
        namespace Shared;

        public sealed record Address {
            [ValidationModules.Constraints.Required] public string? Street { get; init; }
        }
        """;

    private const string OrderShippingToAReferencedAddress = """
        using ValidationModules;
        using ValidationModules.Constraints;

        namespace Sample;

        public record Order {
            [Required] public string? Reference { get; init; }
            [ValidateNested] public Shared.Address? ShipTo { get; init; }
        }
        """;

    [Fact]
    public void NestedTargetFromAnotherAssemblyWithItsValidator_KeepsTheDescent()
    {
        // The shape the generator leaves in the other assembly: a public AddressValidator beside
        // Address, with the parameterless constructor the standalone path calls.
        var result = GeneratorHarness.RunWithReference(
            ReferencedAddress
                + """

                public sealed class AddressValidator : ValidationModules.IValidatorFor<Address> {
                    public ValidationModules.ValidationFlow Validate(
                        ref ValidationModules.ValidationContext context, Address value) =>
                        ValidationModules.ValidationFlow.Continue;
                }
                """,
            OrderShippingToAReferencedAddress
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id is "VM1501" or "VM1505");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("AddressValidator", result.Sources["Sample.OrderValidator.g.cs"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(
        """

            internal sealed class AddressValidator : ValidationModules.IValidatorFor<Address> {
                public ValidationModules.ValidationFlow Validate(
                    ref ValidationModules.ValidationContext context, Address value) =>
                    ValidationModules.ValidationFlow.Continue;
            }
            """
    )]
    public void NestedTargetFromAnotherAssemblyWithNoReachableValidator_IsVM1505(string validator)
    {
        // An assembly that never ran the generator has no validator at all, and an internal one
        // cannot be constructed from here. Either way the descent would not compile.
        var result = GeneratorHarness.RunWithReference(
            ReferencedAddress + validator,
            OrderShippingToAReferencedAddress
        );

        Assert.Contains(
            "Address",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1505").GetMessage()
        );
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("AddressValidator", result.Sources["Sample.OrderValidator.g.cs"]);
    }

    [Fact]
    public void NestedTargetFromAnotherAssemblyWithARulesClassHere_KeepsTheDescent()
    {
        // The rules class makes Address's validator in this compilation, so there is something to
        // call even though the other assembly has none.
        var result = GeneratorHarness.RunWithReference(
            ReferencedAddress,
            OrderShippingToAReferencedAddress
                + """

                public sealed class AddressRules : IValidationRulesFor<Shared.Address> {
                    public static void Describe(ValidationRules<Shared.Address> rules, Shared.Address x) {
                        rules.Require(x.Street);
                    }
                }
                """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id is "VM1501" or "VM1505");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("AddressValidator", result.Sources["Sample.OrderValidator.g.cs"]);
    }
}
