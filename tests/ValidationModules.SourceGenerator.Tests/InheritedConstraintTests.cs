using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Constraints declared on a base type, or on an interface, apply to the type that inherits them.
/// </summary>
/// <remarks>
/// <para>
/// The front end used to walk <c>type.GetMembers()</c>, which returns <b>declared</b> members only.
/// A derived type therefore validated none of its base's constrained properties: the build was
/// clean, the base's own validator was correct, and only the derived type - the one actually being
/// validated - silently answered "valid" for input the base said was invalid. Every shared-base DTO
/// has this shape: a <c>BaseRequest</c> carrying correlation and tenant ids, an audited-entity base,
/// a paged-request base.
/// </para>
/// <para>
/// These assert on emitted text rather than on runtime behaviour because the failure was an absence
/// - the check was simply not written - and a golden file is what pins the absence closed.
/// </para>
/// </remarks>
public class InheritedConstraintTests {

    private static string Body(GeneratorHarness.Result result, string validator) {
        Assert.Empty(result.CompilationErrors);

        return result.Sources.Single(source => source.Key.Contains(validator)).Value;
    }

    // -- base classes ------------------------------------------------------------------------

    private const string BaseChain = """
        using ValidationModules.Constraints;

        namespace Sample;

        public record BaseRequest {
            [Required]
            public string? CorrelationId { get; init; }

            [Required]
            public string? TenantId { get; init; }
        }

        public record CreateOrder : BaseRequest {
            [Required]
            public string? Sku { get; init; }
        }
        """;

    [Fact]
    public void BaseClassConstraints_AreCheckedByTheDerivedValidator() {
        var body = Body(GeneratorHarness.Run(BaseChain), "CreateOrderValidator");

        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"correlationId\", value: value.CorrelationId)", body);
        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"tenantId\", value: value.TenantId)", body);
        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"sku\", value: value.Sku)", body);
    }

    /// <summary>
    /// Root-most base first, down the chain, then the type's own members. §4.2 guarantees
    /// declaration order, and this is the reading of it that matches what someone sees looking at
    /// the two declarations one above the other.
    /// </summary>
    [Fact]
    public void InheritedConstraints_AreCheckedBeforeTheTypesOwn() {
        var body = Body(GeneratorHarness.Run(BaseChain), "CreateOrderValidator");

        Assert.True(
            body.IndexOf("correlationId", StringComparison.Ordinal)
            < body.IndexOf("sku", StringComparison.Ordinal),
            "the base's fields should be checked before the derived type's");
    }

    /// <summary>
    /// The case that would otherwise emit nothing at all: without counting inherited constraints
    /// toward "saw something", a type adding no constraints of its own produces no validator.
    /// </summary>
    [Fact]
    public void DerivedTypeAddingNothingOfItsOwn_StillGetsAValidator() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record BaseRequest {
                [Required]
                public string? CorrelationId { get; init; }
            }

            public record Ping : BaseRequest;
            """);

        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"correlationId\", value: value.CorrelationId)",
            Body(result, "PingValidator"));
    }

    [Fact]
    public void MultiLevelInheritance_CollectsEveryLevel() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public class Root { [Required] public string? A { get; set; } }
            public class Middle : Root { [Required] public string? B { get; set; } }
            public class Leaf : Middle { [Required] public string? C { get; set; } }
            """);

        var body = Body(result, "LeafValidator");

        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"a\", value: value.A)", body);
        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"b\", value: value.B)", body);
        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"c\", value: value.C)", body);
    }

    // -- interfaces --------------------------------------------------------------------------

    /// <summary>
    /// An interface-declared constraint merges onto whatever implements it. This is a deliberate
    /// divergence from <c>Validator.TryValidateObject</c>, which ignores them entirely, and it is
    /// uniform across both vocabularies.
    /// </summary>
    [Fact]
    public void InterfaceConstraints_FlowToTheImplementingMember() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public interface IAudited {
                [Required]
                string? ModifiedBy { get; }
            }

            public record Document : IAudited {
                [Required]
                public string? Title { get; init; }

                public string? ModifiedBy { get; init; }
            }
            """);

        var body = Body(result, "DocumentValidator");

        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"title\", value: value.Title)", body);
        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"modifiedBy\", value: value.ModifiedBy)", body);
    }

    /// <summary>
    /// An interface adds to the implementer rather than replacing it - the interface is a contract
    /// the type opted into, so both are meant. This is the one place a merge happens; two class
    /// declarations of one property do not merge.
    /// </summary>
    [Fact]
    public void InterfaceConstraints_MergeWithTheImplementersOwn() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public interface IAudited {
                [Required]
                string? ModifiedBy { get; }
            }

            public record Document : IAudited {
                [StringLength(1, 64)]
                public string? ModifiedBy { get; init; }
            }
            """);

        var body = Body(result, "DocumentValidator");

        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"modifiedBy\", value: value.ModifiedBy)", body);
        Assert.Contains(
            "ctx.Report(\"modifiedBy\", global::ValidationModules.ValidationCodes.StringLength, value.ModifiedBy, _message", body);
    }

    // -- shadowing ---------------------------------------------------------------------------

    /// <summary>
    /// The most-derived declaration supplies all of a property's constraints, never some of them:
    /// two <c>[StringLength]</c> bounds on one field is ambiguous and would report twice. VM0030
    /// says so out loud, because the alternative is constraints disappearing on a `new` keyword.
    /// </summary>
    [Fact]
    public void ShadowedProperty_TakesOverEveryConstraintAndReportsVM0030() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public class Base {
                [Required]
                [StringLength(1, 10)]
                public virtual string? Name { get; set; }
            }

            public class Derived : Base {
                [StringLength(1, 200)]
                public new string? Name { get; set; }
            }
            """);

        var body = Body(result, "DerivedValidator");

        Assert.Contains(
            "ctx.Report(\"name\", global::ValidationModules.ValidationCodes.StringLength, value.Name, _message", body);
        Assert.DoesNotContain("1, 10", body);
        Assert.DoesNotContain(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"name\", value: value.Name)", body);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0030");
    }

    /// <summary>
    /// An override is one declaration, not two, so nothing is hidden and nothing is dropped.
    /// </summary>
    [Fact]
    public void OverriddenProperty_IsNotReportedAsHiding() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public class Base {
                [Required]
                public virtual string? Name { get; set; }
            }

            public class Derived : Base {
                public override string? Name { get; set; }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0030");
        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"name\", value: value.Name)",
            Body(result, "DerivedValidator"));
    }

    // -- across an assembly boundary ---------------------------------------------------------

    /// <summary>
    /// The case the feature exists for. A shared base lives in a package far more often than in the
    /// consuming project, and <see cref="CrossAssemblyMetadataSpike"/> pins the Roslyn behaviour
    /// this rests on.
    /// </summary>
    [Fact]
    public void BaseTypeFromAReferencedAssembly_ContributesItsConstraints() {
        var result = GeneratorHarness.RunWithReference(
            """
            using ValidationModules.Constraints;

            namespace Shared;

            public record BaseRequest {
                [Required]
                public string? CorrelationId { get; init; }

                [StringLength(1, 64)]
                public string? TenantId { get; init; }
            }
            """,
            """
            using Shared;
            using ValidationModules.Constraints;

            namespace Consumer;

            public record CreateOrder : BaseRequest {
                [Required]
                public string? Sku { get; init; }
            }
            """);

        var body = Body(result, "CreateOrderValidator");

        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"correlationId\", value: value.CorrelationId)", body);
        Assert.Contains(
            "ctx.Report(\"tenantId\", global::ValidationModules.ValidationCodes.StringLength, value.TenantId, _message", body);
        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"sku\", value: value.Sku)", body);
    }

    /// <summary>
    /// A base property that is <c>internal</c> to another assembly cannot be read from the
    /// generated validator, so it is skipped rather than emitted as a reference that will not bind.
    /// The error would otherwise land inside generated code.
    /// </summary>
    [Fact]
    public void InternalBasePropertyFromAnotherAssembly_IsSkippedRatherThanEmitted() {
        var result = GeneratorHarness.RunWithReference(
            """
            using ValidationModules.Constraints;

            namespace Shared;

            public record BaseRequest {
                [Required]
                public string? Visible { get; init; }

                [Required]
                internal string? Hidden { get; init; }
            }
            """,
            """
            using Shared;
            using ValidationModules.Constraints;

            namespace Consumer;

            public record CreateOrder : BaseRequest {
                [Required]
                public string? Sku { get; init; }
            }
            """);

        var body = Body(result, "CreateOrderValidator");

        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"visible\", value: value.Visible)", body);
        Assert.DoesNotContain("Hidden", body);
    }

    /// <summary>
    /// Diagnostics belong to the assembly that declares the constraint. Reporting a base's mistake
    /// again from every derived type would repeat it once per subclass and anchor each copy at a
    /// location the consumer cannot edit.
    /// </summary>
    [Fact]
    public void ConstraintDiagnosticsFromAReferencedBase_AreNotRepeatedOnTheDerivedType() {
        var result = GeneratorHarness.RunWithReference(
            """
            using ValidationModules.Constraints;

            namespace Shared;

            public record BaseRequest {
                // [StringLength] on an int: VM0001 where it is declared, not here.
                [StringLength(1, 10)]
                public int Count { get; init; }
            }
            """,
            """
            using Shared;
            using ValidationModules.Constraints;

            namespace Consumer;

            public record CreateOrder : BaseRequest {
                [Required]
                public string? Sku { get; init; }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0001");
    }
}
