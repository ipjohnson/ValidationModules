using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>IConstraintFor&lt;T&gt;</c>: the instance shape of a custom constraint. The claims under test
/// are the feature: one instance hoisted into a static field and called directly, the interface
/// only in the way when the class left a member to it, <c>[PerValidationInstance]</c> trading the
/// field for a per-check construction that VM1603 prices, and every wrong shape caught at build
/// time as VM1602.
/// </summary>
public class ConstraintInterfaceTests {

    private const string SkuCheckAttribute = """
        using System;
        using ValidationModules;

        namespace Sample;

        public sealed class SkuCheckAttribute : Attribute, IConstraintFor<string> {
            public bool IsValid(string value) => value.StartsWith("SKU-", StringComparison.Ordinal);

            public ValidationFlow Validate(ref ValidationContext context, string value, string field) =>
                IsValid(value) ? ValidationFlow.Continue : context.Report(field, "sku", "not a sku");
        }
        """;

    [Fact]
    public void InterfaceConstraint_HoistsOneInstanceAndCallsItDirectly() {
        var result = GeneratorHarness.Run(SkuCheckAttribute + """

            public record Product {
                [SkuCheck]
                public string? Code { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];

        // The field is typed as the attribute class, so both calls bind on it directly - no
        // interface dispatch anywhere the author implemented implicitly.
        Assert.Contains(
            "private static readonly global::Sample.SkuCheckAttribute CodeConstraint0 = new global::Sample.SkuCheckAttribute();",
            emitted);

        // Null skips, like every structural constraint - the interface's contract says null
        // never arrives.
        Assert.Contains(
            "value.Code is not null && CodeConstraint0.Validate(ref ctx, value.Code, \"code\").ShouldStop",
            emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void InterfaceConstraint_DefaultValidate_GoesThroughTheInterface() {
        // Only IsValid is implemented; Validate is the interface's default implementation, which
        // no method on the class can bind - so that one call casts, and IsValid stays direct.
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;

            namespace Sample;

            public sealed class BareSkuAttribute : Attribute, IConstraintFor<string> {
                public bool IsValid(string value) => value.StartsWith("SKU-", StringComparison.Ordinal);
            }

            public record Product {
                [BareSku]
                public string? Code { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];

        Assert.Contains(
            "((global::ValidationModules.IConstraintFor<string>)CodeConstraint0).Validate(ref ctx, value.Code, \"code\")",
            emitted);
        Assert.Contains("!CodeConstraint0.IsValid(value.Code)", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void InterfaceConstraint_ConstructorArgumentsRideIntoTheConstruction() {
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;

            namespace Sample;

            public sealed class OneOfAttribute : Attribute, IConstraintFor<string> {
                private readonly string[] _allowed;

                public OneOfAttribute(params string[] allowed) { _allowed = allowed; }

                public bool IsValid(string value) => Array.IndexOf(_allowed, value) >= 0;

                public ValidationFlow Validate(ref ValidationContext context, string value, string field) =>
                    IsValid(value) ? ValidationFlow.Continue : context.ReportCustom(field);
            }

            public record Product {
                [OneOf("email", "sms")]
                public string? Channel { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);

        // Built once from the declaration's constants - the state the constructor computes from
        // them is the reason to choose this shape over a static check.
        Assert.Contains(
            "= new global::Sample.OneOfAttribute(new string[] { \"email\", \"sms\" });",
            result.Sources["Sample.ProductValidator.g.cs"]);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void InterfaceConstraint_NullableValueTypeMember_IsGuardedAndUnwrapped() {
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;

            namespace Sample;

            public sealed class EvenAttribute : Attribute, IConstraintFor<int> {
                public bool IsValid(int value) => value % 2 == 0;

                public ValidationFlow Validate(ref ValidationContext context, int value, string field) =>
                    IsValid(value) ? ValidationFlow.Continue : context.ReportCustom(field);
            }

            public record Product {
                [Even]
                public int? Count { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);

        // An int? member matches IConstraintFor<int>: the guard skips null and the call unwraps,
        // so the author's check reads the value type it declared, unboxed.
        Assert.Contains(
            "value.Count is not null && CountConstraint0.Validate(ref ctx, value.Count.Value, \"count\").ShouldStop",
            result.Sources["Sample.ProductValidator.g.cs"]);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void InterfaceConstraint_ParticipatesInTheBooleanFastPath() {
        var result = GeneratorHarness.Run(SkuCheckAttribute + """

            public record Product {
                [SkuCheck]
                public string? Code { get; init; }
            }
            """);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];
        var fastPath = emitted.Substring(emitted.IndexOf("public bool IsValid"));

        // IsValid exists on the interface precisely so this path survives: the verdict without a
        // collector, through the same hoisted instance.
        Assert.Contains("value.Code is not null && !CodeConstraint0.IsValid(value.Code)", fastPath);
    }

    [Fact]
    public void InterfaceConstraint_PerValidationInstance_ConstructsAtTheCheckAndSaysSo() {
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            [PerValidationInstance]
            public sealed class StampAttribute : Attribute, IConstraintFor<string> {
                public bool IsValid(string value) => value.Length > 0;

                public ValidationFlow Validate(ref ValidationContext context, string value, string field) =>
                    IsValid(value) ? ValidationFlow.Continue : context.ReportCustom(field);
            }

            public record Product {
                [Stamp]
                public string? Code { get; init; }
            }
            """);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];

        // No shared field; the construction happens where the check does, on both paths - and the
        // cost is stated as an Info at the site that pays it.
        Assert.DoesNotContain("private static readonly global::Sample.StampAttribute", emitted);
        Assert.Contains(
            "new global::Sample.StampAttribute().Validate(ref ctx, value.Code, \"code\")", emitted);
        Assert.Contains("!new global::Sample.StampAttribute().IsValid(value.Code)", emitted);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1603");

        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void InterfaceConstraint_TheConstraintBaseKnobsWork() {
        // Deriving from ValidationConstraintAttribute is optional; when the author does, When and
        // Unless weave as generator-enforced conditions, and Code and Message ride into the
        // instance for the default Validate to honour.
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class TierAttribute : ValidationConstraintAttribute, IConstraintFor<string> {
                public bool IsValid(string value) => value is "basic" or "pro";
            }

            public record Product {
                public bool IsCatalogued { get; init; }

                [Tier(Code = "tier", Message = "{field} is not a tier", When = nameof(IsCatalogued))]
                public string? Tier { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];

        // The condition hoists into a local exactly as it does for a built-in; the knobs land in
        // the construction, where the instance - not the emitter - reads them.
        Assert.Contains("var c0 = value.IsCatalogued;", emitted);
        Assert.Contains("c0 && value.Tier is not null &&", emitted);
        Assert.Contains("Code = \"tier\", Message = \"{field} is not a tier\"", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void InterfaceConstraint_WinsOverAValidationAttributeBase() {
        // One class, both worlds: under MVC and TryValidateObject it is a ValidationAttribute;
        // here the interface takes precedence and nothing goes through the bridge - no context,
        // no box, no VM2002.
        var result = GeneratorHarness.Run("""
            using System;
            using System.ComponentModel.DataAnnotations;
            using ValidationModules;

            namespace Sample;

            public sealed class SkuAttribute : ValidationAttribute, IConstraintFor<string> {
                public bool IsValid(string value) => value.StartsWith("SKU-", StringComparison.Ordinal);

                public override bool IsValid(object? value) => value is not string text || IsValid(text);

                // Qualified because this file imports both namespaces, and DataAnnotations has a
                // ValidationContext of its own - the CS0104 the Constraints namespace exists to
                // avoid, hit here from the other side.
                public ValidationFlow Validate(
                    ref ValidationModules.ValidationContext context, string value, string field) =>
                    IsValid(value) ? ValidationFlow.Continue : context.ReportCustom(field);
            }

            public record Product {
                [Sku]
                public string? Code { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];

        Assert.Contains("CodeConstraint0.Validate(ref ctx, value.Code, \"code\")", emitted);
        Assert.DoesNotContain("DataAnnotationsSupport", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void InterfaceConstraint_ExactInstantiationBeatsAssignableOnes() {
        // Implements both the member's own type and object; the member's own type runs. The cast
        // in the emitted call is where the choice is visible.
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;

            namespace Sample;

            public sealed class WideAttribute : Attribute, IConstraintFor<string>, IConstraintFor<object> {
                public bool IsValid(string value) => value.Length > 0;

                bool IConstraintFor<object>.IsValid(object value) => true;
            }

            public record Product {
                [Wide]
                public string? Code { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            "((global::ValidationModules.IConstraintFor<string>)CodeConstraint0).Validate",
            result.Sources["Sample.ProductValidator.g.cs"]);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void InterfaceConstraint_AUniqueAssignableInstantiation_Matches() {
        // No exact instantiation, one the member converts to: an IComparable check runs against a
        // string member, the way AcceptsMember already reads a static check's first parameter.
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;

            namespace Sample;

            public sealed class OrderedAttribute : Attribute, IConstraintFor<IComparable> {
                public bool IsValid(IComparable value) => true;

                public ValidationFlow Validate(ref ValidationContext context, IComparable value, string field) =>
                    ValidationFlow.Continue;
            }

            public record Product {
                [Ordered]
                public string? Code { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void InterfaceConstraint_OnABaseProperty_ReachesTheDerivedValidator() {
        var result = GeneratorHarness.Run(SkuCheckAttribute + """

            public abstract record Item {
                [SkuCheck]
                public string? Code { get; init; }
            }

            public record Product : Item;
            """);

        Assert.Contains(
            "CodeConstraint0.Validate(ref ctx, value.Code, \"code\")",
            result.Sources["Sample.ProductValidator.g.cs"]);
    }

    [Fact]
    public void InterfaceConstraint_OnARecordParameter_IsVM1008LikeAnyConstraint() {
        var result = GeneratorHarness.Run(SkuCheckAttribute + """

            public record Product([SkuCheck] string? Code);
            """);

        Assert.Single(result.Diagnostics, d => d.Id == "VM1008");
    }

    // VM1602 — every wrong shape is a build error naming the fix.

    [Fact]
    public void InterfaceConstraint_NoInstantiationFitsTheMember_IsVM1602() {
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;

            namespace Sample;

            public sealed class EvenAttribute : Attribute, IConstraintFor<int> {
                public bool IsValid(int value) => value % 2 == 0;
            }

            public record Product {
                [Even]
                public string? Code { get; init; }
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1602");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("none of those accepts", diagnostic.GetMessage());
        Assert.Contains("IConstraintFor<int>", diagnostic.GetMessage());
    }

    [Fact]
    public void InterfaceConstraint_AmbiguousInstantiations_IsVM1602() {
        // string is both an object and an IComparable, and implements neither instantiation
        // exactly - refusing beats picking one and silently running the other author's intent.
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;

            namespace Sample;

            public sealed class LooseAttribute : Attribute, IConstraintFor<object>, IConstraintFor<IComparable> {
                bool IConstraintFor<object>.IsValid(object value) => true;

                bool IConstraintFor<IComparable>.IsValid(IComparable value) => true;
            }

            public record Product {
                [Loose]
                public string? Code { get; init; }
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1602");

        Assert.Contains("more than one implemented instantiation", diagnostic.GetMessage());
        Assert.Contains("implement IConstraintFor<string>", diagnostic.GetMessage());
    }

    [Fact]
    public void InterfaceConstraint_MixedWithTheStaticShape_IsVM1602() {
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class TornAttribute : CustomConstraintAttribute, IConstraintFor<string> {
                public bool IsValid(string value) => true;
            }

            public record Product {
                [Torn]
                public string? Code { get; init; }
            }
            """);

        Assert.Contains(
            "pick one",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1602").GetMessage());
    }

    [Fact]
    public void InterfaceConstraint_AGenericAttributeClass_IsVM1602() {
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules;

            namespace Sample;

            public sealed class TypedAttribute<T> : Attribute, IConstraintFor<string> {
                public bool IsValid(string value) => true;
            }

            public record Product {
                [Typed<int>]
                public string? Code { get; init; }
            }
            """);

        Assert.Contains(
            "generic attribute class",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1602").GetMessage());
    }
}
