using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// The native format attributes running for real, against the semantics
/// <c>ConstraintChecks</c> pins: the generator tests prove the emitted text matches the
/// DataAnnotations spelling, and this proves the compiled validator gives the BCL's answers.
/// </summary>
public class NativeFormatConstraintTests {

    private static ValidationResult Validate(Registration value) {
        var collector = new ValidationErrorCollector();
        new RegistrationValidator().ValidateInto(collector, value);
        return collector.ToResult();
    }

    [Fact]
    public void CleanValue_IsValid() {
        var result = Validate(new Registration {
            Email = "a@b",
            Contact = "+1 (555) 123-4567 ext. 89",
            Homepage = "https://example.test",
            Docs = new Uri("https://example.test/docs"),
            CardNumber = "4012-8888-8888-1881",
            Signature = "aGVsbG8=",
            Attachment = "report.PDF",
            Username = "ordinary",
        });

        Assert.True(result.IsValid);
    }

    /// <summary>Every property is nullable and unconstrained by [Required]: null passes, as the
    /// BCL attributes read it - presence is [Required]'s question.</summary>
    [Fact]
    public void AllNull_IsValid() {
        Assert.True(Validate(new Registration()).IsValid);
    }

    [Theory]
    [InlineData("no-at-sign", "email")]
    [InlineData("two@@signs", "email")]
    public void Email_FailsWithItsCode(string value, string code) {
        var result = Validate(new Registration { Email = value });

        var error = Assert.Single(result.Errors);
        Assert.Equal("email", error.Field);
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void EachFormat_FailsUnderItsOwnCode() {
        var result = Validate(new Registration {
            Contact = "letters",
            Homepage = "gopher://old",
            Docs = new Uri("relative", UriKind.Relative),
            CardNumber = "4012-8888-8888-1882",
            Signature = "not base64!",
            Attachment = "report.exe",
            Username = "admin",
        });

        Assert.False(result.IsValid);
        Assert.Equal("phone", Assert.Single(result.Errors, e => e.Field == "contact").Code);
        Assert.Equal("url", Assert.Single(result.Errors, e => e.Field == "homepage").Code);
        Assert.Equal("url", Assert.Single(result.Errors, e => e.Field == "docs").Code);
        Assert.Equal("credit_card", Assert.Single(result.Errors, e => e.Field == "cardNumber").Code);
        Assert.Equal("base64", Assert.Single(result.Errors, e => e.Field == "signature").Code);
        Assert.Equal("file_extension", Assert.Single(result.Errors, e => e.Field == "attachment").Code);

        // [DeniedValues] shares [AllowedValues]' code, negation and all.
        var denied = Assert.Single(result.Errors, e => e.Field == "username");
        Assert.Equal("enum", denied.Code);
        Assert.Contains("admin", denied.Message);
    }
}
