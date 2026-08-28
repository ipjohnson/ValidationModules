using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The diagnostics that report what the DataAnnotations front end will not compile.
/// </summary>
/// <remarks>
/// API-SURFACE.md §18 accepts <c>System.ComponentModel.DataAnnotations</c> as a second vocabulary,
/// which makes silence dangerous in a way it is not for the native attributes: a
/// <c>[EmailAddress]</c> that this generator skips still looks enforced, because the reader has
/// every reason to believe <c>Validator.TryValidateObject</c> would have honoured it. So every
/// attribute that is recognised and *not* compiled says so at build time rather than at run time,
/// where it would say nothing at all.
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

    // VM0010 — the vocabulary is switched off, so a constraint that reads as enforced is not.

    [Fact]
    public void DataAnnotations_SetToIgnore_ReportsVM0010PerSkippedConstraint() {
        var result = GeneratorHarness.Run(
            Model("""
                [Required]
                public string? Name { get; set; }
                """),
            ("ValidationModules_DataAnnotations", "Ignore"));

        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(result.Diagnostics, d => d.Id == "VM0010").Severity);
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

    // VM0063 — the format validators, skipped on purpose rather than by omission.

    [Theory]
    [InlineData("EmailAddress")]
    [InlineData("Phone")]
    [InlineData("Url")]
    [InlineData("CreditCard")]
    [InlineData("Base64String")]
    public void FormatValidator_IsVM0063(string attribute) {
        var result = GeneratorHarness.Run(Model($$"""
            [{{attribute}}]
            [Required]
            public string? Value { get; set; }
            """));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0063");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains($"{attribute}Attribute", diagnostic.GetMessage());
    }

    [Fact]
    public void FormatValidator_MessagePointsAtThePatternReplacement() {
        // The reason it is skipped rather than approximated: DataAnnotations' EmailAddressAttribute
        // accepts anything with one '@' not at either end, which is far more lenient than what
        // almost anyone declaring [EmailAddress] believes they asked for. Emitting a [Pattern] the
        // author can read beats silently reproducing that.
        var result = GeneratorHarness.Run(Model("""
            [EmailAddress]
            public string? Email { get; set; }
            """));

        Assert.Contains("[Pattern]", Assert.Single(result.Diagnostics, d => d.Id == "VM0063").GetMessage());
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
