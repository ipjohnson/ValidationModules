using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Private constants referenced from a lifted predicate, compiled and run on both engines.
/// </summary>
/// <remarks>
/// A private member cannot be reached from the class a predicate is lifted into, so a constant is
/// carried across by value. That is safe because C# bakes a constant into every use site anyway —
/// but only if the literal we write reads back as the same value <i>and</i> the same type, which is
/// what these check where a golden file could not.
/// </remarks>
public class LiftedConstantTests {

    private static readonly IValidatorFor<Quote> Generated = new QuoteValidator();
    private static readonly IValidatorFor<Quote> Described = new DescribedValidator<Quote>(new QuoteRules());

    public static TheoryData<IValidatorFor<Quote>> BothEngines => new() { Generated, Described };

    private static Quote Valid() => new() { Amount = 1000.50m, Ratio = 1.2345678901234567, Tier = QuoteTier.Standard };

    /// <summary>
    /// Exactly on the bound. A decimal rendered without its suffix would not have compiled; one
    /// rendered through a double would land a hair off and fail here.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothEngines))]
    public void ADecimalConstantKeepsItsValueToTheLastPlace(IValidatorFor<Quote> validator) {
        Assert.True(validator.Validate(Valid()).IsValid, validator.GetType().Name);

        Assert.Equal(
            "ceiling",
            Assert.Single(validator.Validate(Valid() with { Amount = 1000.51m }).Errors).Code);
    }

    /// <summary>
    /// The digits a lossy round-trip would drop are the ones that decide this case.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothEngines))]
    public void ADoubleConstantKeepsEveryDigit(IValidatorFor<Quote> validator) {
        Assert.True(validator.Validate(Valid()).IsValid, validator.GetType().Name);

        Assert.Equal(
            "ratio",
            Assert.Single(validator.Validate(Valid() with { Ratio = 1.2345678901234569 }).Errors).Code);
    }

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void AnEnumConstantStillNamesItsMember(IValidatorFor<Quote> validator) {
        // Restricted is Tier.Premium, so the guarded rule only runs for a premium quote.
        Assert.True(validator.Validate(Valid() with { Amount = 0m, Tier = QuoteTier.Standard }).IsValid);

        Assert.Equal(
            "positive",
            Assert.Single(validator.Validate(Valid() with { Amount = 0m, Tier = QuoteTier.Premium }).Errors).Code);
    }

    /// <summary>
    /// The two engines see the same constants — trivially, because C# baked them at both use sites,
    /// which is the property that makes carrying a value across sound in the first place.
    /// </summary>
    [Fact]
    public void BothEnginesAgree() {
        foreach (var quote in new[] {
            Valid(),
            Valid() with { Amount = 1000.51m },
            Valid() with { Ratio = 1.2345678901234569 },
            Valid() with { Amount = 0m, Tier = QuoteTier.Premium },
        }) {
            Assert.Equal(
                Generated.Validate(quote).Errors.Select(error => error.Code),
                Described.Validate(quote).Errors.Select(error => error.Code));
        }
    }
}
