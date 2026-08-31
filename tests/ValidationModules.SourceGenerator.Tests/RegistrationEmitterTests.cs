using ValidationModules.SourceGenerator.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The two registration branches from plan §7.3, and the switch that chooses between them.
/// </summary>
/// <remarks>
/// Both branches share one emitter for the body and differ only in the wrapper, which is what makes
/// a golden file per branch worth having: the interesting failure is one branch drifting from the
/// other, and that is visible in a diff and invisible in a substring assertion.
/// </remarks>
public class RegistrationEmitterTests {

    private const string TwoTypes = """
        using ValidationModules.Constraints;

        namespace Sample;

        public record Address {
            [Required]
            public string? Street { get; init; }
        }

        public record Pet {
            [Required]
            public string? Name { get; init; }
        }
        """;

    /// <param name="compiles">
    /// False for the DependencyModules branch alone. Forcing that branch emits a module against
    /// <c>IDependencyModule</c>, and the harness compilation deliberately does not reference
    /// DependencyModules — that reference is what the probe in the generator tests for, so adding it
    /// would make the auto-detection tests below unable to observe the negative answer. The emitted
    /// text is still worth pinning; the integration project under integ-tests/ is what proves the
    /// module compiles and runs.
    /// </param>
    private static string Registration(bool compiles, params (string Key, string Value)[] properties) {
        var result = GeneratorHarness.Run(TwoTypes, properties);

        if (compiles) {
            Assert.Empty(result.CompilationErrors);
        }

        return result.Sources.TryGetValue("GeneratedValidatorRegistration.g.cs", out var source)
            ? source
            : "<no registration emitted>";
    }

    private static string Registration(params (string Key, string Value)[] properties) =>
        Registration(compiles: true, properties);

    [Fact]
    public void ServiceCollectionBranch_EmitsAnIServiceCollectionExtension() {
        // Strongly typed AddSingleton calls rather than a table of (Type, factory) records: the
        // table erased the generic, allocated closures at static init to iterate once at startup,
        // and lived in a class the consumer had to know the name of.
        Snapshot.Match(Registration(("ValidationModules_Registration", "ServiceCollection")));
    }

    [Fact]
    public void DependencyModulesBranch_EmitsAModuleWrappingTheSameExtension() {
        // One body, two wrappers — plan §7.3. The module is a single call into the extension rather
        // than a second copy of the registrations, so the two branches cannot drift.
        Snapshot.Match(Registration(compiles: false, ("ValidationModules_Registration", "DependencyModules")));
    }

    [Fact]
    public void DependencyModulesBranch_DelegatesRatherThanRepeatingTheRegistrations() {
        var source = Registration(compiles: false, ("ValidationModules_Registration", "DependencyModules"));

        Assert.Contains(
            "global::Microsoft.Extensions.DependencyInjection.GeneratorTestsValidationExtensions.AddGeneratorTestsValidators(services);",
            source);

        // The validator registrations appear once, in the extension — not again inside the module.
        // Anchored on the qualified ServiceCollectionServiceExtensions call rather than bare
        // "AddSingleton<", which also matches inside the TryAddSingleton that registers the namer.
        Assert.Equal(
            2,
            source.Split("global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<").Length - 1);
    }

    [Fact]
    public void NoneEmitsNothingAtAll() {
        // The escape hatch for DM arriving transitively into a project that does not want its
        // validators in a module.
        Assert.Equal("<no registration emitted>", Registration(("ValidationModules_Registration", "None")));
    }

    [Fact]
    public void WithoutDependencyModulesReferenced_TheDefaultIsTheServiceCollectionBranch() {
        // The harness compilation does not reference DependencyModules, so this is the probe's
        // negative answer rather than an explicit setting.
        Assert.Contains("AddGeneratorTestsValidators", Registration());
        Assert.DoesNotContain("IDependencyModule", Registration());
    }

    [Fact]
    public void TheExtensionLandsInTheDependencyInjectionNamespace() {
        // Where a composition root has already imported, so the method turns up on IntelliSense
        // after `services.Add` without a second using.
        Assert.Contains("namespace Microsoft.Extensions.DependencyInjection", Registration());
    }

    [Fact]
    public void NoValidatorRegistrationTableIsEmitted() {
        // ValidatorRegistration and AddValidationModules(IReadOnlyList<…>) stay in the runtime for
        // anyone hand-building a table. Nothing generates one.
        var source = Registration();

        Assert.DoesNotContain("GeneratedValidators", source);
        Assert.DoesNotContain("ValidatorRegistration", source);
    }

    [Theory]
    [InlineData(null, "CamelCaseFieldNamer")]
    [InlineData("SnakeCase", "SnakeCaseFieldNamer")]
    [InlineData("PascalCase", "PascalCaseFieldNamer")]
    [InlineData("AsDeclared", "PascalCaseFieldNamer")]
    public void TheRegisteredNamerMatchesThePolicyTheLiteralsWereEmittedWith(string? policy, string expected) {
        // Field names are baked in at build time, so a namer resolved at run time that disagrees
        // with them puts one field on the wire under two spellings.
        var source = policy is null
            ? Registration()
            : Registration(("ValidationModules_FieldNaming", policy));

        Assert.Contains(
            $"TryAddSingleton<global::ValidationModules.Naming.IValidationFieldNamer>(services, global::ValidationModules.Naming.{expected}.Instance)",
            source);
    }

    [Fact]
    public void RegistrationIsOrderedByNamespaceThenValidatorName() {
        // Ordered so the emitted table does not reshuffle between builds, which would turn every
        // incremental compile into a diff. Namespace first, because a validator name is not unique
        // on its own — two namespaces may each declare a Customer.
        // Block-scoped, because two file-scoped namespaces cannot share a file — and declared with
        // Beta first so passing cannot be an accident of source order.
        var source = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Beta {
                public record Zebra {
                    [Required]
                    public string? Name { get; init; }
                }

                public record Alpaca {
                    [Required]
                    public string? Name { get; init; }
                }
            }

            namespace Alpha {
                public record Yak {
                    [Required]
                    public string? Name { get; init; }
                }
            }
            """).Sources["GeneratedValidatorRegistration.g.cs"];

        var order = new[] { "Alpha.YakValidator", "Beta.AlpacaValidator", "Beta.ZebraValidator" }
            .Select(name => source.IndexOf(name, StringComparison.Ordinal))
            .ToList();

        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.OrderBy(index => index).ToList(), order);
    }

    [Fact]
    public void NoValidatedTypes_EmitsNoRegistrationRatherThanAnEmptyTable() {
        var result = GeneratorHarness.Run("""
            namespace Sample;

            public record Pet {
                public string? Name { get; init; }
            }
            """);

        Assert.DoesNotContain("GeneratedValidatorRegistration.g.cs", result.Sources.Keys);
    }

    [Theory]
    [InlineData("My-App", "AddMyAppValidators")]
    [InlineData("app2-signupapi", "AddApp2SignupapiValidators")]
    [InlineData("7Eleven", "Add_7ElevenValidators")]
    [InlineData("My..App", "AddMyAppValidators")]
    [InlineData("My.App", "AddMyAppValidators")]
    [InlineData("my_lib", "AddMyLibValidators")]
    public void AssemblyNameThatIsNotAnIdentifier_IsSanitizedIntoTheMethodName(
        string assemblyName, string expected) {

        // "My-App" once emitted `namespace My-App;` and broke the consumer's build in generated
        // code; the same sanitization now has to survive being spliced into a method name, where
        // a dot is illegal as well. Segments PascalCase on the way in - app2-signupapi used to
        // name the method Addapp2_signupapiValidators, an ugly public identifier whose casing was
        // decided before 1.0.0 froze it.
        var source = GeneratorHarness.Run(TwoTypes, assemblyName)
            .Sources["GeneratedValidatorRegistration.g.cs"];

        Assert.Contains(
            $"public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection {expected}(",
            source);
    }
}
