using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Fragments: decomposition and reuse as method extraction, read by the generator. A static, void,
/// same-compilation method that receives the builder is followed; its body is expanded into a
/// method in its declaring type's container - carrying the fragment file's own usings - and called
/// in place, one instantiation per concrete target.
/// </summary>
public class FragmentTests {

    private const string Audited = """
        using System;
        using System.Text.Json.Serialization;
        using ValidationModules;

        namespace Sample;

        public interface IAudited {
            string? CreatedBy { get; }
            int Version { get; }
        }

        public sealed record Order : IAudited {
            [JsonPropertyName("created_by")] public string? CreatedBy { get; init; }
            public int Version { get; init; }
            public string? Number { get; init; }
            public int Tier { get; init; }
        }

        public static class AuditRules {
            // The mixin the attributes never had: every audited type gets these rules, said once.
            public static void Standard<T>(ValidationRules<T> rules, T audited) where T : IAudited {
                rules.Require(audited.CreatedBy);
                rules.RangeAtLeast(audited.Version, 1);
            }
        }
        """;

    private static GeneratorHarness.Result Run(string describeBody, string extra = "") =>
        GeneratorHarness.Run(Audited + $$"""


            {{extra}}
            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
            {{describeBody}}
                }
            }
            """);

    private static GeneratorHarness.Result Clean(string describeBody, string extra = "") {
        var result = Run(describeBody, extra);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return result;
    }

    [Fact]
    public void AGenericFragment_IsStampedPerConcreteTargetAndCalledInPlace() {
        var result = Clean("        AuditRules.Standard(rules, x);");

        var container = result.Sources["Sample.AuditRules_Fragments.g.cs"];
        var region = result.Sources["Sample.OrderRules_Rules.g.cs"];

        Assert.Contains("Standard_Order", container);
        Assert.Contains("global::Sample.AuditRules_Fragments.Standard_Order(ref ctx, x)", region);
    }

    [Fact]
    public void AGenericFragment_ResolvesWireNamesAgainstTheConcreteImplementer() {
        // The member binds through the constraint interface, but [JsonPropertyName] on Order's
        // implementing property is what the wire sees - the point of stamping per concrete type.
        var result = Clean("        AuditRules.Standard(rules, x);");

        Assert.Contains("\"created_by\"", result.Sources["Sample.AuditRules_Fragments.g.cs"]);
    }

    [Fact]
    public void AFragmentsOwnParameterNames_SurviveTranscription() {
        // The fragment names its subject `audited`, not `x`; the emitted method reuses the
        // fragment's names so its body needs no identifier rewriting.
        var result = Clean("        AuditRules.Standard(rules, x);");

        Assert.Contains("audited.CreatedBy", result.Sources["Sample.AuditRules_Fragments.g.cs"]);
    }

    [Fact]
    public void TwoCallers_ShareOneInstantiation() {
        var result = Clean(
            "        AuditRules.Standard(rules, x);",
            """
            public sealed class OrderAuditRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    AuditRules.Standard(rules, x);
                }
            }

            """);

        var container = result.Sources["Sample.AuditRules_Fragments.g.cs"];

        Assert.Equal(1, container.Split("public static global::ValidationModules.ValidationFlow Standard_Order").Length - 1);
    }

    [Fact]
    public void ExtraParameters_BindAtTheCallSite() {
        var result = Clean(
            "        CustomsRules.Declare(rules, x, strict: x.Tier > 2);",
            """
            public static class CustomsRules {
                public static void Declare(ValidationRules<Order> rules, Order order, bool strict) {
                    if (strict) {
                        rules.Require(order.Number);
                    }
                }
            }

            """);

        var region = result.Sources["Sample.OrderRules_Rules.g.cs"];
        var container = result.Sources["Sample.CustomsRules_Fragments.g.cs"];

        Assert.Contains("global::Sample.CustomsRules_Fragments.Declare(ref ctx, x, x.Tier > 2)", region);
        Assert.Contains("if (strict) {", container);
    }

    [Fact]
    public void AFragmentMayCallAFragment() {
        var result = Clean(
            "        Outer.Declare(rules, x);",
            """
            public static class Outer {
                public static void Declare(ValidationRules<Order> rules, Order order) {
                    rules.Require(order.Number);
                    AuditRules.Standard(rules, order);
                }
            }

            """);

        Assert.Contains(
            "global::Sample.AuditRules_Fragments.Standard_Order(ref ctx, order)",
            result.Sources["Sample.Outer_Fragments.g.cs"]);
    }

    [Fact]
    public void AFragmentCycle_IsVM0086() {
        var result = Run(
            "        Left.Declare(rules, x);",
            """
            public static class Left {
                public static void Declare(ValidationRules<Order> rules, Order order) =>
                    Right.Declare(rules, order);
            }

            public static class Right {
                public static void Declare(ValidationRules<Order> rules, Order order) =>
                    Left.Declare(rules, order);
            }

            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0086");
    }

    [Fact]
    public void ACrossAssemblyFragment_IsVM0085WithTheSourcePackageFix() {
        var shared = GeneratorHarness.CompileToReference("""
            using ValidationModules;

            namespace Shared;

            public sealed record Widget {
                public string? Name { get; init; }
            }

            public static class SharedRules {
                public static void Standard(ValidationRules<Widget> rules, Widget widget) {
                    rules.Require(widget.Name);
                }
            }
            """, "Shared");

        var result = GeneratorHarness.Run("""
            using Shared;
            using ValidationModules;

            namespace App;

            public sealed class WidgetRules : IValidationRulesFor<Widget> {
                public static void Describe(ValidationRules<Widget> rules, Widget x) {
                    SharedRules.Standard(rules, x);
                }
            }
            """,
            "App",
            OutputKind.DynamicallyLinkedLibrary,
            new[] { shared });

        var reported = Assert.Single(result.Diagnostics, d => d.Id == "VM0085");

        Assert.Contains("SharedRules.Standard", reported.GetMessage());
        Assert.Contains("source", reported.GetMessage());
    }

    [Fact]
    public void AnExplicitInterfaceImplementation_IsVM0088RatherThanAnErrorInGeneratedCode() {
        var result = GeneratorHarness.Run("""
            using ValidationModules;

            namespace Sample;

            public interface IAudited {
                string? CreatedBy { get; }
            }

            public sealed record Order : IAudited {
                string? IAudited.CreatedBy => Number;
                public string? Number { get; init; }
            }

            public static class AuditRules {
                public static void Standard<T>(ValidationRules<T> rules, T audited) where T : IAudited {
                    rules.Require(audited.CreatedBy);
                }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    AuditRules.Standard(rules, x);
                }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0088" && d.GetMessage().Contains("explicitly"));
    }

    [Fact]
    public void TheSubjectArgument_MustBeTheDescribeSubject() {
        // A facet of a child is Nested's territory, where the path pushes.
        var result = Run(
            """
                    var other = new Order();
                    AuditRules.Standard(rules, other);
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0087");
    }

    [Fact]
    public void ADescentInsideAFragment_IsRejected() {
        var result = Run(
            "        Shipping.Declare(rules, x);",
            """
            public sealed record Address {
                public string? Line1 { get; init; }
            }

            public static class Shipping {
                public static void Declare(ValidationRules<Order> rules, Order order) {
                    rules.Nested(order.Number);
                }
            }

            """);

        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AnEarlyReturnInAFragment_EndsTheFragmentOnly() {
        var result = Clean(
            """
                    Gate.Declare(rules, x);
                    rules.Require(x.Number);
            """,
            """
            public static class Gate {
                public static void Declare(ValidationRules<Order> rules, Order order) {
                    if (order.Version == 0) {
                        return;
                    }

                    rules.Require(order.CreatedBy);
                }
            }

            """);

        var container = result.Sources["Sample.Gate_Fragments.g.cs"];
        var region = result.Sources["Sample.OrderRules_Rules.g.cs"];

        // The fragment's return is Continue - the caller's next statement still runs.
        Assert.Contains("return global::ValidationModules.ValidationFlow.Continue;", container);
        Assert.Contains("ReportRequired(ctx, \"number\")", region);
    }
}
