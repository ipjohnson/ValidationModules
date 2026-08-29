using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins the composed message text. These are the strings that would otherwise have been literals in
/// generated code, so a change here changes every consumer's error output at once - which is the
/// point of them living in one place, and the reason they need pinning.
/// </summary>
public class ValidationContextExtensionsTests {

    [Fact]
    public void AddRequired_ComposesTheStandardMessage() {
        Assert.Equal("name is required.", Single(context => context.ReportRequired("name")).Message);
    }

    [Fact]
    public void AddRequired_UsesTheSharedCode() {
        Assert.Equal(ValidationCodes.Required, Single(context => context.ReportRequired("name")).Code);
    }

    [Theory]
    [InlineData(0, 100, "name must be at most 100 characters.")]
    [InlineData(1, 100, "name must be between 1 and 100 characters.")]
    [InlineData(0, 1, "name must be at most 1 character.")]
    [InlineData(2, int.MaxValue, "name must be at least 2 characters.")]
    [InlineData(1, int.MaxValue, "name must be at least 1 character.")]
    public void AddStringLength_ComposesByWhichBoundsAreSet(int min, int max, string expected) {
        Assert.Equal(expected, Single(context => context.ReportStringLength("name", min, max)).Message);
    }

    [Theory]
    [InlineData(1, int.MaxValue, "toys must be at least 1 item.")]
    [InlineData(0, 3, "toys must be at most 3 items.")]
    [InlineData(1, 3, "toys must be between 1 and 3 items.")]
    public void AddItemCount_ComposesByWhichBoundsAreSet(int min, int max, string expected) {
        Assert.Equal(expected, Single(context => context.ReportItemCount("toys", min, max)).Message);
    }

    [Fact]
    public void AddRange_FormatsBothBounds() {
        Assert.Equal("age must be between 0 and 30.", Single(context => context.ReportRange("age", 0, 30)).Message);
    }

    [Fact]
    public void AddRange_UsesInvariantCulture() {
        // A message is a wire format, not prose. Under a comma-decimal culture this would otherwise
        // read "0,5", which a client parsing the message cannot handle and a test on the developer's
        // machine would never catch.
        var original = Thread.CurrentThread.CurrentCulture;
        try {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            Assert.Equal(
                "ratio must be between 0.5 and 1.5.",
                Single(context => context.ReportRange("ratio", 0.5, 1.5)).Message);
        } finally {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    /// <summary>
    /// The reason the one-sided helpers exist. Composing an absent bound as the type's extreme put
    /// "must be between 1 and 7.9228162514264338E+28" in a 400 body for a specification that set
    /// only <c>minimum</c>.
    /// </summary>
    [Fact]
    public void AddRangeAtLeast_NamesOnlyTheBoundThatWasDeclared() {
        Assert.Equal("age must be at least 1.", Single(context => context.ReportRangeAtLeast("age", 1)).Message);
    }

    [Fact]
    public void AddRangeAtMost_NamesOnlyTheBoundThatWasDeclared() {
        Assert.Equal("age must be at most 99.", Single(context => context.ReportRangeAtMost("age", 99)).Message);
    }

    [Fact]
    public void AddRangeAtLeastAndAtMost_ShareTheRangeCode() {
        // One code for the shape, because the failure is the same one a client already handles.
        Assert.Equal(ValidationCodes.Range, Single(context => context.ReportRangeAtLeast("age", 1)).Code);
        Assert.Equal(ValidationCodes.Range, Single(context => context.ReportRangeAtMost("age", 99)).Code);
    }

    [Theory]
    [InlineData(5, "quantity must be a multiple of 5.")]
    [InlineData(0.05, "quantity must be a multiple of 0.05.")]
    public void AddMultipleOf_NamesTheDivisor(double divisor, string expected) {
        Assert.Equal(expected, Single(context => context.ReportMultipleOf("quantity", (decimal)divisor)).Message);
    }

    [Fact]
    public void AddMultipleOf_UsesItsOwnCode() {
        Assert.Equal(ValidationCodes.MultipleOf, Single(context => context.ReportMultipleOf("quantity", 5m)).Code);
    }

    [Fact]
    public void AddUniqueItems_DoesNotEchoTheDuplicate() {
        var error = Single(context => context.ReportUniqueItems("tags"));

        Assert.Equal("tags must not contain duplicate items.", error.Message);
        Assert.Equal(ValidationCodes.UniqueItems, error.Code);
    }

    [Fact]
    public void AddPattern_DoesNotEchoThePattern() {
        var error = Single(context => context.ReportPattern("sku"));

        Assert.Equal("sku is not in the required format.", error.Message);
    }

    [Fact]
    public void AddAllowedValues_ListsThePermittedSet() {
        Assert.Equal(
            "status must be one of: available, pending, sold.",
            Single(context => context.ReportAllowedValues("status", "available, pending, sold")).Message);
    }

    [Fact]
    public void Extensions_ChainOffPush() {
        // The reason the receiver is by value rather than `in` or `ref`: the result of Push is not
        // addressable, so a by-reference receiver would not compile here.
        var collector = new ValidationErrorCollector();

        new ValidationContext(collector).Push("home").ReportRequired("postalCode");

        Assert.Equal("home.postalCode", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Extensions_ChainOffPushIndex() {
        var collector = new ValidationErrorCollector();

        new ValidationContext(collector).PushIndex("toys", 3).ReportStringLength("name", max: 10);

        var error = Assert.Single(collector.ToResult().Errors);
        Assert.Equal("toys[3].name", error.Field);
        Assert.Equal("name must be at most 10 characters.", error.Message);
    }

    [Fact]
    public void Extensions_AcceptASeverity() {
        var error = Single(context => context.ReportRequired("name", ValidationSeverity.Warning));

        Assert.Equal(ValidationSeverity.Warning, error.Severity);
    }

    [Fact]
    public void Extensions_UsableFromAHandWrittenValidator() {
        // Hand-written validators get the same helpers as generated ones, so their messages match
        // rather than being a second wording of the same rule.
        var result = ExtensionUsingValidator.Instance.Validate(new Pet());

        Assert.Equal(
            ["name is required.", "toys must be at least 1 item."],
            result.Errors.Select(error => error.Message));
    }

    // The format family: message and code per helper, the same two pins the rest of the file makes.

    [Fact]
    public void ReportEmail_ComposesTheStandardMessageAndCode() {
        var error = Single(context => context.ReportEmail("email"));

        Assert.Equal("email is not a valid email address.", error.Message);
        Assert.Equal(ValidationCodes.Email, error.Code);
    }

    [Fact]
    public void ReportPhone_ComposesTheStandardMessageAndCode() {
        var error = Single(context => context.ReportPhone("mobile"));

        Assert.Equal("mobile is not a valid phone number.", error.Message);
        Assert.Equal(ValidationCodes.Phone, error.Code);
    }

    [Fact]
    public void ReportUrl_NamesTheAcceptedSchemes() {
        var error = Single(context => context.ReportUrl("homepage"));

        Assert.Equal("homepage is not a valid http, https or ftp URL.", error.Message);
        Assert.Equal(ValidationCodes.Url, error.Code);
    }

    [Fact]
    public void ReportCreditCard_ComposesTheStandardMessageAndCode() {
        var error = Single(context => context.ReportCreditCard("card"));

        Assert.Equal("card is not a valid credit card number.", error.Message);
        Assert.Equal(ValidationCodes.CreditCard, error.Code);
    }

    [Fact]
    public void ReportBase64_ComposesTheStandardMessageAndCode() {
        var error = Single(context => context.ReportBase64("payload"));

        Assert.Equal("payload is not a valid Base64 string.", error.Message);
        Assert.Equal(ValidationCodes.Base64, error.Code);
    }

    [Fact]
    public void ReportFileExtension_EmbedsTheJoinedSet() {
        var error = Single(context => context.ReportFileExtension("avatar", ".png, .jpg"));

        Assert.Equal("avatar must have one of these file extensions: .png, .jpg.", error.Message);
        Assert.Equal(ValidationCodes.FileExtension, error.Code);
    }

    [Fact]
    public void ReportEmail_TakesACodeOverrideWithoutLosingTheComposedMessage() {
        var error = Single(context => context.ReportEmail("email", code: "contact_email"));

        Assert.Equal("contact_email", error.Code);
        Assert.Equal("email is not a valid email address.", error.Message);
    }

    private static ValidationError Single(Action<ValidationContext> act) {
        var collector = new ValidationErrorCollector();

        act(new ValidationContext(collector));

        return Assert.Single(collector.ToResult().Errors);
    }

    private sealed class ExtensionUsingValidator : IValidatorFor<Pet> {
        public static readonly ExtensionUsingValidator Instance = new();

        private ExtensionUsingValidator() { }

        public ValidationFlow Validate(ref ValidationContext context, Pet value) {
            if (string.IsNullOrWhiteSpace(value.Name)) {
                if (context.ReportRequired("name").ShouldStop) {
                    return ValidationFlow.Stop;
                }
            } else if (value.Name.Length > 10) {
                if (context.ReportStringLength("name", max: 10).ShouldStop) {
                    return ValidationFlow.Stop;
                }
            }

            if (value.Toys.Count < 1 && context.ReportItemCount("toys", min: 1).ShouldStop) {
                return ValidationFlow.Stop;
            }

            return ValidationFlow.Continue;
        }
    }
}
