using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// One rules class, several targets: a class may implement <c>IValidationRulesFor&lt;T&gt;</c> for
/// as many types as it likes, providing one <c>Describe</c> overload per target. Each target still
/// gets its own validator; the regions share one companion file of <c>Describe</c> overloads.
/// </summary>
/// <remarks>
/// The pairing goes through <c>FindImplementationForInterfaceMember</c> rather than a name lookup,
/// which is what lands each overload on its own target whatever order anything is written in - and
/// what makes an explicitly implemented <c>Describe</c> visible at all.
/// </remarks>
public class MultiTargetRulesTests {

    private const string TwoTargets = """
        using ValidationModules;

        namespace Sample;

        public sealed record Order {
            public string? Number { get; init; }
        }

        public sealed record Customer {
            public string? Name { get; init; }
        }

        public sealed class CatalogRules :
            IValidationRulesFor<Order>,
            IValidationRulesFor<Customer> {

            private const int MaxLength = 40;

            public static void Describe(ValidationRules<Order> rules, Order x) {
                rules.Require(x.Number).Length(2, MaxLength);
            }

            public static void Describe(ValidationRules<Customer> rules, Customer x) {
                rules.Require(x.Name).Length(2, MaxLength);
            }
        }
        """;

    [Fact]
    public void TwoTargets_ProduceTwoValidatorsAndOneCompanion() {
        var result = GeneratorHarness.Run(TwoTargets);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        Assert.Contains(result.Sources.Keys, key => key.Contains("OrderValidator"));
        Assert.Contains(result.Sources.Keys, key => key.Contains("CustomerValidator"));

        var companion = result.Sources["Sample.CatalogRules_Rules.g.cs"];

        // One file, two Describe overloads - a second file would collide on the hint name and
        // AddSource throwing would fail the whole generator.
        Assert.Equal(2, companion.Split("public static global::ValidationModules.ValidationFlow Describe(").Length - 1);
        Assert.Contains("global::Sample.Order x", companion);
        Assert.Contains("global::Sample.Customer x", companion);
    }

    [Fact]
    public void EachValidator_CallsItsOwnRegion() {
        var result = GeneratorHarness.Run(TwoTargets);

        Assert.Contains(
            "global::Sample.CatalogRules_Rules.Describe(ref ctx, value)",
            result.Sources["Sample.OrderValidator.g.cs"]);
        Assert.Contains(
            "global::Sample.CatalogRules_Rules.Describe(ref ctx, value)",
            result.Sources["Sample.CustomerValidator.g.cs"]);
    }

    [Fact]
    public void ASharedPrivateConstant_BakesIntoBothRegions() {
        // The cohesion payoff: the class's own members serve every region, under the same
        // qualification and constant-baking rules as a single-target class.
        var companion = GeneratorHarness.Run(TwoTargets).Sources["Sample.CatalogRules_Rules.g.cs"];

        Assert.Equal(2, companion.Split("Length > 40").Length - 1);
    }

    [Fact]
    public void PairingFollowsTheImplementation_NotDeclarationOrder() {
        // Interfaces listed one way, overloads written the other: each region must still carry its
        // own target's rules. Before the interface-member pairing, the first Describe in source
        // was taken for the first interface, whatever its type.
        var result = GeneratorHarness.Run("""
            using ValidationModules;

            namespace Sample;

            public sealed record Order {
                public string? Number { get; init; }
            }

            public sealed record Customer {
                public string? Name { get; init; }
            }

            public sealed class CatalogRules :
                IValidationRulesFor<Order>,
                IValidationRulesFor<Customer> {

                public static void Describe(ValidationRules<Customer> rules, Customer x) {
                    rules.Require(x.Name);
                }

                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.Require(x.Number);
                }
            }
            """);

        Assert.Empty(result.CompilationErrors);

        var companion = result.Sources["Sample.CatalogRules_Rules.g.cs"];

        Assert.Contains("ReportRequired(ctx, \"number\")", companion);
        Assert.Contains("ReportRequired(ctx, \"name\")", companion);
    }

    [Fact]
    public void AnExplicitlyImplementedDescribe_IsRead() {
        // An explicit implementation's metadata name is not "Describe", so the old name lookup
        // never saw it and the class was silently not a rules class at all.
        var result = GeneratorHarness.Run("""
            using ValidationModules;

            namespace Sample;

            public sealed record Order {
                public string? Number { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                static void IValidationRulesFor<Order>.Describe(ValidationRules<Order> rules, Order x) {
                    rules.Require(x.Number);
                }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "ReportRequired(ctx, \"number\")",
            result.Sources["Sample.OrderRules_Rules.g.cs"]);
    }

    [Fact]
    public void ABrokenRegion_DropsOnlyItself() {
        // One target's body fails transcription; the other target still gets its validator, and
        // the diagnostic names the failure.
        var result = GeneratorHarness.Run("""
            using ValidationModules;

            namespace Sample;

            public sealed record Order {
                public string? Number { get; init; }
                public string? Mutable { get; set; }
            }

            public sealed record Customer {
                public string? Name { get; init; }
            }

            public sealed class CatalogRules :
                IValidationRulesFor<Order>,
                IValidationRulesFor<Customer> {

                public static void Describe(ValidationRules<Order> rules, Order x) {
                    x.Mutable = "no";
                }

                public static void Describe(ValidationRules<Customer> rules, Customer x) {
                    rules.Require(x.Name);
                }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0070");
        Assert.Contains(result.Sources.Keys, key => key.Contains("CustomerValidator"));
        Assert.Contains("ReportRequired(ctx, \"name\")", result.Sources["Sample.CatalogRules_Rules.g.cs"]);
    }

    [Fact]
    public void FacetFields_StayDistinctAcrossRegions() {
        // Both regions cache a facet validator; merged into one companion, the fields must not
        // collide - the seed carries the count across writers.
        var result = GeneratorHarness.Run("""
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public interface IAudited {
                string? CreatedBy { get; }
            }

            public sealed class AuditRules : IValidationRulesFor<IAudited> {
                public static void Describe(ValidationRules<IAudited> rules, IAudited x) {
                    rules.Require(x.CreatedBy);
                }
            }

            public sealed record Order : IAudited {
                public string? CreatedBy { get; init; }
            }

            public sealed record Refund : IAudited {
                public string? CreatedBy { get; init; }
            }

            public sealed class CatalogRules :
                IValidationRulesFor<Order>,
                IValidationRulesFor<Refund> {

                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.As<IAudited>(x);
                }

                public static void Describe(ValidationRules<Refund> rules, Refund x) {
                    rules.As<IAudited>(x);
                }
            }
            """);

        Assert.Empty(result.CompilationErrors);

        var companion = result.Sources["Sample.CatalogRules_Rules.g.cs"];

        Assert.Contains("_facet0", companion);
        Assert.Contains("_facet1", companion);
    }
}
