using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The diagnostics the DataAnnotations front end reports: what it will not compile, and what it
/// compiles with semantics worth stating.
/// </summary>
/// <remarks>
/// API-SURFACE.md §18 accepts <c>System.ComponentModel.DataAnnotations</c> as a second vocabulary,
/// which makes silence dangerous in a way it is not for the native attributes: an attribute this
/// generator skips still looks enforced, because the reader has every reason to believe
/// <c>Validator.TryValidateObject</c> would have honoured it. So everything recognised and *not*
/// compiled says so at build time - and the format validators, which *are* compiled, say exactly
/// what check was emitted, because the BCL's semantics are looser than the attribute names
/// suggest.
/// </remarks>
public class DataAnnotationsDiagnosticsTests {

    private static string Model(string members, string usings = "") => $$"""
        using System;
        using System.Collections.Generic;
        using System.ComponentModel.DataAnnotations;
        {{usings}}

        namespace Sample;

        public class Customer {
        {{members}}
        }
        """;

    // VM0010 — the vocabulary is switched off, so this library leaves the constraint alone.

    [Fact]
    public void DataAnnotations_SetToIgnore_ReportsVM0010PerSkippedConstraint() {
        var result = GeneratorHarness.Run(
            Model("""
                [Required]
                public string? Name { get; set; }
                """),
            ("ValidationModules_DataAnnotations", "Ignore"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0010");

        // Info, not Warning: the project asked for Ignore, so the skip is configuration working.
        // And the message names ValidationModules as the one ignoring, because the attribute stays
        // in the compilation and another validation system may still enforce it.
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("ValidationModules is ignoring", diagnostic.GetMessage());
        Assert.Contains("another validation system may still enforce it", diagnostic.GetMessage());
    }

    [Fact]
    public void DataAnnotations_SetToIgnore_ReportsEveryConstraintNotJustTheFirst() {
        var result = GeneratorHarness.Run(
            Model("""
                [Required]
                [StringLength(100, MinimumLength = 1)]
                public string? Name { get; set; }

                [Range(0, 120)]
                public int Age { get; set; }
                """),
            ("ValidationModules_DataAnnotations", "Ignore"));

        // Two on Name, one on Age — the report is per constraint, not per property.
        Assert.Equal(3, result.Diagnostics.Count(d => d.Id == "VM0010"));
    }

    [Fact]
    public void DataAnnotations_SetToIgnore_EmitsNoValidatorForADataAnnotationsOnlyType() {
        var result = GeneratorHarness.Run(
            Model("""
                [Required]
                public string? Name { get; set; }
                """),
            ("ValidationModules_DataAnnotations", "Ignore"));

        Assert.DoesNotContain("Sample.CustomerValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void DataAnnotations_Default_IsCompiledAndSilent() {
        // Compiling them is the default; Ignore is the opt-out, not the other way round.
        var result = GeneratorHarness.Run(Model("""
            [Required]
            public string? Name { get; set; }
            """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0010");
        Assert.Contains("Sample.CustomerValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void DataAnnotations_SetToIgnore_LeavesNativeConstraintsAlone() {
        // The switch governs one vocabulary. A type carrying both keeps the native half.
        var result = GeneratorHarness.Run(
            Model(
                """
                    [System.ComponentModel.DataAnnotations.Required]
                    public string? Ignored { get; set; }

                    [ValidationModules.Constraints.Required]
                    public string? Kept { get; set; }
                """),
            ("ValidationModules_DataAnnotations", "Ignore"));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0010");
        Assert.Contains("\"kept\"", result.Sources["Sample.CustomerValidator.g.cs"]);
    }

    // VM0060 — an attribute carrying arbitrary code, which cannot be compiled by reading metadata.

    [Fact]
    public void CustomValidationAttribute_IsVM0060() {
        var source = """
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public sealed class EvenNumberAttribute : ValidationAttribute {
                public override bool IsValid(object? value) => value is int number && number % 2 == 0;
            }

            public class Customer {
                [EvenNumber]
                [Required]
                public string? Name { get; set; }
            }
            """;

        var result = GeneratorHarness.Run(source);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0060");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("EvenNumberAttribute", diagnostic.GetMessage());
        Assert.Contains("It is not enforced", diagnostic.GetMessage());
    }

    [Fact]
    public void CustomValidationAttribute_UnderIgnore_IsVM0060AsInfo() {
        // The custom attribute fires in both modes — it can never be compiled — but under Ignore
        // the project has said DataAnnotations belong to someone else, so the report drops to
        // Info and says which library is doing the ignoring.
        var source = """
            using System.ComponentModel.DataAnnotations;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class EvenNumberAttribute : ValidationAttribute {
                public override bool IsValid(object? value) => value is int number && number % 2 == 0;
            }

            public class Customer {
                [EvenNumber]
                [ValidationModules.Constraints.Required]
                public string? Name { get; set; }
            }
            """;

        var result = GeneratorHarness.Run(source, ("ValidationModules_DataAnnotations", "Ignore"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0060");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("ValidationModules is ignoring it", diagnostic.GetMessage());
        Assert.Contains("another validation system may still enforce it", diagnostic.GetMessage());
    }

    [Fact]
    public void CustomValidationAttribute_FromTheDataAnnotationsNamespace_IsAlsoVM0060() {
        // [CustomValidation] points at a method by name and reflects to call it — the one thing this
        // library exists to avoid, and unresolvable at build time regardless.
        var result = GeneratorHarness.Run(Model("""
            [CustomValidation(typeof(Customer), "Check")]
            [Required]
            public string? Name { get; set; }

            public static ValidationResult? Check(object value) => ValidationResult.Success;
            """));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0060");
    }

    // VM0061 — a rule about two members, which a per-property constraint cannot express.

    [Fact]
    public void CompareAttribute_IsVM0061() {
        var result = GeneratorHarness.Run(Model("""
            public string? Password { get; set; }

            [Compare(nameof(Password))]
            public string? Confirm { get; set; }
            """));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0061");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Confirm", diagnostic.GetMessage());
    }

    // VM0063 — the format validators compile to the BCL's own checks, and the Info states which.

    [Theory]
    [InlineData("EmailAddress", "IsEmail", "ReportEmail")]
    [InlineData("Phone", "IsPhone", "ReportPhone")]
    [InlineData("Url", "IsUrl", "ReportUrl")]
    [InlineData("CreditCard", "IsCreditCard", "ReportCreditCard")]
    [InlineData("Base64String", "IsBase64", "ReportBase64")]
    public void FormatValidator_CompilesTheCheckAndReportsVM0063AsInfo(
        string attribute, string check, string report) {
        var result = GeneratorHarness.Run(Model($$"""
            [{{attribute}}]
            [Required]
            public string? Value { get; set; }
            """));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0063");

        // Info, not Warning: the attribute is enforced, identically to every other DataAnnotations
        // consumer, so there is nothing to fix - only the compiled semantics, stated verbatim.
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains($"{attribute}Attribute", diagnostic.GetMessage());
        Assert.Contains("compiles to the DataAnnotations check", diagnostic.GetMessage());

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        Assert.Contains($"ConstraintChecks.{check}", emitted);
        Assert.Contains(report, emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void FormatValidator_EmailInfoStatesTheSemanticsExactly() {
        // The check is looser than the attribute's name suggests - by the BCL's design, and
        // consistently with RFC 5322 - so the Info says precisely what passes, at the site that
        // declared it, and still points at [Pattern] for anyone who wanted more.
        var result = GeneratorHarness.Run(Model("""
            [EmailAddress]
            public string? Email { get; set; }
            """));

        var message = Assert.Single(result.Diagnostics, d => d.Id == "VM0063").GetMessage();

        Assert.Contains("'a@b' passes", message);
        Assert.Contains("[Pattern]", message);
    }

    [Fact]
    public void FormatValidator_OnANonStringMember_IsVM0001AndNoInfo() {
        // DataAnnotations would run [EmailAddress] against the int and fail every value; a rule
        // that can never pass is a build error here, and the Info stays quiet rather than
        // narrating semantics beside an error that removes them.
        var result = GeneratorHarness.Run(Model("""
            [EmailAddress]
            public int Age { get; set; }
            """));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0001");

        Assert.Contains("[EmailAddress]", diagnostic.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0063");
    }

    [Fact]
    public void Url_OnAUriMember_CompilesTheUriOverload() {
        // The one format kind with a second legal member type. The emitted call is textually
        // identical; overload resolution picks the Uri form. net8's UrlAttribute rejects every
        // Uri - the branch arrived later - and one semantics is emitted for both TFMs, which
        // ConstraintChecksTests pins as deliberate.
        var result = GeneratorHarness.Run(Model("""
            [Url]
            public Uri? Homepage { get; set; }
            """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0001");
        Assert.Contains("ConstraintChecks.IsUrl", result.Sources["Sample.CustomerValidator.g.cs"]);
        Assert.Contains(
            "absolute with scheme http, https or ftp",
            Assert.Single(result.Diagnostics, d => d.Id == "VM0063").GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void FileExtensions_DefaultSetIsNormalizedAndHoisted() {
        var result = GeneratorHarness.Run(Model("""
            [FileExtensions]
            public string? Avatar { get; set; }
            """));

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        // jquery validate's defaults, dot-prefixed at build time, hoisted like a pattern, and
        // joined once into the report rather than joined per failure.
        Assert.Contains("\".png\"", emitted);
        Assert.Contains("\".gif\"", emitted);
        Assert.Contains("AvatarExtensions0", emitted);
        Assert.Contains("ReportFileExtension", emitted);
        Assert.Contains(".png, .jpg, .jpeg, .gif", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void FileExtensions_NormalizesACustomSetTheWayTheAttributeDoes() {
        // Spaces and dots removed, lowercased, split on commas - so "tar.gz" becomes ".targz",
        // which is the attribute's own quirk reproduced rather than repaired.
        var result = GeneratorHarness.Run(Model("""
            [FileExtensions(Extensions = " .PNG, tar.gz ")]
            public string? Upload { get; set; }
            """));

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        Assert.Contains("\".png\"", emitted);
        Assert.Contains("\".targz\"", emitted);
    }

    [Fact]
    public void FormatValidator_WithAnErrorMessage_EmitsTheLiteralAndKeepsTheCode() {
        var result = GeneratorHarness.Run(Model("""
            [EmailAddress(ErrorMessage = "That is not an email we can reach.")]
            public string? Email { get; set; }
            """));

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        Assert.Contains("That is not an email we can reach.", emitted);
        Assert.Contains("ValidationCodes.Email", emitted);
    }

    [Fact]
    public void FormatValidator_UnderIgnore_IsVM0010LikeAnyOtherConstraint() {
        // Now that the format validators compile, Ignore mode owes them the same news it gives
        // [Required]: this library is leaving the attribute alone, and someone else may not.
        var result = GeneratorHarness.Run(
            Model("""
                [EmailAddress]
                public string? Email { get; set; }
                """),
            ("ValidationModules_DataAnnotations", "Ignore"));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0010");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0063");
    }

    // VM0064 — a length constraint on a member that is neither a string nor a collection.

    [Theory]
    [InlineData("[MinLength(1)] public int Age { get; set; }")]
    [InlineData("[MaxLength(10)] public int Age { get; set; }")]
    [InlineData("[Length(1, 10)] public int Age { get; set; }")]
    public void LengthOnUnsupportedMember_IsVM0064(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM0064").Severity);
    }

    [Theory]
    [InlineData("[MinLength(1)] public string? Name { get; set; }")]
    [InlineData("[MaxLength(10)] public string? Name { get; set; }")]
    [InlineData("[MinLength(1)] public List<string> Tags { get; set; } = new();")]
    [InlineData("[MaxLength(10)] public string[] Tags { get; set; } = [];")]
    [InlineData("[Length(1, 10)] public List<string> Tags { get; set; } = new();")]
    public void LengthOnStringOrCollection_IsSilent(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0064");
    }

    [Fact]
    public void LengthOnAString_BecomesAStringLengthAndOnACollectionAnItemCount() {
        // The same attribute reads as two different constraints depending on the member's type,
        // which is what DataAnnotations means by it. The diagnostic exists for the third case.
        var result = GeneratorHarness.Run(Model("""
            [MaxLength(10)]
            public string? Name { get; set; }

            [MaxLength(3)]
            public List<string> Tags { get; set; } = new();
            """));

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];
        Assert.Contains("ReportStringLength", emitted);
        Assert.Contains("ReportItemCount", emitted);
    }

    // VM0067 — IValidatableObject, whose Validate method the generated validator does not call.

    [Fact]
    public void ValidatableObject_IsVM0067() {
        var source = """
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public class Customer : IValidatableObject {
                [Required]
                public string? Name { get; set; }

                public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) {
                    yield break;
                }
            }
            """;

        var result = GeneratorHarness.Run(source);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0067");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Customer", diagnostic.GetMessage());
        Assert.Contains("not called by the generated validator", diagnostic.GetMessage());
    }

    [Fact]
    public void ValidatableObject_UnderIgnore_IsVM0067AsInfo() {
        var source = """
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;
            using ValidationModules.Constraints;

            namespace Sample;

            public class Customer : IValidatableObject {
                [ValidationModules.Constraints.Required]
                public string? Name { get; set; }

                public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) {
                    yield break;
                }
            }
            """;

        var result = GeneratorHarness.Run(source, ("ValidationModules_DataAnnotations", "Ignore"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0067");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("ValidationModules is ignoring its Validate method", diagnostic.GetMessage());
        Assert.Contains("another validation system may still call it", diagnostic.GetMessage());
    }

    [Fact]
    public void ValidatableObject_StillEmitsAValidatorForTheConstraintsItDoesUnderstand() {
        // The warning is about the half that is not compiled. Dropping the type entirely would be a
        // worse answer than validating what can be validated and saying what was left out.
        var source = """
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public class Customer : IValidatableObject {
                [Required]
                public string? Name { get; set; }

                public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) {
                    yield break;
                }
            }
            """;

        var result = GeneratorHarness.Run(source);

        Assert.Contains("\"name\"", result.Sources["Sample.CustomerValidator.g.cs"]);
    }

    [Fact]
    public void PlainModel_DoesNotReportVM0067() {
        var result = GeneratorHarness.Run(Model("""
            [Required]
            public string? Name { get; set; }
            """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0067");
    }

    // The clean case, so none of the above can pass because the front end never ran.

    [Fact]
    public void WellFormedDataAnnotationsModel_ProducesNoDiagnosticsAndCompiles() {
        var result = GeneratorHarness.Run(Model("""
            [Required]
            [StringLength(100, MinimumLength = 1)]
            public string? Name { get; set; }

            [Range(0, 120)]
            public int Age { get; set; }

            [RegularExpression("^[A-Z]{3}$")]
            public string? Sku { get; set; }

            [MaxLength(5)]
            public List<string> Tags { get; set; } = new();
            """));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }
}
