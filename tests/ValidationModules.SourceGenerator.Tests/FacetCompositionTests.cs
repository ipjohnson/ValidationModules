using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>rules.As&lt;TFacet&gt;(x)</c>: validate the subject as one of its facets. One spelling, two
/// bindings - a facet generated in this compilation binds statically; a facet from a referenced
/// assembly resolves the closed <c>IValidatorFor&lt;TFacet&gt;</c> through the pass's services,
/// loudly.
/// </summary>
public class FacetCompositionTests {

    private const string SameCompilation = """
        using ValidationModules;
        using ValidationModules.Constraints;

        namespace Sample;

        [GenerateValidator]
        public interface IAudited {
            [Required] string? CreatedBy { get; }
            [Range(1, 100)] int Version { get; }
        }

        public sealed record Order : IAudited {
            public string? CreatedBy { get; init; }
            public int Version { get; init; }
            public string? Number { get; init; }
        }

        public sealed class OrderRules : IValidationRulesFor<Order> {
            public static void Describe(ValidationRules<Order> rules, Order x) {
                rules.Require(x.Number);
                rules.As<IAudited>(x);
            }
        }
        """;

    [Fact]
    public void ASameCompilationFacet_BindsStaticallyThroughACachedValidator() {
        var result = GeneratorHarness.Run(SameCompilation);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var region = result.Sources["Sample.OrderRules_Rules.g.cs"];

        // Lazily built, cached on the companion, no DI involved - and the path does not push:
        // the facet validates the subject through the same ctx, so its fields report at the
        // current level.
        Assert.Contains("(_facet0 ??= new global::Sample.IAuditedValidator()).Validate(ref ctx, x)", region);
        Assert.DoesNotContain("ctx.Push", region);
        Assert.DoesNotContain("GetService", region);
    }

    [Fact]
    public void TheFacetInterfaceItself_GetsAGeneratedValidator() {
        // [GenerateValidator] already allows AttributeTargets.Interface; the facet's own validator
        // is what the As binds to.
        var result = GeneratorHarness.Run(SameCompilation);

        Assert.Contains(result.Sources.Keys, key => key.Contains("IAuditedValidator"));
    }

    [Fact]
    public void ACrossAssemblyFacet_ResolvesTheClosedServiceAndThrowsNamingTheModule() {
        var shared = GeneratorHarness.CompileToReference("""
            namespace Shared;

            public interface IAudited {
                string? CreatedBy { get; }
            }
            """, "Shared.Contracts");

        var result = GeneratorHarness.Run("""
            using Shared;
            using ValidationModules;

            namespace App;

            public sealed record Order : IAudited {
                public string? CreatedBy { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.As<IAudited>(x);
                }
            }
            """,
            "App",
            OutputKind.DynamicallyLinkedLibrary,
            new[] { shared });

        Assert.Empty(result.CompilationErrors);

        var region = result.Sources["App.OrderRules_Rules.g.cs"];

        // Statically closed: the facet type is written in source, so the service type is closed at
        // build time - no scanning, no MakeGenericType - and failure is loud, naming the module.
        Assert.Contains(
            "(global::ValidationModules.IValidatorFor<global::Shared.IAudited>?)ctx.Services?.GetService(typeof(global::ValidationModules.IValidatorFor<global::Shared.IAudited>))",
            region);
        Assert.Contains("AddSharedContractsValidators()", region);
        Assert.Contains("InvalidOperationException", region);
    }

    [Fact]
    public void ASameCompilationFacetWithNoRules_IsVM3105() {
        // A facet declared here with nothing declaring rules for it would make the As a silent
        // no-op, which is the failure this library refuses everywhere else.
        var result = GeneratorHarness.Run("""
            using ValidationModules;

            namespace Sample;

            public interface IAudited {
                string? CreatedBy { get; }
            }

            public sealed record Order : IAudited {
                public string? CreatedBy { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.As<IAudited>(x);
                }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3105");
    }

    [Fact]
    public void AFacetWhoseRulesComeFromARulesClass_IsSilent() {
        // The facet's rules arrive from another rules class rather than attributes - the pre-scan
        // is what keeps VM3105 from firing on correct code whatever the candidate order.
        var result = GeneratorHarness.Run("""
            using ValidationModules;

            namespace Sample;

            public interface IAudited {
                string? CreatedBy { get; }
            }

            public sealed record Order : IAudited {
                public string? CreatedBy { get; init; }
            }

            public sealed class AuditRules : IValidationRulesFor<IAudited> {
                public static void Describe(ValidationRules<IAudited> rules, IAudited x) {
                    rules.Require(x.CreatedBy);
                }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.As<IAudited>(x);
                }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3105");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void TheArgument_MustBeTheSubject() {
        // A facet of a child is Nested's territory, where the path pushes.
        var result = GeneratorHarness.Run(SameCompilation.Replace(
            "rules.As<IAudited>(x);",
            "rules.As<IAudited>(new Order());"));

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3002");
    }

    [Fact]
    public void AnAsUnderAnIf_IsGuardedLikeAnyIsland() {
        var result = GeneratorHarness.Run(SameCompilation.Replace(
            "rules.As<IAudited>(x);",
            "if (x.Version > 0) { rules.As<IAudited>(x); }"));

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("if (x.Version > 0) {", result.Sources["Sample.OrderRules_Rules.g.cs"]);
    }
}
