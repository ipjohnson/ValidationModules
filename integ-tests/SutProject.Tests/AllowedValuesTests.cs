using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// The allowed-values constraint from all three places it can be written, run rather than
/// inspected.
/// </summary>
public class AllowedValuesTests
{
    [Theory]
    [InlineData("active", true)]
    [InlineData("ACTIVE", true)]
    [InlineData("Pending", true)]
    [InlineData("closed", false)]
    public void Comparison_IsApplied(string status, bool expected)
    {
        var result = new SubscriptionValidator().Validate(new Subscription { Status = status });

        Assert.Equal(expected, result.IsValid);
    }

    [Fact]
    public void DataAnnotationsEnumValues_AreNamedInTheMessages()
    {
        var result = new PlanChangeValidator().Validate(
            new PlanChange { Plan = Tier.Free, Downgrade = Tier.Free }
        );

        Assert.Collection(
            result.Errors,
            error => Assert.Equal("plan must be one of: Pro, Enterprise.", error.Message),
            error => Assert.Equal("downgrade must not be one of: Free.", error.Message)
        );
    }

    [Fact]
    public void RulesClass_ChecksEveryValueItWasGiven()
    {
        IValidatorFor<Consignment> validator = new ConsignmentValidator();

        Assert.True(validator.Validate(new Consignment()).IsValid);
        Assert.True(validator.Validate(new Consignment { Kind = "pallet", Mode = "rail" }).IsValid);

        var result = validator.Validate(
            new Consignment
            {
                Kind = "z",
                Mode = "sea",
                Tier = Tier.Free,
            }
        );

        Assert.Collection(
            result.Errors,
            error => Assert.Equal("kind must be one of: parcel, pallet.", error.Message),
            error => Assert.Equal("mode must be one of: road, rail.", error.Message),
            error => Assert.Equal("tier must be one of: Pro, Enterprise.", error.Message)
        );
    }
}
