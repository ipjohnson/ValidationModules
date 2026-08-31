using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>CustomConstraintAttribute</c>: the author's own attribute, compiled like a built-in. The
/// claims under test are the feature: a direct static call with the constructor's constants, the
/// base's knobs working unchanged, and every wrong shape caught at build time as VM1601.
/// </summary>
public class CustomConstraintTests {

    private const string SkuAttribute = """
        using System;
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed class SkuAttribute : CustomConstraintAttribute {
            public static bool IsValid(string value) => value.StartsWith("SKU-", StringComparison.Ordinal);
        }
        """;

    [Fact]
    public void CustomConstraint_CompilesToADirectStaticCall() {
        var result = GeneratorHarness.Run(SkuAttribute + """

            public record Product {
                [Sku]
                public string? Code { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];

        // Null passes, like every structural constraint; the check is the author's static, called
        // directly with nothing constructed and nothing boxed.
        Assert.Contains(
            "value.Code is not null && !global::Sample.SkuAttribute.IsValid(value.Code)", emitted);
        Assert.Contains("ReportCustom", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void CustomConstraint_ConstructorArgumentsFlowIntoTheCall() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class DivisibleAttribute : CustomConstraintAttribute {
                public DivisibleAttribute(int divisor) { }

                public static bool IsValid(int value, int divisor) => value % divisor == 0;
            }

            public record Product {
                [Divisible(3)]
                public int Count { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            "!global::Sample.DivisibleAttribute.IsValid(value.Count, 3)",
            result.Sources["Sample.ProductValidator.g.cs"]);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void CustomConstraint_TheBaseKnobsWorkUnchanged() {
        var result = GeneratorHarness.Run(SkuAttribute + """

            public record Product {
                public bool IsCatalogued { get; init; }

                [Sku(Code = "sku_shape", Message = "sku must start with SKU-", When = nameof(IsCatalogued))]
                public string? Code { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];

        // A Message takes the literal path with the custom code; the condition hoists into a
        // local exactly as it does for a built-in constraint.
        Assert.Contains("\"sku_shape\", \"sku must start with SKU-\"", emitted);
        Assert.Contains("var c0 = value.IsCatalogued;", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void CustomConstraint_NullableValueTypeMember_IsGuardedAndUnwrapped() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class EvenAttribute : CustomConstraintAttribute {
                public static bool IsValid(int value) => value % 2 == 0;
            }

            public record Product {
                [Even]
                public int? Count { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            "value.Count is not null && !global::Sample.EvenAttribute.IsValid(value.Count.Value)",
            result.Sources["Sample.ProductValidator.g.cs"]);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void CustomConstraint_ParticipatesInTheBooleanFastPath() {
        var result = GeneratorHarness.Run(SkuAttribute + """

            public record Product {
                [Sku]
                public string? Code { get; init; }
            }
            """);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];
        var fastPath = emitted.Substring(emitted.IndexOf("public bool IsValid"));

        Assert.Contains("!global::Sample.SkuAttribute.IsValid(value.Code)", fastPath);
    }

    // VM1601 — every wrong shape is a build error naming the fix.

    [Theory]
    [InlineData("", "declares no public static bool IsValid")]
    [InlineData("public bool IsValid(string value) => true;", "declares no public static bool IsValid")]
    [InlineData("public static string IsValid(string value) => \"\";", "declares no public static bool IsValid")]
    [InlineData("public static bool IsValid(int value) => true;", "cannot accept this member")]
    [InlineData("public static bool IsValid(string value, int extra) => true;",
        "the constructor supplies 0")]
    public void CustomConstraint_WrongShape_IsVM1601(string method, string reason) {
        var result = GeneratorHarness.Run($$"""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class BrokenAttribute : CustomConstraintAttribute {
                {{method}}
            }

            public record Product {
                [Broken]
                public string? Code { get; init; }
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1601");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(reason, diagnostic.GetMessage());
    }

    [Fact]
    public void CustomConstraint_ConstructorParameterTypeMismatch_IsVM1601() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class BrokenAttribute : CustomConstraintAttribute {
                public BrokenAttribute(string divisor) { }

                public static bool IsValid(int value, int divisor) => true;
            }

            public record Product {
                [Broken("3")]
                public int Count { get; init; }
            }
            """);

        Assert.Contains(
            "constructor's matching parameter is 'string'",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1601").GetMessage());
    }

    [Fact]
    public void CustomConstraint_ACustomPropertySetter_IsVM1601() {
        // A static check has no instance to read the property from, so setting one would be an
        // argument that silently never arrives - the failure shape this library refuses.
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class SizedAttribute : CustomConstraintAttribute {
                public int Limit { get; init; }

                public static bool IsValid(string value) => true;
            }

            public record Product {
                [Sized(Limit = 5)]
                public string? Code { get; init; }
            }
            """);

        Assert.Contains(
            "pass it through the constructor",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1601").GetMessage());
    }

    [Fact]
    public void CustomConstraint_OnARecordParameter_IsVM1008LikeAnyConstraint() {
        // The attribute lands on the parameter and is never read - the same silent failure VM1008
        // exists to catch for the built-ins, so a custom constraint gets the same net.
        var result = GeneratorHarness.Run(SkuAttribute + """

            public record Product([Sku] string? Code);
            """);

        Assert.Single(result.Diagnostics, d => d.Id == "VM1008");
    }

    // -- author defaults ------------------------------------------------------------------------

    private const string SkuWithDefaults = """
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed class SkuAttribute : CustomConstraintAttribute {
            public const string DefaultMessage = "sku must look like SKU-XXXXXXXX";
            public const string DefaultCode = "sku_format";

            public static bool IsValid(string value) =>
                value.StartsWith("SKU-", System.StringComparison.Ordinal);
        }
        """;

    /// <summary>
    /// The author bakes the default; a bare application gets it. A const rather than a
    /// constructor assignment, because the generator never constructs the attribute for a static
    /// check - code is invisible here, a constant is not.
    /// </summary>
    [Fact]
    public void AuthorDefaults_ApplyWhenTheApplicationSetsNothing() {
        var result = GeneratorHarness.Run(SkuWithDefaults + """

            public record Product {
                [Sku] public string? Sku { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];

        Assert.Contains("\"sku must look like SKU-XXXXXXXX\"", emitted);
        Assert.Contains("\"sku_format\"", emitted);
    }

    /// <summary>The use site still wins, same as overriding a built-in's composed text.</summary>
    [Fact]
    public void AuthorDefaults_LoseToTheUseSite() {
        var result = GeneratorHarness.Run(SkuWithDefaults + """

            public record Product {
                [Sku(Message = "warehouse skus start with SKU-", Code = "warehouse_sku")]
                public string? Sku { get; init; }
            }
            """);

        var emitted = result.Sources["Sample.ProductValidator.g.cs"];

        Assert.Contains("\"warehouse skus start with SKU-\"", emitted);
        Assert.Contains("\"warehouse_sku\"", emitted);
        Assert.DoesNotContain("SKU-XXXXXXXX", emitted);
    }

    /// <summary>A default declared on a shared base attribute serves every derived check.</summary>
    [Fact]
    public void AuthorDefaults_AreInheritedFromABaseAttribute() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public abstract class FormatAttribute : CustomConstraintAttribute {
                public const string DefaultMessage = "value is not in the required format";
            }

            public sealed class SkuAttribute : FormatAttribute {
                public static bool IsValid(string value) =>
                    value.StartsWith("SKU-", System.StringComparison.Ordinal);
            }

            public record Product {
                [Sku] public string? Sku { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "\"value is not in the required format\"",
            result.Sources["Sample.ProductValidator.g.cs"]);
    }
}
