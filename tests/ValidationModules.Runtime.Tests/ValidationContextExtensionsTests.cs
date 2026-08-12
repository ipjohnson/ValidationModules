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
        Assert.Equal("name is required.", Single(context => context.AddRequired("name")).Message);
    }

    [Fact]
    public void AddRequired_UsesTheSharedCode() {
        Assert.Equal(ValidationCodes.Required, Single(context => context.AddRequired("name")).Code);
    }

    [Theory]
    [InlineData(0, 100, "name must be at most 100 characters.")]
    [InlineData(1, 100, "name must be between 1 and 100 characters.")]
    [InlineData(0, 1, "name must be at most 1 character.")]
    [InlineData(2, int.MaxValue, "name must be at least 2 characters.")]
    [InlineData(1, int.MaxValue, "name must be at least 1 character.")]
    public void AddStringLength_ComposesByWhichBoundsAreSet(int min, int max, string expected) {
        Assert.Equal(expected, Single(context => context.AddStringLength("name", min, max)).Message);
    }

    [Theory]
    [InlineData(1, int.MaxValue, "toys must be at least 1 item.")]
    [InlineData(0, 3, "toys must be at most 3 items.")]
    [InlineData(1, 3, "toys must be between 1 and 3 items.")]
    public void AddItemCount_ComposesByWhichBoundsAreSet(int min, int max, string expected) {
        Assert.Equal(expected, Single(context => context.AddItemCount("toys", min, max)).Message);
    }

    [Fact]
    public void AddRange_FormatsBothBounds() {
        Assert.Equal("age must be between 0 and 30.", Single(context => context.AddRange("age", 0, 30)).Message);
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
                Single(context => context.AddRange("ratio", 0.5, 1.5)).Message);
        } finally {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void AddPattern_DoesNotEchoThePattern() {
        var error = Single(context => context.AddPattern("sku"));

        Assert.Equal("sku is not in the required format.", error.Message);
    }

    [Fact]
    public void AddAllowedValues_ListsThePermittedSet() {
        Assert.Equal(
            "status must be one of: available, pending, sold.",
            Single(context => context.AddAllowedValues("status", "available, pending, sold")).Message);
    }

    [Fact]
    public void Extensions_ChainOffPush() {
        // The reason the receiver is by value rather than `in` or `ref`: the result of Push is not
        // addressable, so a by-reference receiver would not compile here.
        var collector = new ValidationErrorCollector();

        new ValidationContext(collector).Push("home").AddRequired("postalCode");

        Assert.Equal("home.postalCode", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Extensions_ChainOffPushIndex() {
        var collector = new ValidationErrorCollector();

        new ValidationContext(collector).PushIndex("toys", 3).AddStringLength("name", max: 10);

        var error = Assert.Single(collector.ToResult().Errors);
        Assert.Equal("toys[3].name", error.Field);
        Assert.Equal("name must be at most 10 characters.", error.Message);
    }

    [Fact]
    public void Extensions_AcceptASeverity() {
        var error = Single(context => context.AddRequired("name", ValidationSeverity.Warning));

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

    private static ValidationError Single(Action<ValidationContext> act) {
        var collector = new ValidationErrorCollector();

        act(new ValidationContext(collector));

        return Assert.Single(collector.ToResult().Errors);
    }

    private sealed class ExtensionUsingValidator : IValidatorFor<Pet> {
        public static readonly ExtensionUsingValidator Instance = new();

        private ExtensionUsingValidator() { }

        public void Validate(ref ValidationContext context, Pet value) {
            if (string.IsNullOrWhiteSpace(value.Name)) {
                context.AddRequired("name");
            } else if (value.Name.Length > 10) {
                context.AddStringLength("name", max: 10);
            }

            if (value.Toys.Count < 1) {
                context.AddItemCount("toys", min: 1);
            }
        }
    }
}
