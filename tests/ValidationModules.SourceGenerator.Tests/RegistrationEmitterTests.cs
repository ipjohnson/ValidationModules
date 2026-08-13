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
    public void ServiceCollectionBranch_EmitsAStaticTableOfFactories() {
        // Factory delegates rather than (Type, Type) pairs, so nothing resolves through
        // ActivatorUtilities' constructor reflection.
        Snapshot.Match(Registration(("ValidationModules_Registration", "ServiceCollection")));
    }

    [Fact]
    public void DependencyModulesBranch_EmitsACompleteModule() {
        // IDependencyModule has exactly one member without a default implementation, so no DM
        // generator involvement is needed and there is no partial left to complete.
        Snapshot.Match(Registration(compiles: false, ("ValidationModules_Registration", "DependencyModules")));
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
        Assert.Contains("GeneratedValidators", Registration());
        Assert.DoesNotContain("IDependencyModule", Registration());
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
    [InlineData("My-App", "My_App")]
    [InlineData("7Eleven", "_7Eleven")]
    [InlineData("My..App", "My.App")]
    public void AssemblyNameThatIsNotAValidNamespace_IsSanitized(string assemblyName, string expected) {
        // "My-App" emitted `namespace My-App;` and broke the consumer's build in generated code.
        var source = GeneratorHarness.Run(TwoTypes, assemblyName)
            .Sources["GeneratedValidatorRegistration.g.cs"];

        Assert.Contains($"namespace {expected};", source);
    }
}
