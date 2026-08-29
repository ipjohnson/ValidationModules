using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Private constants referenced from a transcribed body, compiled and run.
/// </summary>
/// <remarks>
/// A private member cannot be reached from the companion class a body is transcribed into, so a
/// constant is carried across by value. That is safe because C# bakes a constant into every use
/// site anyway — but only if the literal we write reads back as the same value <i>and</i> the same
/// type, which is what these check where a golden file could not.
/// </remarks>
public class LiftedConstantTests {

    private static readonly IValidatorFor<Quote> Validator = new QuoteValidator();

    private static Quote Valid() => new() { Amount = 1000.50m, Ratio = 1.2345678901234567, Tier = QuoteTier.Standard };

    /// <summary>
    /// Exactly on the bound. A decimal rendered without its suffix would not have compiled; one
    /// rendered through a double would land a hair off and fail here.
    /// </summary>
    [Fact]
    public void ADecimalConstantKeepsItsValueToTheLastPlace() {
        Assert.True(Validator.Validate(Valid()).IsValid);

        Assert.Equal(
            "ceiling",
            Assert.Single(Validator.Validate(Valid() with { Amount = 1000.51m }).Errors).Code);
    }

    /// <summary>
    /// The digits a lossy round-trip would drop are the ones that decide this case.
    /// </summary>
    [Fact]
    public void ADoubleConstantKeepsEveryDigit() {
        Assert.True(Validator.Validate(Valid()).IsValid);

        Assert.Equal(
            "ratio",
            Assert.Single(Validator.Validate(Valid() with { Ratio = 1.2345678901234569 }).Errors).Code);
    }

    [Fact]
    public void AnEnumConstantStillNamesItsMember() {
        // Restricted is Tier.Premium, so the guarded rule only runs for a premium quote.
        Assert.True(Validator.Validate(Valid() with { Amount = 0m, Tier = QuoteTier.Standard }).IsValid);

        Assert.Equal(
            "positive",
            Assert.Single(Validator.Validate(Valid() with { Amount = 0m, Tier = QuoteTier.Premium }).Errors).Code);
    }
}
