using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Fragments: decomposition and reuse as method extraction, read by the generator. A static, void,
/// same-compilation method that receives the builder is followed; its body is expanded into a
/// method in its declaring type's container - carrying the fragment file's own usings - and called
/// in place, one instantiation per concrete target.
/// </summary>
public class FragmentTests
{
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
        GeneratorHarness.Run(
            Audited
                + $$"""


                {{extra}}
                public sealed class OrderRules : IValidationRulesFor<Order> {
                    public static void Describe(ValidationRules<Order> rules, Order x) {
                {{describeBody}}
                    }
                }
                """
        );

    private static GeneratorHarness.Result Clean(string describeBody, string extra = "")
    {
        var result = Run(describeBody, extra);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return result;
    }

    [Fact]
    public void AGenericFragment_IsStampedPerConcreteTargetAndCalledInPlace()
    {
        var result = Clean("        AuditRules.Standard(rules, x);");

        var container = result.Sources["Sample.AuditRules_Fragments.g.cs"];
        var region = result.Sources["Sample.OrderRules_Rules.g.cs"];

        Assert.Contains("Standard_Order", container);
        Assert.Contains("global::Sample.AuditRules_Fragments.Standard_Order(ref ctx, x)", region);
    }

    [Fact]
    public void AGenericFragment_ResolvesWireNamesAgainstTheConcreteImplementer()
    {
        // The member binds through the constraint interface, but [JsonPropertyName] on Order's
        // implementing property is what the wire sees - the point of stamping per concrete type.
        var result = Clean("        AuditRules.Standard(rules, x);");

        Assert.Contains("\"created_by\"", result.Sources["Sample.AuditRules_Fragments.g.cs"]);
    }

    [Fact]
    public void AFragmentsOwnParameterNames_SurviveTranscription()
    {
        // The fragment names its subject `audited`, not `x`; the emitted method reuses the
        // fragment's names so its body needs no identifier rewriting.
        var result = Clean("        AuditRules.Standard(rules, x);");

        Assert.Contains("audited.CreatedBy", result.Sources["Sample.AuditRules_Fragments.g.cs"]);
    }

    [Fact]
    public void TwoCallers_ShareOneInstantiation()
    {
        var result = Clean(
            "        AuditRules.Standard(rules, x);",
            """
            public sealed class OrderAuditRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    AuditRules.Standard(rules, x);
                }
            }

            """
        );

        var container = result.Sources["Sample.AuditRules_Fragments.g.cs"];

        Assert.Equal(
            1,
            container
                .Split("public static global::ValidationModules.ValidationFlow Standard_Order")
                .Length - 1
        );
    }

    [Fact]
    public void ExtraParameters_BindAtTheCallSite()
    {
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

            """
        );

        var region = result.Sources["Sample.OrderRules_Rules.g.cs"];
        var container = result.Sources["Sample.CustomsRules_Fragments.g.cs"];

        Assert.Contains(
            "global::Sample.CustomsRules_Fragments.Declare(ref ctx, x, x.Tier > 2)",
            region
        );
        Assert.Contains("if (strict)\n        {", container);
    }

    [Fact]
    public void AFragmentMayCallAFragment()
    {
        var result = Clean(
            "        Outer.Declare(rules, x);",
            """
            public static class Outer {
                public static void Declare(ValidationRules<Order> rules, Order order) {
                    rules.Require(order.Number);
                    AuditRules.Standard(rules, order);
                }
            }

            """
        );

        Assert.Contains(
            "global::Sample.AuditRules_Fragments.Standard_Order(ref ctx, order)",
            result.Sources["Sample.Outer_Fragments.g.cs"]
        );
    }

    /// <summary>
    /// The chain the generic fragments below share. Inside <c>Standard</c>, the calls bind over its
    /// own <c>T</c>, and expanding them for <c>Order</c> has to put <c>Order</c> in for it, as the
    /// outer instantiation did. <c>IsSystem</c> is an ordinary static method rather than a fragment:
    /// a generic fragment's builder is <c>ValidationRules&lt;T&gt;</c>, which no non-generic
    /// fragment can take.
    /// </summary>
    private const string Chain = """
        public static class AuditChecks {
            public static bool IsSystem(IAudited audited) => audited.CreatedBy == "system";
        }

        public static class ChainRules {
            public static void Standard<T>(ValidationRules<T> rules, T audited) where T : IAudited {
                if (AuditChecks.IsSystem(audited)) {
                    return;
                }

                rules.Require(audited.CreatedBy);
                Extra(rules, audited);
            }

            public static void Extra<T>(ValidationRules<T> rules, T audited) where T : IAudited {
                rules.RangeAtLeast(audited.Version, 1);
                Last<T>(rules, audited);
            }

            public static void Last<T>(ValidationRules<T> rules, T audited) where T : IAudited {
                rules.Ensure(audited.Version < 100);
            }
        }

        """;

    [Fact]
    public void AGenericFragmentCallingAGenericFragment_ExpandsEachForTheConcreteTarget()
    {
        var result = Clean("        ChainRules.Standard(rules, x);", Chain);

        var container = result.Sources["Sample.ChainRules_Fragments.g.cs"];

        Assert.Contains(
            "global::Sample.ChainRules_Fragments.Extra_Order(ref ctx, audited)",
            container
        );
        Assert.Contains(
            "global::Sample.ChainRules_Fragments.Last_Order(ref ctx, audited)",
            container
        );
        Assert.DoesNotContain(" T audited", container);
    }

    [Fact]
    public void AGenericFragmentCallingAGenericFragment_PathsTheInnerRulesAgainstTheSubject()
    {
        var result = Clean("        ChainRules.Standard(rules, x);", Chain);

        var container = result.Sources["Sample.ChainRules_Fragments.g.cs"];

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3007");
        Assert.Contains("\"version\"", container);
    }

    [Fact]
    public void ANonGenericFragment_MayStartAGenericChain()
    {
        var result = Clean(
            "        Outer.Declare(rules, x);",
            Chain
                + """
                public static class Outer {
                    public static void Declare(ValidationRules<Order> rules, Order order) {
                        rules.Require(order.Number);
                        ChainRules.Standard(rules, order);
                    }
                }

                """
        );

        Assert.Contains("Extra_Order", result.Sources["Sample.ChainRules_Fragments.g.cs"]);
    }

    /// <summary>
    /// An instantiation is its type arguments, not only its target. <c>Tagged</c> is closed over
    /// <c>Order</c> twice, once with <c>string</c> and once with <c>int</c>, and the two need two
    /// methods: one method keyed by the target alone was handed the other's argument.
    /// </summary>
    [Fact]
    public void AFragmentWithASecondTypeParameter_IsExpandedPerInstantiation()
    {
        var result = Clean(
            """
                    TaggedRules.Tagged(rules, x, "direct");
                    TaggedRules.Outer(rules, x);
            """,
            """
            public static class TaggedRules {
                public static void Outer<T>(ValidationRules<T> rules, T audited) where T : IAudited {
                    Tagged(rules, audited, 2);
                }

                public static void Tagged<T, TTag>(ValidationRules<T> rules, T audited, TTag tag)
                    where T : IAudited {
                    rules.Require(audited.CreatedBy);
                }
            }

            """
        );

        var container = result.Sources["Sample.TaggedRules_Fragments.g.cs"];

        Assert.Contains("string tag", container);
        Assert.Contains("int tag", container);
    }

    /// <summary>
    /// An expansion is a method with no type parameters, so every mention of the fragment's
    /// <c>T</c> is written as the type it was expanded for. <c>nameof(T)</c> is <c>"T"</c> in C#
    /// whatever <c>T</c> stands for, and stays that. The code and message an <c>Ensure</c> derives
    /// are the fragment's source text, <c>T</c> included, so string literals are left out of the
    /// search for a <c>T</c> left behind.
    /// </summary>
    [Theory]
    [InlineData(
        "rules.Ensure(audited.CreatedBy != typeof(T).Name, message: \"m\");",
        "audited.CreatedBy != typeof(global::Sample.Order).Name"
    )]
    [InlineData(
        "rules.Ensure(!Equals(audited, default(T)), field: \"f\");",
        "default(global::Sample.Order)"
    )]
    [InlineData(
        "object boxed = audited; rules.Ensure(((T)boxed).Version > 0, field: \"f\");",
        "((global::Sample.Order)boxed).Version > 0"
    )]
    [InlineData(
        "object boxed = audited; rules.Ensure(boxed is T, field: \"f\");",
        "boxed is global::Sample.Order"
    )]
    [InlineData(
        "object boxed = audited; rules.Ensure(boxed is T typed && typed.Version > 0, field: \"f\");",
        "boxed is global::Sample.Order typed && typed.Version > 0"
    )]
    [InlineData("rules.Ensure(audited.CreatedBy != nameof(T));", "audited.CreatedBy != \"T\"")]
    [InlineData(
        "rules.Ensure(Array.Empty<T>().Length == 0, field: \"f\");",
        "Array.Empty<global::Sample.Order>().Length == 0"
    )]
    [InlineData(
        "T? missing = default; rules.Ensure(missing is null, field: \"f\");",
        "global::Sample.Order? missing = default;"
    )]
    [InlineData(
        "var pair = new T[] { audited }; rules.Ensure(pair.Length == 1, field: \"f\");",
        "var pair = new global::Sample.Order[]"
    )]
    [InlineData(
        "foreach (T item in new[] { audited }) { rules.Context.Report(\"f\", \"c\", item.CreatedBy ?? \"\"); }",
        "foreach (global::Sample.Order item in new[]"
    )]
    public void AGenericFragmentsTypeParameter_IsWrittenAsTheTypeItWasExpandedFor(
        string statement,
        string expected
    )
    {
        var result = Clean(
            "        TypedRules.Standard(rules, x);",
            $$"""
            public static class TypedRules {
                public static void Standard<T>(ValidationRules<T> rules, T audited) where T : IAudited {
                    {{statement}}
                }
            }

            """
        );

        var container = result.Sources["Sample.TypedRules_Fragments.g.cs"];

        Assert.Contains(expected, container);
        Assert.DoesNotMatch(@"\bT\b", Regex.Replace(container, @"""(?:[^""\\]|\\.)*""", "\"\""));
    }

    /// <summary>
    /// A type parameter that a value type stands for: <c>T?</c> over an unconstrained <c>T</c> is
    /// <c>T</c> itself there, not <c>Nullable&lt;T&gt;</c>.
    /// </summary>
    [Fact]
    public void AValueTypeArgument_KeepsTheMeaningOfTQuestionMark()
    {
        var result = Clean(
            "        Tagged.Declare(rules, x, 7);",
            """
            public static class Tagged {
                public static void Declare<T, TTag>(ValidationRules<T> rules, T audited, TTag tag)
                    where T : IAudited {
                    TTag? copy = tag;
                    rules.Ensure(copy!.Equals(tag), field: "tag");
                }
            }

            """
        );

        Assert.Contains("int copy = tag;", result.Sources["Sample.Tagged_Fragments.g.cs"]);
    }

    /// <summary>
    /// A type test takes the plain form of the type a type parameter stands for: no nullable
    /// annotation, and a tuple written as its <c>ValueTuple</c>, which after <c>is</c> would read
    /// as a positional pattern. A declaration keeps the type as it was inferred.
    /// </summary>
    [Theory]
    [InlineData("x.Number", "string? copy = tag;", "boxed is string")]
    [InlineData(
        "(Tier: x.Tier, Number: x.Number)",
        "(int Tier, string? Number) copy = tag;",
        "boxed is global::System.ValueTuple<int, string?>"
    )]
    public void ATypeTest_TakesThePlainFormOfTheTypeArgument(
        string argument,
        string declared,
        string tested
    )
    {
        var result = Clean(
            $"        Tagged.Declare(rules, x, {argument});",
            """
            public static class Tagged {
                public static void Declare<T, TTag>(ValidationRules<T> rules, T audited, TTag tag)
                    where T : IAudited {
                    TTag copy = tag;
                    object? boxed = copy;
                    rules.Ensure(boxed is TTag, field: "tag");
                }
            }

            """
        );

        var container = result.Sources["Sample.Tagged_Fragments.g.cs"];

        Assert.Contains(declared, container);
        Assert.Contains(tested, container);
    }

    /// <summary>
    /// One fragment expanded for two types declares one rule, so both expansions report the one
    /// code derived from the fragment's source, <c>T</c> included. A code taken from the expanded
    /// text would differ per type.
    /// </summary>
    [Fact]
    public void AFragmentExpandedForTwoTypes_ReportsOneDerivedCode()
    {
        var result = Clean(
            "        NamedRules.Standard(rules, x);",
            """
            public sealed record Invoice : IAudited {
                public string? CreatedBy { get; init; }
                public int Version { get; init; }
            }

            public sealed class InvoiceRules : IValidationRulesFor<Invoice> {
                public static void Describe(ValidationRules<Invoice> rules, Invoice x) {
                    NamedRules.Standard(rules, x);
                }
            }

            public static class NamedRules {
                public static void Standard<T>(ValidationRules<T> rules, T audited) where T : IAudited {
                    rules.Ensure(audited.CreatedBy != typeof(T).Name, message: "The author repeats the type.");
                }
            }

            """
        );

        var container = result.Sources["Sample.NamedRules_Fragments.g.cs"];

        Assert.Contains("typeof(global::Sample.Order).Name", container);
        Assert.Contains("typeof(global::Sample.Invoice).Name", container);
        Assert.Equal(2, Regex.Matches(container, "\"created_by_not_equal_typeof_t_name\"").Count);
    }

    /// <summary>
    /// A type argument the expansion cannot name is reported at the call, rather than written
    /// into a generated file that does not compile.
    /// </summary>
    [Fact]
    public void AnAnonymousTypeArgument_IsVM3009AtTheCall()
    {
        var result = Run(
            "        Tagged.Declare(rules, x, new { Strict = true });",
            """
            public static class Tagged {
                public static void Declare<T, TTag>(ValidationRules<T> rules, T audited, TTag tag)
                    where T : IAudited {
                    rules.Ensure(typeof(TTag) != typeof(T), field: "tag");
                }
            }

            """
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3009");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(
            "'Tagged.Declare' is called with TTag = '<anonymous type: bool Strict>', which its "
                + "generated expansion cannot name. An anonymous type has no name. Pass a value "
                + "of a named type, such as a record, instead",
            diagnostic.GetMessage()
        );
        Assert.Equal(
            "Tagged.Declare(rules, x, new { Strict = true })",
            diagnostic
                .Location.SourceTree!.GetText(TestContext.Current.CancellationToken)
                .ToString(diagnostic.Location.SourceSpan)
        );
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void APrivateTypeArgument_IsVM3009AtTheCall()
    {
        var result = GeneratorHarness.Run(
            Audited
                + """

                public static class Tagged {
                    public static void Declare<T, TTag>(ValidationRules<T> rules, T audited, TTag tag)
                        where T : IAudited {
                        rules.Ensure(tag is not null, field: "tag");
                    }
                }

                public sealed class OrderRules : IValidationRulesFor<Order> {
                    private sealed class Secret { }

                    public static void Describe(ValidationRules<Order> rules, Order x) {
                        Tagged.Declare(rules, x, new Secret());
                    }
                }
                """
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3009");

        Assert.EndsWith(
            "'Sample.OrderRules.Secret' is not accessible outside the type that declares it. "
                + "Make it internal",
            diagnostic.GetMessage()
        );
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void AGenericFragmentCycle_IsVM3006()
    {
        var result = Run(
            "        Loop.Left(rules, x);",
            """
            public static class Loop {
                public static void Left<T>(ValidationRules<T> rules, T audited) where T : IAudited =>
                    Right(rules, audited);

                public static void Right<T>(ValidationRules<T> rules, T audited) where T : IAudited =>
                    Left(rules, audited);
            }

            """
        );

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3006");
    }

    [Fact]
    public void AFragmentCycle_IsVM3006()
    {
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

            """
        );

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3006");
    }

    [Fact]
    public void ACrossAssemblyFragment_IsVM3005WithTheSourcePackageFix()
    {
        var shared = GeneratorHarness.CompileToReference(
            """
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
            """,
            "Shared"
        );

        var result = GeneratorHarness.Run(
            """
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
            new[] { shared }
        );

        var reported = Assert.Single(result.Diagnostics, d => d.Id == "VM3005");

        Assert.Contains("SharedRules.Standard", reported.GetMessage());
        Assert.Contains("source", reported.GetMessage());
    }

    [Fact]
    public void AnExplicitInterfaceImplementation_IsVM3004RatherThanAnErrorInGeneratedCode()
    {
        var result = GeneratorHarness.Run(
            """
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
            """
        );

        Assert.Contains(
            result.Diagnostics,
            d => d.Id == "VM3004" && d.GetMessage().Contains("explicitly")
        );
    }

    [Fact]
    public void TheSubjectArgument_MustBeTheDescribeSubject()
    {
        // A facet of a child is Nested's territory, where the path pushes.
        var result = Run(
            """
                    var other = new Order();
                    AuditRules.Standard(rules, other);
            """
        );

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3002");
    }

    [Fact]
    public void ADescentInsideAFragment_IsRejected()
    {
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

            """
        );

        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AnEarlyReturnInAFragment_EndsTheFragmentOnly()
    {
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

            """
        );

        var container = result.Sources["Sample.Gate_Fragments.g.cs"];
        var region = result.Sources["Sample.OrderRules_Rules.g.cs"];

        // The fragment's return is Continue - the caller's next statement still runs.
        Assert.Contains("return global::ValidationModules.ValidationFlow.Continue;", container);
        Assert.Contains("ReportRequired(ctx, \"number\")", region);
    }
}
