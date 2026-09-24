using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The diagnostics the DataAnnotations front end reports: what it will not compile, and what it
/// compiles with semantics worth stating.
/// </summary>
/// <remarks>
/// This generator accepts <c>System.ComponentModel.DataAnnotations</c> as a second vocabulary,
/// which makes silence dangerous in a way it is not for the native attributes: an attribute this
/// generator skips still looks enforced, because the reader has every reason to believe
/// <c>Validator.TryValidateObject</c> would have honoured it. So everything recognised and *not*
/// compiled says so at build time - and the format validators, which *are* compiled, say exactly
/// what check was emitted, because the BCL's semantics are looser than the attribute names
/// suggest.
/// </remarks>
public class DataAnnotationsDiagnosticsTests
{
    private static string Model(string members, string usings = "") =>
        $$"""
            using System;
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;
            {{usings}}

            namespace Sample;

            public class Customer {
            {{members}}
            }
            """;

    // VM2001 — the vocabulary is switched off, so this library leaves the constraint alone.

    [Fact]
    public void DataAnnotations_SetToIgnore_ReportsVM2001PerSkippedConstraint()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                public string? Name { get; set; }
                """
            ),
            ("ValidationModules_DataAnnotations", "Ignore")
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2001");

        // Info, not Warning: the project asked for Ignore, so the skip is configuration working.
        // And the message names ValidationModules as the one ignoring, because the attribute stays
        // in the compilation and another validation system may still enforce it.
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("ValidationModules is ignoring", diagnostic.GetMessage());
        Assert.Contains("another validation system may still enforce it", diagnostic.GetMessage());
    }

    [Fact]
    public void DataAnnotations_SetToIgnore_ReportsEveryConstraintNotJustTheFirst()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                [StringLength(100, MinimumLength = 1)]
                public string? Name { get; set; }

                [Range(0, 120)]
                public int Age { get; set; }
                """
            ),
            ("ValidationModules_DataAnnotations", "Ignore")
        );

        // Two on Name, one on Age — the report is per constraint, not per property.
        Assert.Equal(3, result.Diagnostics.Count(d => d.Id == "VM2001"));
    }

    [Fact]
    public void DataAnnotations_SetToIgnore_EmitsNoValidatorForADataAnnotationsOnlyType()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                public string? Name { get; set; }
                """
            ),
            ("ValidationModules_DataAnnotations", "Ignore")
        );

        Assert.DoesNotContain("Sample.CustomerValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void DataAnnotations_Default_IsCompiledAndSilent()
    {
        // Compiling them is the default; Ignore is the opt-out, not the other way round.
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                public string? Name { get; set; }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM2001");
        Assert.Contains("Sample.CustomerValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void DataAnnotations_SetToIgnore_LeavesNativeConstraintsAlone()
    {
        // The switch governs one vocabulary. A type carrying both keeps the native half.
        var result = GeneratorHarness.Run(
            Model(
                """
                    [System.ComponentModel.DataAnnotations.Required]
                    public string? Ignored { get; set; }

                    [ValidationModules.Constraints.Required]
                    public string? Kept { get; set; }
                """
            ),
            ("ValidationModules_DataAnnotations", "Ignore")
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM2001");
        Assert.Contains("\"kept\"", result.Sources["Sample.CustomerValidator.g.cs"]);
    }

    // VM2002 — a custom attribute is user code, so it is constructed once and invoked.

    [Fact]
    public void CustomValidationAttribute_IsConstructedOnceAndInvoked()
    {
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

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2002");

        // Info, not Warning: the attribute is enforced by running it, which is the only faithful
        // reading of user code. The message carries the cost model instead of a refusal.
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("EvenNumberAttribute", diagnostic.GetMessage());
        Assert.Contains("constructed once and invoked", diagnostic.GetMessage());

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        Assert.Contains("new global::Sample.EvenNumberAttribute()", emitted);
        Assert.Contains("DataAnnotationsSupport.Validate(ref ctx, NameCustom0", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void CustomValidationAttribute_ArgumentsAreRenderedFullyQualified()
    {
        // Constructor and named arguments are compile-time constants, re-rendered rather than
        // lifted as syntax, so the construction binds in a generated file with no usings.
        var source = """
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public sealed class DivisibleAttribute : ValidationAttribute {
                public DivisibleAttribute(int divisor) => Divisor = divisor;
                public int Divisor { get; }
                public bool Strict { get; set; }
                public override bool IsValid(object? value) => value is int n && n % Divisor == 0;
            }

            public class Customer {
                [Divisible(3, Strict = true, ErrorMessage = "must divide by three")]
                public int Count { get; set; }
            }
            """;

        var result = GeneratorHarness.Run(source);

        Assert.Contains(
            "new global::Sample.DivisibleAttribute(3) { Strict = true, ErrorMessage = \"must divide by three\" }",
            result.Sources["Sample.CustomerValidator.g.cs"]
        );
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void CustomValidationAttribute_WithResourceMessages_IsAlsoVM2009()
    {
        // The one part of an invoked attribute the trimmer can break, visible in metadata.
        var source = """
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public static class Messages {
                public static string Even => "must be even";
            }

            public sealed class EvenNumberAttribute : ValidationAttribute {
                public override bool IsValid(object? value) => value is int number && number % 2 == 0;
            }

            public class Customer {
                [EvenNumber(ErrorMessageResourceType = typeof(Messages), ErrorMessageResourceName = "Even")]
                public int Count { get; set; }
            }
            """;

        var result = GeneratorHarness.Run(source);

        Assert.Single(result.Diagnostics, d => d.Id == "VM2009");
        Assert.Contains(
            "typeof(global::Sample.Messages)",
            result.Sources["Sample.CustomerValidator.g.cs"]
        );
    }

    // VM2011 — ErrorMessageResourceName names nothing the generated validator can read.

    private static string ResourceModel(string name) =>
        $$"""
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public class ResBase {
                public static string Inherited => "{0} is missing";
            }

            public class Res : ResBase {
                public static string NameRequired => "{0} is missing";
                internal static string Internal => "{0} is missing";
                public const string Constant = "{0} is missing";
                public static int Count => 3;
                private static string Hidden => "{0} is missing";
                public static string Method() => "{0} is missing";
                public string Instance => "{0} is missing";
            }

            public class Customer {
                [Required(ErrorMessageResourceType = typeof(Res), ErrorMessageResourceName = "{{name}}")]
                public string? Name { get; set; }
            }
            """;

    /// <summary>
    /// The generated code reads the resource member directly, so each of these used to fail with a
    /// compiler error at a column of the generated file. The constraint now compiles with its
    /// default message, and the error is at the attribute.
    /// </summary>
    [Theory]
    [InlineData(
        "Nope",
        "'Sample.Res' has no property named 'Nope'. Write the name with nameof, as in "
            + "ErrorMessageResourceName = nameof(Sample.Res.Nope)"
    )]
    [InlineData("Instance", "'Sample.Res.Instance' is not static")]
    [InlineData("Count", "'Sample.Res.Count' is of type 'int', not string")]
    [InlineData("Hidden", "'Sample.Res.Hidden' cannot be read from the generated validator")]
    [InlineData("Method", "'Sample.Res.Method' is not a property")]
    public void ResourceNameThatDoesNotResolve_IsVM2011(string name, string reason)
    {
        var result = GeneratorHarness.Run(ResourceModel(name));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2011");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.StartsWith(
            $"'RequiredAttribute' on 'Name' sets ErrorMessageResourceName to \"{name}\", but ",
            diagnostic.GetMessage()
        );
        Assert.Contains(reason, diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("NameRequired")]
    [InlineData("Internal")]
    [InlineData("Constant")]
    [InlineData("Inherited")]
    public void ResourceNameTheGeneratedCodeCanRead_IsSilent(string name)
    {
        var result = GeneratorHarness.Run(ResourceModel(name));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM2011");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            $"global::Sample.Res.{name}",
            result.Sources["Sample.CustomerValidator.g.cs"]
        );
    }

    [Fact]
    public void InternalResourceInAnotherAssembly_IsVM2011()
    {
        // DataAnnotations would read it by reflection. The generated validator names it directly,
        // and an internal member of another assembly does not compile there.
        var result = GeneratorHarness.RunWithReference(
            """
            namespace Shared;

            public class Res {
                internal static string NameRequired => "{0} is missing";
            }
            """,
            """
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public class Customer {
                [Required(ErrorMessageResourceType = typeof(Shared.Res), ErrorMessageResourceName = "NameRequired")]
                public string? Name { get; set; }
            }
            """
        );

        Assert.Contains(
            "'Shared.Res' has no public property named 'NameRequired', and the generated validator "
                + "cannot read an internal one in another assembly. Make it public",
            Assert.Single(result.Diagnostics, d => d.Id == "VM2011").GetMessage()
        );
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void CustomValidationAttribute_UnderIgnore_IsVM2002AsInfo()
    {
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

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2002");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("ValidationModules is ignoring it", diagnostic.GetMessage());
        Assert.Contains("another validation system may still enforce it", diagnostic.GetMessage());
    }

    [Fact]
    public void CustomValidation_ResolvesToADirectStaticCall()
    {
        // DataAnnotations reflects to find this method per validation; the generator resolves it
        // once at build time and emits the call, so nothing dispatches by name at run time.
        var result = GeneratorHarness.Run(
            Model(
                """
                [CustomValidation(typeof(Customer), "Check")]
                [Required]
                public string? Name { get; set; }

                public static ValidationResult? Check(object value) => ValidationResult.Success;
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM2002");

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        Assert.Contains(
            "DataAnnotationsSupport.Apply(ref ctx, global::Sample.Customer.Check(value.Name)",
            emitted
        );
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void CustomValidation_ContextTakingOverload_GetsABuiltContext()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [CustomValidation(typeof(Customer), "Check")]
                public string? Name { get; set; }

                public static ValidationResult? Check(string? value, ValidationContext context) =>
                    ValidationResult.Success;
                """
            )
        );

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        Assert.Contains("global::Sample.Customer.Check(value.Name, ", emitted);
        Assert.Contains(
            "DataAnnotationsSupport.CreateContext(ctx.Services, value, \"Name\"",
            emitted
        );
        Assert.Empty(result.CompilationErrors);
    }

    // VM2008 — a [CustomValidation] target that cannot be called is an error, with the reason.

    [Theory]
    [InlineData(
        "public static ValidationResult? Check(int value) => ValidationResult.Success;",
        "cannot accept this member"
    )]
    [InlineData(
        "public static string Check(object value) => \"no\";",
        "does not return ValidationResult"
    )]
    [InlineData(
        "public ValidationResult? Check(object value) => ValidationResult.Success;",
        "is not a public static method"
    )]
    [InlineData("", "is not a public static method")]
    public void CustomValidation_UnusableTarget_IsVM2008(string method, string reason)
    {
        var result = GeneratorHarness.Run(
            Model(
                $$"""
                [CustomValidation(typeof(Customer), "Check")]
                public string? Name { get; set; }

                {{method}}
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2008");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(reason, diagnostic.GetMessage());
    }

    // VM2003 — a rule about two members, which a per-property constraint cannot express.

    [Fact]
    public void CompareAttribute_IsVM2003()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                public string? Password { get; set; }

                [Compare(nameof(Password))]
                public string? Confirm { get; set; }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2003");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Confirm", diagnostic.GetMessage());
    }

    // VM2007 — a runtime string conversion this library will not compile. It used to be dropped
    // in silence, the only validating DataAnnotations attribute that was.

    [Fact]
    public void EnumDataType_IsVM2007_NamingTheNativeReplacement()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                [EnumDataType(typeof(DayOfWeek))]
                public string? Day { get; set; }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2007");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("[EnumDefined]", diagnostic.GetMessage());

        // Not enforced means not emitted - the [Required] beside it still is.
        Assert.DoesNotContain("EnumDataType", result.Sources["Sample.CustomerValidator.g.cs"]);
    }

    // VM2004 — the format validators compile to the BCL's own checks, and the Info states which.

    [Theory]
    [InlineData("EmailAddress", "IsEmail", "ReportEmail")]
    [InlineData("Phone", "IsPhone", "ReportPhone")]
    [InlineData("Url", "IsUrl", "ReportUrl")]
    [InlineData("CreditCard", "IsCreditCard", "ReportCreditCard")]
    [InlineData("Base64String", "IsBase64", "ReportBase64")]
    public void FormatValidator_CompilesTheCheckAndReportsVM2004AsInfo(
        string attribute,
        string check,
        string report
    )
    {
        var result = GeneratorHarness.Run(
            Model(
                $$"""
                [{{attribute}}]
                [Required]
                public string? Value { get; set; }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2004");

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
    public void FormatValidator_EmailInfoStatesTheSemanticsExactly()
    {
        // The check is looser than the attribute's name suggests - by the BCL's design, and
        // consistently with RFC 5322 - so the Info says precisely what passes, at the site that
        // declared it, and still points at [Pattern] for anyone who wanted more.
        var result = GeneratorHarness.Run(
            Model(
                """
                [EmailAddress]
                public string? Email { get; set; }
                """
            )
        );

        var message = Assert.Single(result.Diagnostics, d => d.Id == "VM2004").GetMessage();

        Assert.Contains("'a@b' passes", message);
        Assert.Contains("[Pattern]", message);
    }

    [Fact]
    public void FormatValidator_OnANonStringMember_IsVM1001AndNoInfo()
    {
        // DataAnnotations would run [EmailAddress] against the int and fail every value; a rule
        // that can never pass is a build error here, and the Info stays quiet rather than
        // narrating semantics beside an error that removes them.
        var result = GeneratorHarness.Run(
            Model(
                """
                [EmailAddress]
                public int Age { get; set; }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1001");

        Assert.Contains("[EmailAddress]", diagnostic.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM2004");
    }

    [Fact]
    public void Url_OnAUriMember_CompilesTheUriOverload()
    {
        // The one format kind with a second legal member type. The emitted call is textually
        // identical; overload resolution picks the Uri form. net8's UrlAttribute rejects every
        // Uri - the branch arrived later - and one semantics is emitted for both TFMs, which
        // ConstraintChecksTests pins as deliberate.
        var result = GeneratorHarness.Run(
            Model(
                """
                [Url]
                public Uri? Homepage { get; set; }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1001");
        Assert.Contains("ConstraintChecks.IsUrl", result.Sources["Sample.CustomerValidator.g.cs"]);
        Assert.Contains(
            "absolute with scheme http, https or ftp",
            Assert.Single(result.Diagnostics, d => d.Id == "VM2004").GetMessage()
        );
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void FileExtensions_DefaultSetIsNormalizedAndHoisted()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [FileExtensions]
                public string? Avatar { get; set; }
                """
            )
        );

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        // jquery validate's defaults, dot-prefixed at build time, hoisted like a pattern, and
        // joined once into the report rather than joined per failure.
        Assert.Contains("\".png\"", emitted);
        Assert.Contains("\".gif\"", emitted);
        Assert.Contains("AvatarExtensions0", emitted);
        Assert.Contains(
            "global::ValidationModules.ValidationMessageTemplates.FileExtension",
            emitted
        );
        Assert.Contains(".png, .jpg, .jpeg, .gif", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void FileExtensions_NormalizesACustomSetTheWayTheAttributeDoes()
    {
        // Spaces and dots removed, lowercased, split on commas - so "tar.gz" becomes ".targz",
        // which is the attribute's own quirk reproduced rather than repaired.
        var result = GeneratorHarness.Run(
            Model(
                """
                [FileExtensions(Extensions = " .PNG, tar.gz ")]
                public string? Upload { get; set; }
                """
            )
        );

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        Assert.Contains("\".png\"", emitted);
        Assert.Contains("\".targz\"", emitted);
    }

    [Fact]
    public void FormatValidator_WithAnErrorMessage_EmitsTheLiteralAndKeepsTheCode()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [EmailAddress(ErrorMessage = "That is not an email we can reach.")]
                public string? Email { get; set; }
                """
            )
        );

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        Assert.Contains("That is not an email we can reach.", emitted);
        Assert.Contains("ValidationCodes.Email", emitted);
    }

    [Fact]
    public void FormatValidator_UnderIgnore_IsVM2001LikeAnyOtherConstraint()
    {
        // Now that the format validators compile, Ignore mode owes them the same news it gives
        // [Required]: this library is leaving the attribute alone, and someone else may not.
        var result = GeneratorHarness.Run(
            Model(
                """
                [EmailAddress]
                public string? Email { get; set; }
                """
            ),
            ("ValidationModules_DataAnnotations", "Ignore")
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM2001");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM2004");
    }

    // VM2005 — a length constraint on a member that is neither a string nor a collection.

    [Theory]
    [InlineData("[MinLength(1)] public int Age { get; set; }")]
    [InlineData("[MaxLength(10)] public int Age { get; set; }")]
    [InlineData("[Length(1, 10)] public int Age { get; set; }")]
    public void LengthOnUnsupportedMember_IsVM2005(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Equal(
            DiagnosticSeverity.Error,
            Assert.Single(result.Diagnostics, d => d.Id == "VM2005").Severity
        );
    }

    [Theory]
    [InlineData("[MinLength(1)] public string? Name { get; set; }")]
    [InlineData("[MaxLength(10)] public string? Name { get; set; }")]
    [InlineData("[MinLength(1)] public List<string> Tags { get; set; } = new();")]
    [InlineData("[MaxLength(10)] public string[] Tags { get; set; } = [];")]
    [InlineData("[Length(1, 10)] public List<string> Tags { get; set; } = new();")]
    public void LengthOnStringOrCollection_IsSilent(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM2005");
    }

    [Fact]
    public void LengthOnAString_BecomesAStringLengthAndOnACollectionAnItemCount()
    {
        // The same attribute reads as two different constraints depending on the member's type,
        // which is what DataAnnotations means by it. The diagnostic exists for the third case.
        var result = GeneratorHarness.Run(
            Model(
                """
                [MaxLength(10)]
                public string? Name { get; set; }

                [MaxLength(3)]
                public List<string> Tags { get; set; } = new();
                """
            )
        );

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];
        Assert.Contains(
            "global::ValidationModules.ValidationMessageTemplates.StringLengthAtMost, 10",
            emitted
        );
        Assert.Contains(
            "global::ValidationModules.ValidationMessageTemplates.ItemCountAtMost, 3",
            emitted
        );
    }

    // VM2006 — IValidatableObject, compiled with TryValidateObject's sequencing.

    [Fact]
    public void ValidatableObject_IsCompiledLastAndGatedOnItsOwnValidatorsRules()
    {
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

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2006");

        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("Customer", diagnostic.GetMessage());
        Assert.Contains("after every other rule", diagnostic.GetMessage());

        var emitted = result.Sources["Sample.CustomerValidator.g.cs"];

        // Last, and only when this validator's own rules found nothing blocking -
        // Validator.TryValidateObject's sequencing. The mark is taken before the first rule, so a
        // warning, or an error the parent recorded before this validator began, does not count.
        Assert.Contains("var mark = ctx.Mark();", emitted);
        Assert.Contains(
            "!ctx.HasBlockingErrorsSince(mark) && global::ValidationModules.DataAnnotationsSupport.ValidateObject(ref ctx, value",
            emitted
        );
        Assert.DoesNotContain("ctx.HasErrors", emitted);

        // The boolean fast path cannot know "the whole pass was clean", so the type falls back to
        // the interface default, the way applied rules do.
        Assert.DoesNotContain("public bool IsValid", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ValidatableObject_UnderIgnore_IsVM2006AsInfo()
    {
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

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2006");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains(
            "ValidationModules is ignoring its Validate method",
            diagnostic.GetMessage()
        );
        Assert.Contains("another validation system may still call it", diagnostic.GetMessage());
    }

    [Fact]
    public void ValidatableObject_StillEmitsAValidatorForTheConstraintsItDoesUnderstand()
    {
        // The attribute half compiles as it always did; the interface rides behind it.
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
    public void PlainModel_DoesNotReportVM2006()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                public string? Name { get; set; }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM2006");
    }

    // VM2010 — a ValidationAttribute on the class, compiled with TryValidateObject's sequencing.

    private const string AlwaysFails = """
        [AttributeUsage(AttributeTargets.Class)]
        public sealed class AlwaysFailsAttribute : ValidationAttribute {
            public override bool IsValid(object? value) => false;
        }
        """;

    private static string ClassLevel(string types) =>
        $$"""
            using System;
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            {{AlwaysFails}}

            {{types}}
            """;

    [Fact]
    public void ClassLevelValidationAttribute_RunsAfterThePropertyRulesAndReportsVM2010()
    {
        var result = GeneratorHarness.Run(
            ClassLevel(
                """
                [AlwaysFails]
                public class Booking {
                    [Required]
                    public string? Name { get; set; }
                }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2010");

        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("'AlwaysFailsAttribute' on 'Booking'", diagnostic.GetMessage());
        Assert.Contains(
            "after every property rule on the type has passed",
            diagnostic.GetMessage()
        );
        Assert.Equal(
            "AlwaysFails",
            diagnostic
                .Location.SourceTree!.GetText(TestContext.Current.CancellationToken)
                .ToString(diagnostic.Location.SourceSpan)
        );

        var emitted = result.Sources["Sample.BookingValidator.g.cs"];

        // Constructed once, like a property-level custom attribute, and called with the object as
        // the value behind the same gate IValidatableObject uses. A result naming no members
        // reports against the object, which is what the null field asks of Apply.
        Assert.Contains("ObjectAttribute0 = new global::Sample.AlwaysFailsAttribute();", emitted);
        Assert.Contains("var mark = ctx.Mark();", emitted);
        Assert.Contains("if (!ctx.HasBlockingErrorsSince(mark))", emitted);
        Assert.Contains(
            "global::ValidationModules.DataAnnotationsSupport.Apply(ref ctx, ObjectAttribute0.GetValidationResult(value, "
                + "global::ValidationModules.DataAnnotationsSupport.CreateContext(ctx.Services, value, null, value.GetType().Name)), null, ",
            emitted
        );
        Assert.True(
            emitted.IndexOf("ReportRequired", StringComparison.Ordinal)
                < emitted.IndexOf("ObjectAttribute0.GetValidationResult", StringComparison.Ordinal)
        );
        Assert.DoesNotContain("public bool IsValid", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ClassLevelValidationAttribute_OnATypeWithNoOtherRule_StillGetsAValidator()
    {
        // Validator.TryValidateObject runs it whatever else the type carries, so the attribute is a
        // rule in its own right rather than something that rides on a property's constraints.
        var result = GeneratorHarness.Run(
            ClassLevel(
                """
                [AlwaysFails]
                public class Booking {
                    public string? Name { get; set; }
                }
                """
            )
        );

        Assert.Contains("ObjectAttribute0", result.Sources["Sample.BookingValidator.g.cs"]);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ClassLevelValidationAttribute_RunsBeforeValidatableObject_BehindOneGateEach()
    {
        var result = GeneratorHarness.Run(
            ClassLevel(
                """
                [AlwaysFails]
                public class Booking : IValidatableObject {
                    [Required]
                    public string? Name { get; set; }

                    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) {
                        yield break;
                    }
                }
                """
            )
        );

        var emitted = result.Sources["Sample.BookingValidator.g.cs"];

        // One mark, two gates: IValidatableObject asks again, so a failing class-level attribute
        // holds it back exactly as it does under Validator.TryValidateObject.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(emitted, @"ctx\.Mark\(\)"));
        Assert.Equal(
            2,
            System
                .Text.RegularExpressions.Regex.Matches(emitted, @"HasBlockingErrorsSince\(mark\)")
                .Count
        );
        Assert.True(
            emitted.IndexOf("ObjectAttribute0.GetValidationResult", StringComparison.Ordinal)
                < emitted.IndexOf("DataAnnotationsSupport.ValidateObject", StringComparison.Ordinal)
        );
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ClassLevelValidationAttribute_UnderIgnore_IsVM2010AsInfoAndIsNotEmitted()
    {
        var result = GeneratorHarness.Run(
            ClassLevel(
                """
                [AlwaysFails]
                public class Booking {
                    [ValidationModules.Constraints.Required]
                    public string? Name { get; set; }
                }
                """
            ),
            ("ValidationModules_DataAnnotations", "Ignore")
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2010");

        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("ValidationModules is ignoring it", diagnostic.GetMessage());
        Assert.Contains("another validation system may still enforce it", diagnostic.GetMessage());

        var emitted = result.Sources["Sample.BookingValidator.g.cs"];

        Assert.DoesNotContain("ObjectAttribute", emitted);
        Assert.DoesNotContain("ctx.Mark()", emitted);
    }

    [Fact]
    public void ClassLevelValidationAttribute_WithAnArgumentThatCannotBeRendered_IsVM2010AsAWarning()
    {
        // A non-constant argument is CS0182 already; the generator adds what it did with the rule
        // rather than emitting a construction that cannot compile.
        var result = GeneratorHarness.Run(
            ClassLevel(
                """
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class AtMostAttribute : ValidationAttribute {
                    public AtMostAttribute(int max) { }
                    public override bool IsValid(object? value) => false;
                }

                public static class Limits {
                    public static readonly int Max = 3;
                }

                [AtMost(Limits.Max)]
                public class Booking {
                    [Required]
                    public string? Name { get; set; }
                }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2010");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("It is not enforced", diagnostic.GetMessage());
        Assert.Contains("IValidatableObject.Validate", diagnostic.GetMessage());
        Assert.DoesNotContain("ObjectAttribute", result.Sources["Sample.BookingValidator.g.cs"]);
    }

    [Fact]
    public void ClassLevelCustomValidation_ResolvesToADirectStaticCallWithTheObjectAsTheValue()
    {
        var result = GeneratorHarness.Run(
            ClassLevel(
                """
                [CustomValidation(typeof(Booking), nameof(Booking.CheckDates))]
                [CustomValidation(typeof(Booking), nameof(Booking.CheckGuests))]
                public class Booking {
                    [Required]
                    public string? Name { get; set; }

                    public static ValidationResult? CheckDates(Booking booking, ValidationContext context) =>
                        ValidationResult.Success;

                    public static ValidationResult? CheckGuests(object booking) => ValidationResult.Success;
                }
                """
            )
        );

        Assert.Equal(2, result.Diagnostics.Count(d => d.Id == "VM2010"));
        Assert.Contains(
            result.Diagnostics,
            d => d.Id == "VM2010" && d.GetMessage().Contains("calls its method directly")
        );

        var emitted = result.Sources["Sample.BookingValidator.g.cs"];

        // No attribute instance and no reflective dispatch, as on a property. Two methods on one
        // class both run: [CustomValidation] is keyed by its method, not by its attribute type.
        Assert.Contains(
            "global::Sample.Booking.CheckDates(value, global::ValidationModules.DataAnnotationsSupport.CreateContext(ctx.Services, value, null, value.GetType().Name)), null, ",
            emitted
        );
        Assert.Contains("global::Sample.Booking.CheckGuests(value), null, ", emitted);
        Assert.DoesNotContain("CustomValidationAttribute", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ClassLevelCustomValidation_WhoseMethodCannotTakeTheObject_IsVM2008()
    {
        var result = GeneratorHarness.Run(
            ClassLevel(
                """
                [CustomValidation(typeof(Booking), nameof(Booking.Check))]
                public class Booking {
                    [Required]
                    public string? Name { get; set; }

                    public static ValidationResult? Check(string value) => ValidationResult.Success;
                }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM2008");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("'CustomValidationAttribute' on 'Booking'", diagnostic.GetMessage());
        Assert.Contains("takes 'string'", diagnostic.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM2010");
    }

    [Fact]
    public void ClassLevelValidationAttribute_OnABaseType_RunsForTheDerivedTypeAndIsReportedOnce()
    {
        // TypeDescriptor.GetAttributes, which DataAnnotations reads type-level attributes through,
        // merges the base types' attributes whatever their Inherited says. The finding belongs to
        // the declaration, so the derived type does not repeat it.
        var result = GeneratorHarness.Run(
            ClassLevel(
                """
                [AttributeUsage(AttributeTargets.Class, Inherited = false)]
                public sealed class NotInheritedAttribute : ValidationAttribute {
                    public override bool IsValid(object? value) => false;
                }

                [AlwaysFails, NotInherited]
                public abstract class BookingBase {
                    [Required]
                    public string? Name { get; set; }
                }

                public sealed class Booking : BookingBase {
                    [Required]
                    public string? Code { get; set; }
                }
                """
            )
        );

        Assert.Equal(2, result.Diagnostics.Count(d => d.Id == "VM2010"));
        Assert.All(
            result.Diagnostics.Where(d => d.Id == "VM2010"),
            d => Assert.Contains("on 'BookingBase'", d.GetMessage())
        );

        var emitted = result.Sources["Sample.BookingValidator.g.cs"];

        Assert.Contains("new global::Sample.AlwaysFailsAttribute()", emitted);
        Assert.Contains("new global::Sample.NotInheritedAttribute()", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ClassLevelValidationAttribute_OnBothBaseAndDerived_RunsOnceWithTheDerivedDeclaration()
    {
        // One per attribute type, most-derived first - TypeDescriptor's dedupe, which is what
        // decides whether DataAnnotations runs the base's declaration or the derived one's.
        var result = GeneratorHarness.Run(
            ClassLevel(
                """
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class LabelledAttribute : ValidationAttribute {
                    public LabelledAttribute(string label) { }
                    public override bool IsValid(object? value) => false;
                }

                [Labelled("base")]
                public abstract class BookingBase {
                    [Required]
                    public string? Name { get; set; }
                }

                [Labelled("derived")]
                public sealed class Booking : BookingBase {
                }
                """
            )
        );

        var emitted = result.Sources["Sample.BookingValidator.g.cs"];

        Assert.Contains("new global::Sample.LabelledAttribute(\"derived\")", emitted);
        Assert.DoesNotContain("\"base\"", emitted);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ClassLevelValidationAttribute_MakesTheTypeANestingTarget()
    {
        // The nesting side asks the same question Build answers, so a descent into a type whose
        // only rule is class-level is kept rather than dropped as VM1501.
        var result = GeneratorHarness.Run(
            ClassLevel(
                """
                [AlwaysFails]
                public sealed class Stay {
                    public string? Room { get; set; }
                }

                public sealed class Trip {
                    [ValidationModules.Constraints.ValidateNested]
                    public Stay? Stay { get; set; }
                }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
        Assert.Contains(
            "global::Sample.StayValidator",
            result.Sources["Sample.TripValidator.g.cs"]
        );
        Assert.Empty(result.CompilationErrors);
    }

    // The clean case, so none of the above can pass because the front end never ran.

    [Fact]
    public void WellFormedDataAnnotationsModel_ProducesNoDiagnosticsAndCompiles()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                [StringLength(100, MinimumLength = 1)]
                public string? Name { get; set; }

                [Range(0, 120)]
                public int Age { get; set; }

                [RegularExpression("^[A-Z]{3}$")]
                public string? Sku { get; set; }

                [MaxLength(5)]
                public List<string> Tags { get; set; } = new();
                """
            )
        );

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }
}
