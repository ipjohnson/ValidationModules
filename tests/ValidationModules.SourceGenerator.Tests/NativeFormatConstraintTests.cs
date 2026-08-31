using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The seven attributes that completed DataAnnotations parity: <c>[EmailAddress]</c>,
/// <c>[Phone]</c>, <c>[Url]</c>, <c>[CreditCard]</c>, <c>[Base64String]</c>,
/// <c>[FileExtensions]</c> and <c>[DeniedValues]</c>, under the BCL's exact names.
/// </summary>
/// <remarks>
/// The rc1013 trial counted 108 CS0104 ambiguity errors, every one downstream of a model file
/// importing <c>System.ComponentModel.DataAnnotations</c> to reach an attribute the native
/// vocabulary did not have. These tests pin the fix from both ends: a model file needs only
/// <c>ValidationModules.Constraints</c>, and whichever namespace an attribute came from, the
/// emitted validator is the same file - asserted as equality rather than as two snapshots, so the
/// two paths cannot drift apart quietly.
/// </remarks>
public class NativeFormatConstraintTests {

    /// <summary>
    /// The brief's shape: one model file, one using, all seven attributes. No CS0104 is possible
    /// because the second namespace is never imported, and every code reaches the wire.
    /// </summary>
    [Fact]
    public void AllSeven_CompileFromTheSingleUsing_AndEmitTheirCodes() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Signup {
                [EmailAddress] public string? Email { get; init; }
                [Phone] public string? Contact { get; init; }
                [Url] public string? Homepage { get; init; }
                [CreditCard] public string? CardNumber { get; init; }
                [Base64String] public string? Signature { get; init; }
                [FileExtensions(Extensions = "pdf,docx")] public string? Attachment { get; init; }
                [DeniedValues("admin", "root")] public string? Username { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity > DiagnosticSeverity.Info);

        var emitted = result.Sources["Sample.SignupValidator.g.cs"];

        Assert.Contains("global::ValidationModules.ConstraintChecks.IsEmail", emitted);
        Assert.Contains("global::ValidationModules.ConstraintChecks.IsPhone", emitted);
        Assert.Contains("global::ValidationModules.ConstraintChecks.IsUrl", emitted);
        Assert.Contains("global::ValidationModules.ConstraintChecks.IsCreditCard", emitted);
        Assert.Contains("global::ValidationModules.ConstraintChecks.IsBase64", emitted);
        Assert.Contains("global::ValidationModules.ConstraintChecks.HasFileExtension", emitted);

        // [DeniedValues] reuses the membership machinery negated: same check, same enum code.
        Assert.Contains("== \"admin\"", emitted);
        Assert.Contains("global::ValidationModules.ValidationMessageTemplates.DeniedValues", emitted);
    }

    /// <summary>
    /// The parity contract itself: for each of the seven, the native spelling and the
    /// DataAnnotations spelling reach the identical emitted validator.
    /// </summary>
    [Theory]
    [InlineData("[EmailAddress]")]
    [InlineData("[Phone]")]
    [InlineData("[Url]")]
    [InlineData("[CreditCard]")]
    [InlineData("[Base64String]")]
    [InlineData("[FileExtensions]")]
    [InlineData("""[FileExtensions(Extensions = "pdf, .tar.gz")]""")]
    [InlineData("""[DeniedValues("admin", "root", "system")]""")]
    public void EachOfTheSeven_EmitsIdenticallyFromEitherNamespace(string attribute) {
        var native = GeneratorHarness.Run(Model("ValidationModules.Constraints", attribute));
        var bridged = GeneratorHarness.Run(Model("System.ComponentModel.DataAnnotations", attribute));

        Assert.Empty(native.CompilationErrors);
        Assert.Empty(bridged.CompilationErrors);
        Assert.Equal(
            bridged.Sources["Sample.DocumentValidator.g.cs"],
            native.Sources["Sample.DocumentValidator.g.cs"]);
    }

    /// <summary>
    /// <c>[Url]</c> is the one format check with a second member type, and the overload choice
    /// must not depend on which namespace the attribute came from.
    /// </summary>
    [Fact]
    public void Url_OnAUriMember_EmitsIdenticallyFromEitherNamespace() {
        const string model = """
            using {0};

            namespace Sample;

            public record Document {{
                [Url] public System.Uri? Homepage {{ get; init; }}
            }}
            """;

        var native = GeneratorHarness.Run(string.Format(model, "ValidationModules.Constraints"));
        var bridged = GeneratorHarness.Run(string.Format(model, "System.ComponentModel.DataAnnotations"));

        Assert.Empty(native.CompilationErrors);
        Assert.Contains(
            "global::ValidationModules.ConstraintChecks.IsUrl",
            native.Sources["Sample.DocumentValidator.g.cs"]);
        Assert.Equal(
            bridged.Sources["Sample.DocumentValidator.g.cs"],
            native.Sources["Sample.DocumentValidator.g.cs"]);
    }

    /// <summary>
    /// The applicability rule travels with the kind, not the namespace: a native format attribute
    /// on a non-string member is the same VM1001 the bridged one reports.
    /// </summary>
    [Fact]
    public void NativeFormatAttribute_OnANonString_IsVM1001() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [EmailAddress] public int Age { get; init; }
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1001");
        Assert.Contains("[EmailAddress]", diagnostic.GetMessage());
    }

    /// <summary>
    /// The base overrides ride along: the format validators derive from
    /// <c>ValidationConstraintAttribute</c> like every native constraint, so <c>Code</c>,
    /// <c>Message</c> and <c>When</c> need no special-casing.
    /// </summary>
    [Fact]
    public void NativeFormatAttribute_CarriesTheBaseOverrides() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Signup {
                [EmailAddress(Code = "work_email", Message = "{field} must be a work address.")]
                public string? Email { get; init; }
            }
            """);

        var emitted = result.Sources["Sample.SignupValidator.g.cs"];

        Assert.Contains("\"work_email\"", emitted);
        Assert.Contains("email must be a work address.", emitted);
    }

    private static string Model(string ns, string attribute) => $$"""
        using {{ns}};

        namespace Sample;

        public record Document {
            {{attribute}}
            public string? Field { get; init; }
        }
        """;
}
