using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Pins the gate on inline patterns.
/// </summary>
/// <remarks>
/// The inline form is correct and publishes AOT-clean; what it costs is size. Constructing a Regex
/// from a pattern string roots the regex parser and interpreter, measured at +448 KB on a
/// published AOT binary against +16 KB for the same pattern reached through a consumer-declared
/// [GeneratedRegex]. So the diagnostic is about a binary roughly 40% larger, not a broken build,
/// and it only fires where that matters.
/// </remarks>
public class PatternPolicyTests
{
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
    public void InlinePattern_NotAotFacing_IsAccepted()
    {
        var result = GeneratorHarness.Run(InlinePattern);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1301");
        Assert.Contains(
            "new global::System.Text.RegularExpressions.Regex(",
            result.Sources["Sample.PetValidator.g.cs"]
        );
    }

    [Fact]
    public void InlinePattern_PublishAot_IsAnError()
    {
        var result = GeneratorHarness.Run(InlinePattern, ("PublishAot", "true"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1301");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void InlinePattern_IsAotCompatible_IsAlsoAnError()
    {
        // PublishAot is only ever true in the executable. A class library holding the models would
        // never see it, so gating on that alone would push the failure onto somebody else's publish.
        var result = GeneratorHarness.Run(InlinePattern, ("IsAotCompatible", "true"));

        Assert.Single(result.Diagnostics, d => d.Id == "VM1301");
    }

    [Fact]
    public void InlinePattern_RejectedUnderAot_IsDroppedWhileTheRestOfTheTypeIsStillEmitted()
    {
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
        // VM1301 and not also with a second, less useful error out of the generated file. Every
        // other constraint on the type still compiles.
        var emitted = result.Sources["Sample.PetValidator.g.cs"];
        Assert.DoesNotContain("new global::System.Text.RegularExpressions.Regex(", emitted);
        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"name\", value: value.Name)",
            emitted
        );
    }

    [Fact]
    public void InlinePattern_PolicyWarn_ReportsButStillEmits()
    {
        var result = GeneratorHarness.Run(
            InlinePattern,
            ("PublishAot", "true"),
            ("ValidationModules_PatternPolicy", "Warn")
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1301");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains(
            "new global::System.Text.RegularExpressions.Regex(",
            result.Sources["Sample.PetValidator.g.cs"]
        );
    }

    [Fact]
    public void InlinePattern_PolicyAllow_IsSilentEvenUnderAot()
    {
        var result = GeneratorHarness.Run(
            InlinePattern,
            ("PublishAot", "true"),
            ("ValidationModules_PatternPolicy", "Allow")
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1301");
    }

    [Fact]
    public void InlinePattern_PolicyError_FiresWithoutAnyAotSignal()
    {
        // What a library shipping to AOT consumers sets, so the failure lands on its own build.
        var result = GeneratorHarness.Run(
            InlinePattern,
            ("ValidationModules_PatternPolicy", "Error")
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1301");
    }

    [Fact]
    public void ReferencedPattern_UnderAot_IsAcceptedAndCallsTheMember()
    {
        var result = GeneratorHarness.Run(ReferencedPattern, ("PublishAot", "true"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1301");

        var emitted = result.Sources["Sample.PetValidator.g.cs"];
        Assert.Contains(
            "global::ValidationModules.ConstraintChecks.IsMatch(global::Sample.PetPatterns.Sku(), value.Sku)",
            emitted
        );
        Assert.DoesNotContain("new global::System.Text.RegularExpressions.Regex(", emitted);
    }

    // [RegularExpression] - the same inline Regex field, so the same policy.

    private const string RegularExpressionModel = """
        namespace Sample;

        public record Form {
            [System.ComponentModel.DataAnnotations.Required]
            public string? Name { get; init; }

            [System.ComponentModel.DataAnnotations.RegularExpression("[A-Z]{3}")]
            public string? Code { get; init; }
        }
        """;

    [Fact]
    public void RegularExpression_PublishAot_IsAnErrorAndIsDropped()
    {
        // It compiles to the inline form's field and roots the same parser and interpreter, so an
        // AOT-facing project pays the same 448 KB for it. Dropped like the inline [Pattern], with
        // the rest of the type still emitted.
        var result = GeneratorHarness.Run(RegularExpressionModel, ("PublishAot", "true"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1301");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

        var emitted = result.Sources["Sample.FormValidator.g.cs"];
        Assert.DoesNotContain("new global::System.Text.RegularExpressions.Regex(", emitted);
        Assert.Contains("ReportRequired(ctx, \"name\"", emitted);
    }

    [Fact]
    public void RegularExpression_PolicyWarn_ReportsButStillEmits()
    {
        var result = GeneratorHarness.Run(
            RegularExpressionModel,
            ("PublishAot", "true"),
            ("ValidationModules_PatternPolicy", "Warn")
        );

        Assert.Equal(
            DiagnosticSeverity.Warning,
            Assert.Single(result.Diagnostics, d => d.Id == "VM1301").Severity
        );
        Assert.Contains(
            "new global::System.Text.RegularExpressions.Regex(",
            result.Sources["Sample.FormValidator.g.cs"]
        );
    }

    [Fact]
    public void RegularExpression_NotAotFacing_IsAccepted()
    {
        var result = GeneratorHarness.Run(RegularExpressionModel);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1301");
    }

    [Fact]
    public void RegularExpression_VM1301_PrintsTheReplacementWithTheSameMeaning()
    {
        // Replacing the attribute is the fix, and the expression changes on the way: anchored,
        // because [RegularExpression] matches the whole value, and optional, because it passes an
        // empty one. It keeps the attribute's timeout, which is 2000 milliseconds when unset.
        // Printing it is what saves the reader from working that out.
        var result = GeneratorHarness.Run(RegularExpressionModel, ("PublishAot", "true"));

        var message = Assert.Single(result.Diagnostics, d => d.Id == "VM1301").GetMessage();

        Assert.Contains(
            """[GeneratedRegex(@"\A(?:[A-Z]{3})?\z", RegexOptions.None, matchTimeoutMilliseconds: 2000)]""",
            message
        );
        Assert.Contains(
            "replace [RegularExpression] with [Pattern(typeof(FormPatterns), nameof(FormPatterns.Code))]",
            message
        );
    }

    [Fact]
    public void InlinePattern_VM1301_PointsAtTheReferencedForm()
    {
        var result = GeneratorHarness.Run(InlinePattern, ("PublishAot", "true"));

        Assert.Contains(
            "point at it: [Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1301").GetMessage()
        );
    }

    [Fact]
    public void RegularExpression_UnderIgnore_IsNotReported()
    {
        // Nothing is compiled from it, so there is no inline field to object to. VM2001 is the
        // news there.
        var result = GeneratorHarness.Run(
            RegularExpressionModel,
            ("PublishAot", "true"),
            ("ValidationModules_DataAnnotations", "Ignore")
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1301");
    }

    // Options and MatchTimeoutMilliseconds on the reference form - VM1303.

    [Theory]
    [InlineData("Options = RegexOptions.IgnoreCase")]
    [InlineData("MatchTimeoutMilliseconds = 50")]
    public void ReferencedPattern_SettingItCannotRead_IsVM1303(string setting)
    {
        // The referenced regex was built with its own options and timeout, so the setting does
        // nothing. IgnoreCase in particular reads as a case-insensitive rule over a case-sensitive
        // check.
        var result = GeneratorHarness.Run(ReferencedPatternWith(setting));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1303");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains($"'{setting}'", diagnostic.GetMessage());
        Assert.Contains("'Sample.PetPatterns.Sku'", diagnostic.GetMessage());
    }

    [Fact]
    public void ReferencedPattern_VM1303_PrintsTheGeneratedRegexToWrite()
    {
        var result = GeneratorHarness.Run(
            """
            using System.Text.RegularExpressions;
            using ValidationModules.Constraints;

            namespace Sample;

            public static class VoucherPatterns {
                [GeneratedRegex("^[A-Z]+$", RegexOptions.CultureInvariant)]
                public static Regex Code() => new("^[A-Z]+$");
            }

            public record Voucher {
                [Pattern(typeof(VoucherPatterns), nameof(VoucherPatterns.Code), Options = RegexOptions.IgnoreCase, MatchTimeoutMilliseconds = 50)]
                public string? Code { get; init; }
            }
            """
        );

        // Read off the member's own declaration and merged with what [Pattern] asked for.
        Assert.Contains(
            """Declare it on the regex instead: [GeneratedRegex(@"^[A-Z]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 50)]""",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1303").GetMessage()
        );
    }

    [Fact]
    public void ReferencedPattern_VM1303_WithNoDeclarationToRead_PrintsAPlaceholder()
    {
        // A field holding a regex built elsewhere has no [GeneratedRegex] to merge into.
        var result = GeneratorHarness.Run(
            ReferencedPatternWith("Options = RegexOptions.IgnoreCase")
        );

        Assert.Contains(
            """[GeneratedRegex("...", RegexOptions.IgnoreCase)]""",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1303").GetMessage()
        );
    }

    [Fact]
    public void ReferencedPattern_Compiled_IsVM1303AndNotVM1302()
    {
        // There is no Regex constructor on this form to take the flag, so VM1302's news does not
        // apply, and a [GeneratedRegex] is compiled at build time already.
        var result = GeneratorHarness.Run(ReferencedPatternWith("Options = RegexOptions.Compiled"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1302");
        Assert.Contains(
            "Remove it",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1303").GetMessage()
        );
    }

    [Fact]
    public void ReferencedPattern_WithoutSettings_IsSilent()
    {
        var result = GeneratorHarness.Run(ReferencedPattern);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1303");
    }

    private static string ReferencedPatternWith(string setting) =>
        $$"""
            using System.Text.RegularExpressions;
            using ValidationModules.Constraints;

            namespace Sample;

            public static class PetPatterns {
                private static readonly Regex SkuValue = new Regex("^[A-Z]{3}$");
                public static Regex Sku() => SkuValue;
            }

            public record Pet {
                [Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku), {{setting}})]
                public string? Sku { get; init; }
            }
            """;

    // A match timeout fails the pattern rather than throwing out of Validate.

    [Fact]
    public void EveryPatternForm_MatchesThroughTheTimeoutSafeCheck()
    {
        // The input that exhausts a timeout is the hostile input the timeout exists for, so it has
        // to become a validation failure. The regex's own IsMatch throws instead, and an endpoint
        // answers 500. Both paths of both forms go through the check: Validate and IsValid.
        foreach (var source in new[] { InlinePattern, ReferencedPattern })
        {
            var emitted = GeneratorHarness.Run(source).Sources["Sample.PetValidator.g.cs"];
            var calls =
                emitted.Split("global::ValidationModules.ConstraintChecks.IsMatch(").Length - 1;

            Assert.Equal(2, calls);
            Assert.DoesNotContain(".IsMatch(value.Sku)", emitted);
        }
    }

    [Fact]
    public void RulesClassPattern_MatchesThroughTheTimeoutSafeCheck()
    {
        var result = GeneratorHarness.Run(
            """
            using System.Text.RegularExpressions;
            using ValidationModules;

            namespace Sample;

            public sealed class Product {
                public string? Sku { get; init; }
            }

            public static class ProductPatterns {
                private static readonly Regex SkuValue = new("^[A-Z]{3}$");
                public static Regex Sku() => SkuValue;
            }

            public sealed class ProductRules : IValidationRulesFor<Product> {
                public static void Describe(ValidationRules<Product> rules, Product x) {
                    rules.Pattern(x.Sku, ProductPatterns.Sku);
                }
            }
            """
        );

        Assert.Contains(
            "global::ValidationModules.ConstraintChecks.IsMatch(global::Sample.ProductPatterns.Sku(), x.Sku)",
            result.Sources["Sample.ProductRules_Rules.g.cs"]
        );
    }

    // An empty string passes [RegularExpression] untested, as it does in DataAnnotations.

    [Fact]
    public void RegularExpression_PassesAnEmptyStringWithoutMatching()
    {
        // RegularExpressionAttribute.IsValid returns true for null and "", and leaves emptiness to
        // [Required]. An HTML form posts an optional field left blank as "".
        var emitted = GeneratorHarness.Run(RegularExpressionModel).Sources[
            "Sample.FormValidator.g.cs"
        ];

        Assert.Contains("!string.IsNullOrEmpty(value.Code) && !global::ValidationModules", emitted);
    }

    [Fact]
    public void NativePattern_StillMatchesAnEmptyString()
    {
        // The native attribute follows JSON Schema, where "" is a string like any other.
        var emitted = GeneratorHarness.Run(InlinePattern).Sources["Sample.PetValidator.g.cs"];

        Assert.DoesNotContain("IsNullOrEmpty", emitted);
        Assert.Contains("value.Sku is not null && !global::ValidationModules", emitted);
    }

    [Theory]
    [InlineData("public static int Sku() => 0;", "does not return Regex")]
    [InlineData("public Regex Sku() => null!;", "is not static")]
    [InlineData("public static Regex Sku(int x) => null!;", "takes parameters")]
    public void ReferencedPattern_UnusableMember_IsAnError(string member, string reason)
    {
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

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1107");
        Assert.Contains(reason, diagnostic.GetMessage());
    }

    [Fact]
    public void ReferencedPattern_MissingMember_IsAnError()
    {
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

        Assert.Contains(
            "does not exist",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1107").GetMessage()
        );
    }

    // MatchTimeoutMilliseconds - the attribute's only ReDoS mitigation.

    [Fact]
    public void MatchTimeout_IsPassedToTheEmittedRegex()
    {
        // The property was public, documented, and had exactly one occurrence in src/ - its own
        // declaration. A catastrophic pattern ran to completion however it was set.
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Pattern("^(a+)+$", MatchTimeoutMilliseconds = 250)]
                public string? Sku { get; init; }
            }
            """
        );

        var emitted = result.Sources["Sample.PetValidator.g.cs"];

        Assert.Contains("TimeSpan.FromMilliseconds(250)", emitted);
    }

    [Fact]
    public void NoMatchTimeout_KeepsTheSingleArgumentConstructor()
    {
        // An unset timeout means none, and the single-argument form is load-bearing: it lets ILC
        // prove RegexOptions.Compiled is never set and trim the RegexCompiler path with it,
        // measured at 713 KB. Honouring the timeout must not cost that where nobody asked for one.
        var result = GeneratorHarness.Run(InlinePattern);

        var emitted = result.Sources["Sample.PetValidator.g.cs"];

        Assert.Contains("new global::System.Text.RegularExpressions.Regex(", emitted);
        Assert.DoesNotContain("TimeSpan", emitted);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("-2147483648")]
    [InlineData("2147483647")]
    public void MatchTimeout_TheRegexConstructorRejects_IsVM1304AndIgnored(string timeout)
    {
        // Passed on, the value would throw from the validator's static Regex field when the type
        // initializes, and every validation of the type would fail with it. Zero counts, because
        // leaving the property unset is how to ask for no timeout.
        var result = GeneratorHarness.Run(InlinePatternWithTimeout(timeout));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1304");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains(
            $"[Pattern] on 'Sku' sets 'MatchTimeoutMilliseconds = {timeout}'",
            diagnostic.GetMessage()
        );
        Assert.Contains("or remove it for no timeout", diagnostic.GetMessage());
        Assert.DoesNotContain("TimeSpan", result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1")]
    [InlineData("2147483646")]
    public void MatchTimeout_TheRegexConstructorAccepts_IsNotReported(string timeout)
    {
        // -1 is Regex.InfiniteMatchTimeout in milliseconds, and 2147483646 is the longest timeout
        // the constructor takes.
        var result = GeneratorHarness.Run(InlinePatternWithTimeout(timeout));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1304");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void RegularExpression_WithoutATimeout_UsesTheDataAnnotationsDefault()
    {
        // RegularExpressionAttribute's constructor sets MatchTimeoutInMilliseconds to 2000, so a
        // model moved from DataAnnotations keeps that protection.
        var emitted = GeneratorHarness.Run(RegularExpressionModel).Sources[
            "Sample.FormValidator.g.cs"
        ];

        Assert.Contains("TimeSpan.FromMilliseconds(2000)", emitted);
    }

    [Fact]
    public void RegularExpression_MatchTimeout_IsPassedToTheEmittedRegex()
    {
        var emitted = GeneratorHarness
            .Run(RegularExpressionWith("MatchTimeoutInMilliseconds = 150"))
            .Sources["Sample.FormValidator.g.cs"];

        Assert.Contains("TimeSpan.FromMilliseconds(150)", emitted);
    }

    [Fact]
    public void RegularExpression_MinusOneTimeout_KeepsTheSingleArgumentConstructor()
    {
        // DataAnnotations builds the Regex from the pattern alone when the timeout is -1.
        var result = GeneratorHarness.Run(RegularExpressionWith("MatchTimeoutInMilliseconds = -1"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1304");
        Assert.DoesNotContain("TimeSpan", result.Sources["Sample.FormValidator.g.cs"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void RegularExpression_TimeoutTheRegexConstructorRejects_IsVM1304AndKeepsTheDefault(
        string timeout
    )
    {
        // RegularExpressionAttribute.IsValid throws on these values, so the model never validated
        // under DataAnnotations either.
        var result = GeneratorHarness.Run(
            RegularExpressionWith($"MatchTimeoutInMilliseconds = {timeout}")
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1304");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains(
            $"[RegularExpression] on 'Code' sets 'MatchTimeoutInMilliseconds = {timeout}'",
            diagnostic.GetMessage()
        );
        Assert.Contains("or -1 for no timeout", diagnostic.GetMessage());
        Assert.Contains(
            "TimeSpan.FromMilliseconds(2000)",
            result.Sources["Sample.FormValidator.g.cs"]
        );
    }

    private static string InlinePatternWithTimeout(string timeout) =>
        $$"""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Pattern("^[A-Z]{3}$", MatchTimeoutMilliseconds = {{timeout}})]
                public string? Sku { get; init; }
            }
            """;

    private static string RegularExpressionWith(string setting) =>
        $$"""
            namespace Sample;

            public record Form {
                [System.ComponentModel.DataAnnotations.RegularExpression("[A-Z]{3}", {{setting}})]
                public string? Code { get; init; }
            }
            """;
}
