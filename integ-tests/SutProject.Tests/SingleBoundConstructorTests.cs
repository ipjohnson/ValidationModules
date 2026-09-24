using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// One bound at a time: <c>[StringLength(20)]</c> through the constructor,
/// <c>[StringLength(Min = 12)]</c> by name, and <c>[ItemCount(max: 2)]</c>. Each emits exactly the
/// one comparison it names.
/// </summary>
public class SingleBoundConstructorTests
{
    [Fact]
    public void MinOnly_TooShort_FailsWithTheAtLeastShape()
    {
        var result = new PassphraseValidator().Validate(new Passphrase { Value = "short" });

        var error = Assert.Single(result.Errors);

        Assert.Equal("value", error.Field);
        Assert.Equal(ValidationCodes.StringLength, error.Code);
        Assert.Contains("at least 12", error.Message);
    }

    [Fact]
    public void MinOnly_HasNoUpperBoundToTripOver()
    {
        // The omitted bound must be genuinely absent, not int.MaxValue leaking into a message.
        var result = new PassphraseValidator().Validate(
            new Passphrase { Value = new string('x', 10_000) }
        );

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PositionalStringLength_IsAMaximum_SoAShortValuePasses()
    {
        // [StringLength(20)] read as a minimum rejected this, where DataAnnotations accepts it.
        var result = new PassphraseValidator().Validate(new Passphrase { Label = "work" });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PositionalStringLength_TooLong_FailsWithTheAtMostShape()
    {
        var result = new PassphraseValidator().Validate(
            new Passphrase { Label = new string('x', 21) }
        );

        var error = Assert.Single(result.Errors);

        Assert.Equal("label", error.Field);
        Assert.Equal(ValidationCodes.StringLength, error.Code);
        Assert.Contains("at most 20", error.Message);
    }

    [Fact]
    public void MaxOnly_TooMany_FailsWithTheAtMostShape()
    {
        var result = new PassphraseValidator().Validate(
            new Passphrase { Value = "a long enough passphrase", Hints = ["one", "two", "three"] }
        );

        var error = Assert.Single(result.Errors);

        Assert.Equal("hints", error.Field);
        Assert.Contains("at most 2", error.Message);
    }
}
