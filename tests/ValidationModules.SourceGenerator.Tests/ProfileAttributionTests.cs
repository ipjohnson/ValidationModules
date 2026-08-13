using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// VM0019 — the guard on a declaration surface that shipped ahead of its implementation.
/// </summary>
/// <remarks>
/// <para>
/// <c>ValidationConstraintAttribute</c> carries <c>FromProfile</c>, <c>UntilProfile</c> and
/// <c>Profiles</c>, all three documented at length on the attribute itself, and
/// <c>IValidationProfile</c> exists in the runtime. Profiles are plan Stage 3 and are not built, so
/// none of it is read: one validator is emitted rather than one per profile, and every profiled
/// rule is enforced in every profile.
/// </para>
/// <para>
/// <b>An error rather than a warning, because of which way it fails.</b> A rule that never fires
/// costs a caller nothing; a rule written to apply only from V2 and enforced under V1 rejects data
/// the caller was entitled to send. A warning is a thing a build ships with.
/// </para>
/// <para>
/// When Stage 3 lands this file is replaced by tests for what the arguments actually do, and VM0019
/// goes with it.
/// </para>
/// </remarks>
public class ProfileAttributionTests {

    private static string Model(string members) => $$"""
        using ValidationModules;
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed class V1 : IValidationProfile;
        public sealed class V2 : IValidationProfile<V1>;
        public sealed class Strict : IValidationProfile;

        public record Pet {
        {{members}}
        }
        """;

    [Theory]
    [InlineData("[Required(FromProfile = typeof(V2))] public string? Tag { get; init; }")]
    [InlineData("[Required(UntilProfile = typeof(V2))] public string? Legacy { get; init; }")]
    [InlineData("[Required(Profiles = [typeof(Strict)])] public string? Sku { get; init; }")]
    [InlineData("[StringLength(1, 10, FromProfile = typeof(V2))] public string? Name { get; init; }")]
    [InlineData("[Range(0, 30, Profiles = [typeof(Strict)])] public int Age { get; init; }")]
    public void ProfileArgument_IsVM0019(string member) {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0019");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void VM0019_NamesTheAttributeAndTheProperty() {
        var result = GeneratorHarness.Run(Model(
            "[Required(FromProfile = typeof(V2))] public string? Tag { get; init; }"));

        var message = Assert.Single(result.Diagnostics, d => d.Id == "VM0019").GetMessage();

        Assert.Contains("Required", message);
        Assert.Contains("Tag", message);
    }

    [Fact]
    public void VM0019_SaysWhatCurrentlyHappensRatherThanOnlyThatItIsUnsupported() {
        // The consequential half. "Not implemented" invites the reader to assume the argument is
        // inert; it is not, and the rule is enforced where it was written not to be.
        var message = Assert
            .Single(
                GeneratorHarness.Run(Model("[Required(FromProfile = typeof(V2))] public string? Tag { get; init; }")).Diagnostics,
                d => d.Id == "VM0019")
            .GetMessage();

        Assert.Contains("enforced in every profile", message);
    }

    [Fact]
    public void ProfileArgument_IsReportedPerConstraintRatherThanOncePerType() {
        // The author has to remove each one, and a single diagnostic on the type would not say
        // which rule to look at.
        var result = GeneratorHarness.Run(Model("""
            [Required(FromProfile = typeof(V2))]
            public string? Tag { get; init; }

            [Required(UntilProfile = typeof(V2))]
            public string? Legacy { get; init; }
            """));

        Assert.Equal(2, result.Diagnostics.Count(d => d.Id == "VM0019"));
    }

    [Fact]
    public void OneConstraintCarryingTwoProfileArguments_ReportsOnce() {
        var result = GeneratorHarness.Run(Model(
            "[Required(FromProfile = typeof(V1), UntilProfile = typeof(V2))] public string? Tag { get; init; }"));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0019");
    }

    [Fact]
    public void UnprofiledConstraints_AreSilent() {
        // Profiles being unimplemented must cost nothing to a codebase that declares none — plan §2
        // requires that such a codebase never encounters the concept at all.
        var result = GeneratorHarness.Run(Model("""
            [Required]
            [StringLength(1, 10)]
            public string? Name { get; init; }

            [Range(0, 30)]
            public int Age { get; init; }
            """));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void DeclaringProfileTypesWithoutUsingThem_IsSilent() {
        // IValidationProfile is a public runtime type. Declaring one is harmless; attaching a rule
        // to it is what does not work.
        var result = GeneratorHarness.Run("""
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class V1 : IValidationProfile;
            public sealed class V2 : IValidationProfile<V1>;

            public record Pet {
                [Required] public string? Name { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void NullProfileArgument_IsSilentBecauseItRestrictsNothing() {
        var result = GeneratorHarness.Run(Model(
            "[Required(FromProfile = null)] public string? Tag { get; init; }"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0019");
    }

    [Fact]
    public void EmptyProfileSet_IsSilentBecauseItRestrictsNothing() {
        var result = GeneratorHarness.Run(Model(
            "[Required(Profiles = [])] public string? Tag { get; init; }"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0019");
    }

    [Fact]
    public void AssemblyLevelDefaultProfile_IsAlsoVM0019() {
        // [DefaultValidationProfile] promises the bare IValidatorFor<T> resolves to that profile's
        // rules. It resolves to all of them, so the promise is broken in the same direction.
        var result = GeneratorHarness.Run("""
            using ValidationModules;
            using ValidationModules.Constraints;

            [assembly: DefaultValidationProfile(typeof(Sample.V2))]

            namespace Sample;

            public sealed class V1 : IValidationProfile;
            public sealed class V2 : IValidationProfile<V1>;

            public record Pet {
                [Required] public string? Name { get; init; }
            }
            """);

        Assert.Contains("DefaultValidationProfile", Assert.Single(result.Diagnostics, d => d.Id == "VM0019").GetMessage());
    }

    [Fact]
    public void ProfiledRuleIsStillEmitted_SoTheDiagnosticIsTheOnlyFailure() {
        // The constraint is not dropped. Dropping it would turn one clear error into that plus a
        // model quietly missing a rule, and the build has already failed on the error.
        var result = GeneratorHarness.Run(Model(
            "[Required(FromProfile = typeof(V2))] public string? Tag { get; init; }"));

        Assert.Contains("ctx.AddRequired(\"tag\")", result.Sources["Sample.PetValidator.g.cs"]);
        Assert.Empty(result.CompilationErrors);
    }
}
