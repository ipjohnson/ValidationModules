using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>rules.As&lt;TFacet&gt;(x)</c>: validate the subject as one of its facets. One spelling, two
/// bindings - a facet generated in this compilation binds statically; a facet from a referenced
/// assembly resolves the closed <c>IValidatorFor&lt;TFacet&gt;</c> through the pass's services,
/// loudly.
/// </summary>
public class FacetCompositionTests
{
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
    public void ASameCompilationFacet_BindsStaticallyThroughACachedValidator()
    {
        var result = GeneratorHarness.Run(SameCompilation);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var region = result.Sources["Sample.OrderRules_Rules.g.cs"];

        // Lazily built, cached on the companion, no DI involved - and the path does not push:
        // the facet validates the subject through the same ctx, so its fields report at the
        // current level.
        Assert.Contains(
            "(_facet0 ??= new global::Sample.IAuditedValidator()).Validate(ref ctx, x)",
            region
        );
        Assert.DoesNotContain("ctx.Push", region);
        Assert.DoesNotContain("GetService", region);
    }

    [Fact]
    public void TheFacetInterfaceItself_GetsAGeneratedValidator()
    {
        // [GenerateValidator] already allows AttributeTargets.Interface; the facet's own validator
        // is what the As binds to.
        var result = GeneratorHarness.Run(SameCompilation);

        Assert.Contains(result.Sources.Keys, key => key.Contains("IAuditedValidator"));
    }

    [Fact]
    public void ACrossAssemblyFacet_ResolvesTheClosedServiceAndThrowsNamingTheModule()
    {
        var shared = GeneratorHarness.CompileToReference(
            """
            namespace Shared;

            public interface IAudited {
                string? CreatedBy { get; }
            }
            """,
            "Shared.Contracts"
        );

        var result = GeneratorHarness.Run(
            """
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
            new[] { shared }
        );

        Assert.Empty(result.CompilationErrors);

        var region = result.Sources["App.OrderRules_Rules.g.cs"];

        // Statically closed: the facet type is written in source, so the service type is closed at
        // build time - no scanning, no MakeGenericType - and failure is loud, naming the module.
        Assert.Contains(
            "(global::ValidationModules.IValidatorFor<global::Shared.IAudited>?)ctx.Services?.GetService(typeof(global::ValidationModules.IValidatorFor<global::Shared.IAudited>))",
            region
        );
        Assert.Contains("AddSharedContractsValidators()", region);
        Assert.Contains("InvalidOperationException", region);
    }

    [Fact]
    public void ASameCompilationFacetWithNoRules_IsVM3105()
    {
        // A facet declared here with nothing declaring rules for it would make the As a silent
        // no-op, which is the failure this library refuses everywhere else.
        var result = GeneratorHarness.Run(
            """
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
            """
        );

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3105");
    }

    [Fact]
    public void AFacetWhoseRulesComeFromARulesClass_IsSilent()
    {
        // The facet's rules arrive from another rules class rather than attributes - the pre-scan
        // is what keeps VM3105 from firing on correct code whatever the candidate order.
        var result = GeneratorHarness.Run(
            """
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
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3105");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void TheArgument_MustBeTheSubject()
    {
        // A facet of a child is Nested's territory, where the path pushes.
        var result = GeneratorHarness.Run(
            SameCompilation.Replace("rules.As<IAudited>(x);", "rules.As<IAudited>(new Order());")
        );

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3002");
    }

    [Fact]
    public void AnAsUnderAnIf_IsGuardedLikeAnyIsland()
    {
        var result = GeneratorHarness.Run(
            SameCompilation.Replace(
                "rules.As<IAudited>(x);",
                "if (x.Version > 0) { rules.As<IAudited>(x); }"
            )
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("if (x.Version > 0) {", result.Sources["Sample.OrderRules_Rules.g.cs"]);
    }

    /// <summary>
    /// The model from #91: constraint attributes on the facet's properties, and a rules class that
    /// validates the implementer through the facet.
    /// </summary>
    private const string AttributedFacet = """
        using ValidationModules;
        using ValidationModules.Constraints;

        namespace Sample;

        public interface IAudited {
            [Required] string? CreatedBy { get; }
        }

        public sealed record Invoice : IAudited {
            public string? CreatedBy { get; init; }

            [Required] public string? Carrier { get; init; }
        }

        public sealed class InvoiceRules : IValidationRulesFor<Invoice> {
            public static void Describe(ValidationRules<Invoice> rules, Invoice x) {
                rules.As<IAudited>(x);
            }
        }

        public sealed record Receipt : IAudited {
            public string? CreatedBy { get; init; }
        }
        """;

    /// <summary>
    /// The facet's validator checks its attributes when the <c>As</c> runs it, so the implementer's
    /// validator leaves them out. An implementer with no <c>As</c> still takes them.
    /// </summary>
    [Fact]
    public void AnAttributedFacet_IsCheckedByTheFacetAlone()
    {
        var result = GeneratorHarness.Run(AttributedFacet);

        Assert.Empty(result.CompilationErrors);

        var invoice = result.Sources["Sample.InvoiceValidator.g.cs"];

        Assert.DoesNotContain("value.CreatedBy", invoice);
        Assert.Contains("value.Carrier", invoice);
        Assert.Contains("value.CreatedBy", result.Sources["Sample.IAuditedValidator.g.cs"]);
        Assert.Contains("value.CreatedBy", result.Sources["Sample.ReceiptValidator.g.cs"]);
    }

    /// <summary>
    /// Only the facet's declarations are handed over. The implementer's own declaration of the same
    /// property keeps its constraints.
    /// </summary>
    [Fact]
    public void TheImplementersOwnDeclaration_KeepsItsConstraints()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public interface IAudited {
                [Required] string? CreatedBy { get; }
            }

            public sealed record Invoice : IAudited {
                [StringLength(Max = 3)] public string? CreatedBy { get; init; }
            }

            public sealed class InvoiceRules : IValidationRulesFor<Invoice> {
                public static void Describe(ValidationRules<Invoice> rules, Invoice x) {
                    rules.As<IAudited>(x);
                }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);

        var invoice = result.Sources["Sample.InvoiceValidator.g.cs"];

        Assert.Contains("value.CreatedBy.Length > 3", invoice);
        Assert.DoesNotContain("string.IsNullOrWhiteSpace(value.CreatedBy)", invoice);
    }

    [Fact]
    public void ABaseClassFacet_IsCheckedByTheFacetAlone()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public abstract record Audited {
                [Required] public string? CreatedBy { get; init; }
            }

            public sealed record Invoice : Audited {
                [Required] public string? Carrier { get; init; }
            }

            public sealed class InvoiceRules : IValidationRulesFor<Invoice> {
                public static void Describe(ValidationRules<Invoice> rules, Invoice x) {
                    rules.As<Audited>(x);
                }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);

        var invoice = result.Sources["Sample.InvoiceValidator.g.cs"];

        Assert.DoesNotContain("value.CreatedBy", invoice);
        Assert.Contains("value.Carrier", invoice);
    }

    /// <summary>
    /// A facet from a referenced assembly resolves a validator generated there from the same
    /// declarations, so it is handed over the same way.
    /// </summary>
    [Fact]
    public void ACrossAssemblyAttributedFacet_IsCheckedByTheFacetAlone()
    {
        var shared = GeneratorHarness.CompileToReference(
            """
            using ValidationModules.Constraints;

            namespace Shared;

            public interface IAudited {
                [Required] string? CreatedBy { get; }
            }
            """,
            "Shared.Contracts"
        );

        var result = GeneratorHarness.Run(
            """
            using Shared;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace App;

            public sealed record Order : IAudited {
                public string? CreatedBy { get; init; }

                [Required] public string? Number { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.As<IAudited>(x);
                }
            }
            """,
            "App",
            OutputKind.DynamicallyLinkedLibrary,
            new[] { shared }
        );

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("value.CreatedBy", result.Sources["App.OrderValidator.g.cs"]);
    }

    /// <summary>An <c>As</c> inside a fragment hands the facet over for every caller.</summary>
    [Fact]
    public void AnAsInAFragment_HandsTheFacetOverToTheCaller()
    {
        var result = GeneratorHarness.Run(
            AttributedFacet
                .Replace("rules.As<IAudited>(x);", "Auditing.Standard(rules, x);")
                .Replace(
                    "public sealed record Receipt",
                    """
                    public static class Auditing {
                        public static void Standard(ValidationRules<Invoice> rules, Invoice invoice) {
                            rules.As<IAudited>(invoice);
                        }
                    }

                    public sealed record Receipt
                    """
                )
        );

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("value.CreatedBy", result.Sources["Sample.InvoiceValidator.g.cs"]);
    }
}
