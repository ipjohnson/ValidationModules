using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Characterization tests for profile attribution, which the attributes accept and the generator
/// ignores.
/// </summary>
/// <remarks>
/// <para>
/// <b>These pin a gap, not a design.</b> Profiles are Stage 3 of the plan and are not built. What
/// makes that worth a test rather than a line in the README is that the *declaration* surface
/// shipped ahead of the implementation: <c>ValidationConstraintAttribute</c> carries
/// <c>FromProfile</c>, <c>UntilProfile</c> and <c>Profiles</c>, all three are documented at length
/// on the attribute itself, and <c>IValidationProfile</c> exists in the runtime.
/// </para>
/// <para>
/// So the code compiles, reads exactly as the design describes, and does something else. A rule
/// written <c>[Required(FromProfile = typeof(V2))]</c> — meaning "not required before V2" — is
/// enforced unconditionally, including under V1, with no diagnostic. That is the failure direction
/// that matters: a rule applying where it should not reject data a caller was entitled to send.
/// </para>
/// <para>
/// Plan §11 reserves VM0011–VM0015 and VM0020 for profile diagnostics; none of them is declared
/// yet, so there is not even a descriptor to switch on. When Stage 3 lands, these tests fail and
/// this file is replaced by real ones.
/// </para>
/// </remarks>
public class ProfileAttributionTests {

    private const string Profiled = """
        using ValidationModules;
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed class V1 : IValidationProfile;
        public sealed class V2 : IValidationProfile<V1>;
        public sealed class Strict : IValidationProfile;

        public record Pet {
            [Required]
            public string? Name { get; init; }

            [Required(FromProfile = typeof(V2))]
            public string? Tag { get; init; }

            [Required(UntilProfile = typeof(V2))]
            public string? Legacy { get; init; }

            [Required(Profiles = [typeof(Strict)])]
            public string? Sku { get; init; }
        }
        """;

    [Fact]
    public void ProfileArguments_ProduceNoDiagnostic() {
        Assert.Empty(GeneratorHarness.Run(Profiled).Diagnostics);
    }

    [Fact]
    public void OneValidatorIsEmitted_RatherThanOnePerProfile() {
        var result = GeneratorHarness.Run(Profiled);

        Assert.Contains("Sample.PetValidator.g.cs", result.Sources.Keys);
        Assert.DoesNotContain(result.Sources.Keys, name => name.Contains("_V1") || name.Contains("_V2"));
    }

    [Fact]
    public void EveryProfiledRuleIsEnforcedUnconditionally() {
        // The consequential assertion. FromProfile = V2 should not admit V1; UntilProfile = V2
        // should not admit V2; Profiles = [Strict] should admit neither. All four checks are
        // emitted side by side with nothing distinguishing them.
        var emitted = GeneratorHarness.Run(Profiled).Sources["Sample.PetValidator.g.cs"];

        Assert.Contains("ctx.AddRequired(\"name\")", emitted);
        Assert.Contains("ctx.AddRequired(\"tag\")", emitted);
        Assert.Contains("ctx.AddRequired(\"legacy\")", emitted);
        Assert.Contains("ctx.AddRequired(\"sku\")", emitted);
    }

    [Fact]
    public void NothingInTheEmittedValidatorMentionsAProfile() {
        var emitted = GeneratorHarness.Run(Profiled).Sources["Sample.PetValidator.g.cs"];

        Assert.DoesNotContain("Profile", emitted);
    }

    [Fact]
    public void NoDispatchTableIsEmitted() {
        // Plan §6 specifies a PetValidators.For(Type profile) switch so runtime profile selection
        // needs no MakeGenericType. Nothing emits one yet.
        var result = GeneratorHarness.Run(Profiled);

        Assert.DoesNotContain(result.Sources.Keys, name => name.Contains("Validators.g.cs"));
    }

    [Fact]
    public void RegistrationCarriesNoProfile() {
        // ValidatorRegistration has a Profile component, defaulted to null and never populated.
        var registration = GeneratorHarness.Run(Profiled).Sources["GeneratedValidatorRegistration.g.cs"];

        Assert.Contains("PetValidator.Instance", registration);
        Assert.DoesNotContain("typeof(global::Sample.V1)", registration);
        Assert.DoesNotContain("typeof(global::Sample.V2)", registration);
    }

    [Fact]
    public void ProfileArgumentsThatAreNonsense_AreAlsoAccepted() {
        // VM0011 would reject a profile argument that does not implement IValidationProfile, and
        // VM0013 a range that can never admit anything. Neither descriptor exists, so both compile.
        var result = GeneratorHarness.Run("""
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class V1 : IValidationProfile;
            public sealed class V2 : IValidationProfile<V1>;
            public sealed class NotAProfile;

            public record Pet {
                [Required(Profiles = [typeof(NotAProfile)])]
                public string? Name { get; init; }

                [Required(FromProfile = typeof(V2), UntilProfile = typeof(V1))]
                public string? Tag { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }
}
