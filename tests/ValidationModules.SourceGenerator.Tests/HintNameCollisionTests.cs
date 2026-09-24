using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Pins that two same-named types in different namespaces both get a validator.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn requires the hint name passed to <c>AddSource</c> to be unique per generator, and throws
/// an <see cref="ArgumentException"/> when it is not. That throw is not contained: it fails the
/// whole generator, so every validator in the assembly disappears and the build collapses into a
/// wall of CS0103s pointing at types that were supposed to be generated - none of which names the
/// duplicate.
/// </para>
/// <para>
/// Naming the file after the validator alone made that reachable with two ordinary types.
/// <c>Api.V1.Customer</c> alongside <c>Api.V2.Customer</c> is not an exotic arrangement; it is the
/// shape §6 of the plan is built around, so this is the case profiles walk straight into.
/// </para>
/// </remarks>
public class HintNameCollisionTests
{
    private const string SameNameInTwoNamespaces = """
        using ValidationModules.Constraints;

        namespace Api.V1 {
            public record Customer {
                [Required] public string? Name { get; init; }
            }
        }

        namespace Api.V2 {
            public record Customer {
                [Required] public string? Name { get; init; }
                [Required] public string? Tier { get; init; }
            }
        }
        """;

    [Fact]
    public void Generate_SameTypeNameInTwoNamespaces_EmitsBothValidators()
    {
        var result = GeneratorHarness.Run(SameNameInTwoNamespaces);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Api.V1.CustomerValidator.g.cs", result.Sources.Keys);
        Assert.Contains("Api.V2.CustomerValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void Generate_SameTypeNameInTwoNamespaces_KeepsEachValidatorInItsOwnNamespace()
    {
        var result = GeneratorHarness.Run(SameNameInTwoNamespaces);

        Assert.Contains("namespace Api.V1;", result.Sources["Api.V1.CustomerValidator.g.cs"]);
        Assert.Contains("namespace Api.V2;", result.Sources["Api.V2.CustomerValidator.g.cs"]);

        // The rule that differs between them, so this is not passing on the namespace header alone.
        Assert.DoesNotContain("tier", result.Sources["Api.V1.CustomerValidator.g.cs"]);
        Assert.Contains("tier", result.Sources["Api.V2.CustomerValidator.g.cs"]);
    }

    [Fact]
    public void Generate_SameTypeNameInTwoNamespaces_RegistersBoth()
    {
        var result = GeneratorHarness.Run(SameNameInTwoNamespaces);

        var registration = result.Sources["GeneratedValidatorRegistration.g.cs"];

        // Registered by implementation type, so the container constructs each and injects whatever
        // validates its nested properties.
        Assert.Contains("global::Api.V1.CustomerValidator>(services)", registration);
        Assert.Contains("global::Api.V2.CustomerValidator>(services)", registration);
    }

    /// <summary>
    /// A partial type reaches the generator once per declaration. Building it once per declaration
    /// added its validator twice, and the second <c>AddSource</c> threw on the duplicate hint name.
    /// </summary>
    [Fact]
    public void Generate_PartialTypeDeclaredInTwoParts_EmitsOneValidatorCheckingBoth()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public partial record Pet {
                [Required] public string? Name { get; init; }
            }

            public partial record Pet {
                [Required] public string? Tag { get; init; }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);

        var validator = result.Sources["Sample.PetValidator.g.cs"];

        Assert.Contains("\"name\"", validator);
        Assert.Contains("\"tag\"", validator);
    }

    /// <summary>
    /// A partial rules class was read once per declaration, so its companion declared the same
    /// <c>Describe</c> overload twice and the validator's call to it was ambiguous.
    /// </summary>
    [Fact]
    public void Generate_PartialRulesClassDeclaredInTwoParts_EmitsOneRegion()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules;

            namespace Sample;

            public record Pet {
                public string? Name { get; init; }
            }

            public sealed partial class PetRules : IValidationRulesFor<Pet> {
                public static void Describe(ValidationRules<Pet> rules, Pet x) => rules.Require(x.Name);
            }

            public sealed partial class PetRules { }
            """
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Equal(
            1,
            result
                .Sources["Sample.PetRules_Rules.g.cs"]
                .Split("public static global::ValidationModules.ValidationFlow Describe(")
                .Length - 1
        );
    }

    /// <summary>
    /// A partial subtype was indexed under its base once per declaration, so a compile-time
    /// dispatch over the base matched it in two switch arms.
    /// </summary>
    [Fact]
    public void Generate_PartialSubtypeDeclaredInTwoParts_IsDispatchedOnce()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public class Item {
                [Required] public string? Sku { get; init; }
            }

            public sealed partial class Bundle : Item {
                [Required] public string? Name { get; init; }
            }

            public sealed partial class Bundle { }

            public sealed class Order {
                [ValidateNested(Polymorphism.CompileTime)] public Item? Line { get; init; }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// The global namespace has no prefix to qualify with, so its validators keep the bare file name
    /// - and a global-namespace type must still not collide with a namespaced one of the same name.
    /// </summary>
    [Fact]
    public void Generate_GlobalNamespaceAlongsideNamespaced_EmitsBoth()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            public record Customer {
                [Required] public string? Name { get; init; }
            }

            namespace Api.V1 {
                public record Customer {
                    [Required] public string? Name { get; init; }
                }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("CustomerValidator.g.cs", result.Sources.Keys);
        Assert.Contains("Api.V1.CustomerValidator.g.cs", result.Sources.Keys);
    }

    /// <summary>
    /// Roslyn compares hint names without regard to case, so <c>Batch</c> and <c>batch</c> made the
    /// second <c>AddSource</c> throw, and the registration referred to the validator that was never
    /// added. The first file name in ordinal order is kept, and the other is numbered.
    /// </summary>
    [Fact]
    public void Generate_TypeNamesThatDifferOnlyInCase_EmitsBothValidators()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Batch {
                [Required] public string? A { get; init; }
            }

            public record batch {
                [Required] public string? B { get; init; }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("class BatchValidator", result.Sources["Sample.BatchValidator.g.cs"]);
        Assert.Contains("class batchValidator", result.Sources["Sample.batchValidator.2.g.cs"]);
    }

    /// <summary>
    /// The number follows ordinal order rather than declaration order, so moving a declaration does
    /// not rename a file.
    /// </summary>
    [Fact]
    public void Generate_TypeNamesThatDifferOnlyInCase_GetTheSameFileNamesInEitherOrder()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record batch {
                [Required] public string? B { get; init; }
            }

            public record Batch {
                [Required] public string? A { get; init; }
            }
            """
        );

        Assert.Contains("class BatchValidator", result.Sources["Sample.BatchValidator.g.cs"]);
        Assert.Contains("class batchValidator", result.Sources["Sample.batchValidator.2.g.cs"]);
    }

    [Fact]
    public void Generate_NamespacesThatDifferOnlyInCase_EmitsBothValidators()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Api {
                public record Customer {
                    [Required] public string? Name { get; init; }
                }
            }

            namespace api {
                public record Customer {
                    [Required] public string? Name { get; init; }
                }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("namespace Api;", result.Sources["Api.CustomerValidator.g.cs"]);
        Assert.Contains("namespace api;", result.Sources["api.CustomerValidator.2.g.cs"]);
    }

    /// <summary>
    /// A rules class's companion and a fragment container are named after a type too. Their
    /// <c>AddSource</c> threw outside the VM5002 handler, so Roslyn dropped every generated file.
    /// </summary>
    [Fact]
    public void Generate_CompanionsAndFragmentContainersThatDifferOnlyInCase_AreBothEmitted()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules;

            namespace Sample;

            public sealed class Pet {
                public string? Name { get; init; }
            }

            public sealed class Dog {
                public string? Name { get; init; }
            }

            public static class Shared {
                public static void Named(ValidationRules<Pet> rules, Pet x) => rules.Require(x.Name);
            }

            public static class shared {
                public static void Named(ValidationRules<Dog> rules, Dog x) => rules.Require(x.Name);
            }

            public sealed class PetRules : IValidationRulesFor<Pet> {
                public static void Describe(ValidationRules<Pet> rules, Pet x) => Shared.Named(rules, x);
            }

            public sealed class petRules : IValidationRulesFor<Dog> {
                public static void Describe(ValidationRules<Dog> rules, Dog x) => shared.Named(rules, x);
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id is "VM5002" or "CS8785");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("class PetRules_Rules", result.Sources["Sample.PetRules_Rules.g.cs"]);
        Assert.Contains("class petRules_Rules", result.Sources["Sample.petRules_Rules.2.g.cs"]);
        Assert.Contains("class Shared_Fragments", result.Sources["Sample.Shared_Fragments.g.cs"]);
        Assert.Contains("class shared_Fragments", result.Sources["Sample.shared_Fragments.2.g.cs"]);
    }
}
