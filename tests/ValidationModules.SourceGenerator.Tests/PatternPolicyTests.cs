using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Pins the gate on inline patterns.
/// </summary>
/// <remarks>
/// The inline form is correct and publishes AOT-clean; what it costs is size. Constructing a Regex
/// from a pattern string roots the regex parser and interpreter, measured at +1.16 MB on a
/// published AOT binary against +16 KB for the same pattern reached through a consumer-declared
/// [GeneratedRegex]. So the diagnostic is about a doubling of the binary, not a broken build, and
/// it only fires where that matters.
/// </remarks>
public class PatternPolicyTests {

    private const string InlinePattern = """
        using ValidationModules.Constraints;

        namespace Sample;

        public record Pet {
            [Pattern("^[A-Z]{3}$")]
            public string? Sku { get; init; }
        }
        """;

    private const string ReferencedPattern = """
        using System.Text.RegularExpressions;
        using ValidationModules.Constraints;

        namespace Sample;

        public static class PetPatterns {
            private static readonly Regex SkuValue = new Regex("^[A-Z]{3}$");
            public static Regex Sku() => SkuValue;
        }

        public record Pet {
            [Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
            public string? Sku { get; init; }
        }
        """;

    [Fact]
    public void InlinePattern_NotAotFacing_IsAccepted() {
        var result = GeneratorHarness.Run(InlinePattern);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0017");
        Assert.Contains(
            "new global::System.Text.RegularExpressions.Regex(",
            result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void InlinePattern_PublishAot_IsAnError() {
        var result = GeneratorHarness.Run(InlinePattern, ("PublishAot", "true"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0017");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void InlinePattern_IsAotCompatible_IsAlsoAnError() {
        // PublishAot is only ever true in the executable. A class library holding the models would
        // never see it, so gating on that alone would push the failure onto somebody else's publish.
        var result = GeneratorHarness.Run(InlinePattern, ("IsAotCompatible", "true"));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0017");
    }

    [Fact]
    public void InlinePattern_RejectedUnderAot_IsDroppedWhileTheRestOfTheTypeIsStillEmitted() {
        var source = """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Required]
                public string? Name { get; init; }

                [Pattern("^[A-Z]{3}$")]
                public string? Sku { get; init; }
            }
            """;

        var result = GeneratorHarness.Run(source, ("PublishAot", "true"));

        // The rejected constraint is dropped rather than emitted anyway, so the build fails with
        // VM0017 and not also with a second, less useful error out of the generated file. Every
        // other constraint on the type still compiles.
        var emitted = result.Sources["Sample.PetValidator.g.cs"];
        Assert.DoesNotContain("new global::System.Text.RegularExpressions.Regex(", emitted);
        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"name\", value: value.Name)", emitted);
    }

    [Fact]
    public void InlinePattern_PolicyWarn_ReportsButStillEmits() {
        var result = GeneratorHarness.Run(InlinePattern,
            ("PublishAot", "true"), ("ValidationModules_PatternPolicy", "Warn"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0017");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains(
            "new global::System.Text.RegularExpressions.Regex(",
            result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void InlinePattern_PolicyAllow_IsSilentEvenUnderAot() {
        var result = GeneratorHarness.Run(InlinePattern,
            ("PublishAot", "true"), ("ValidationModules_PatternPolicy", "Allow"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0017");
    }

    [Fact]
    public void InlinePattern_PolicyError_FiresWithoutAnyAotSignal() {
        // What a library shipping to AOT consumers sets, so the failure lands on its own build.
        var result = GeneratorHarness.Run(InlinePattern, ("ValidationModules_PatternPolicy", "Error"));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0017");
    }

    [Fact]
    public void ReferencedPattern_UnderAot_IsAcceptedAndCallsTheMember() {
        var result = GeneratorHarness.Run(ReferencedPattern, ("PublishAot", "true"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0017");

        var emitted = result.Sources["Sample.PetValidator.g.cs"];
        Assert.Contains("global::Sample.PetPatterns.Sku().IsMatch", emitted);
        Assert.DoesNotContain("new global::System.Text.RegularExpressions.Regex(", emitted);
    }

    [Theory]
    [InlineData("public static int Sku() => 0;", "does not return Regex")]
    [InlineData("public Regex Sku() => null!;", "is not static")]
    [InlineData("public static Regex Sku(int x) => null!;", "takes parameters")]
    public void ReferencedPattern_UnusableMember_IsAnError(string member, string reason) {
        var source = $$"""
            using System.Text.RegularExpressions;
            using ValidationModules.Constraints;

            namespace Sample;

            public class PetPatterns { {{member}} }

            public record Pet {
                [Pattern(typeof(PetPatterns), "Sku")]
                public string? Sku { get; init; }
            }
            """;

        var result = GeneratorHarness.Run(source, ("PublishAot", "true"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0018");
        Assert.Contains(reason, diagnostic.GetMessage());
    }

    [Fact]
    public void ReferencedPattern_MissingMember_IsAnError() {
        var source = """
            using ValidationModules.Constraints;

            namespace Sample;

            public class PetPatterns { }

            public record Pet {
                [Pattern(typeof(PetPatterns), "Nope")]
                public string? Sku { get; init; }
            }
            """;

        var result = GeneratorHarness.Run(source, ("PublishAot", "true"));

        Assert.Contains("does not exist", Assert.Single(result.Diagnostics, d => d.Id == "VM0018").GetMessage());
    }

    // MatchTimeoutMilliseconds - the attribute's only ReDoS mitigation.

    [Fact]
    public void MatchTimeout_IsPassedToTheEmittedRegex() {
        // The property was public, documented, and had exactly one occurrence in src/ - its own
        // declaration. A catastrophic pattern ran to completion however it was set.
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Pattern("^(a+)+$", MatchTimeoutMilliseconds = 250)]
                public string? Sku { get; init; }
            }
            """);

        var emitted = result.Sources["Sample.PetValidator.g.cs"];

        Assert.Contains("TimeSpan.FromMilliseconds(250)", emitted);
    }

    [Fact]
    public void NoMatchTimeout_KeepsTheSingleArgumentConstructor() {
        // Zero means no timeout, and the single-argument form is load-bearing: it lets ILC prove
        // RegexOptions.Compiled is never set and trim the RegexCompiler path with it, measured at
        // 713 KB. Honouring the timeout must not cost that where nobody asked for one.
        var result = GeneratorHarness.Run(InlinePattern);

        var emitted = result.Sources["Sample.PetValidator.g.cs"];

        Assert.Contains("new global::System.Text.RegularExpressions.Regex(", emitted);
        Assert.DoesNotContain("TimeSpan", emitted);
    }
}
