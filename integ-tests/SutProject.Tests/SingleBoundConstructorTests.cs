using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// One bound through the constructor: <c>[StringLength(min: 12)]</c> and
/// <c>[ItemCount(max: 2)]</c> compile and emit exactly the one comparison they name.
/// </summary>
public class SingleBoundConstructorTests {

    [Fact]
    public void MinOnly_TooShort_FailsWithTheAtLeastShape() {
        var result = new PassphraseValidator().Validate(new Passphrase { Value = "short" });

        var error = Assert.Single(result.Errors);

        Assert.Equal("value", error.Field);
        Assert.Equal(ValidationCodes.StringLength, error.Code);
        Assert.Contains("at least 12", error.Message);
    }

    [Fact]
    public void MinOnly_HasNoUpperBoundToTripOver() {
        // The omitted bound must be genuinely absent, not int.MaxValue leaking into a message.
        var result = new PassphraseValidator().Validate(new Passphrase {
            Value = new string('x', 10_000),
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void MaxOnly_TooMany_FailsWithTheAtMostShape() {
        var result = new PassphraseValidator().Validate(new Passphrase {
            Value = "a long enough passphrase",
            Hints = ["one", "two", "three"],
        });

        var error = Assert.Single(result.Errors);

        Assert.Equal("hints", error.Field);
        Assert.Contains("at most 2", error.Message);
    }
}
