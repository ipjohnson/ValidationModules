using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// <c>IConstraintFor&lt;T&gt;</c> attributes compiled by the real generator and run against the
/// real runtime: the attribute's own reporting lands on the wire, one instance serves every pass,
/// <c>[PerValidationInstance]</c> constructs per check, and the constraint base's knobs are
/// honoured by the interface's default <c>Validate</c>.
/// </summary>
public class ConstraintInterfaceTests {

    private static Bulletin Valid() => new() { Channel = "email", Sequence = 1, Batch = 2 };

    [Fact]
    public void Validate_CleanValue_IsValid() {
        Assert.True(new BulletinValidator().IsValid(Valid()));
    }

    [Fact]
    public void Validate_FailingValue_ReportsTheAttributesOwnError() {
        var collector = new ValidationErrorCollector();

        new BulletinValidator().ValidateInto(collector, Valid() with { Channel = "fax" });

        var error = Assert.Single(collector.ToResult().Errors);

        Assert.Equal("channel", error.Field);
        Assert.Equal("channel", error.Code);
        Assert.Equal("channel must be one of: email, sms.", error.Message);
    }

    [Fact]
    public void Validate_NullMember_SkipsTheCheckAndFailsOnlyRequired() {
        var collector = new ValidationErrorCollector();

        new BulletinValidator().ValidateInto(collector, Valid() with { Channel = null });

        var error = Assert.Single(collector.ToResult().Errors);

        Assert.Equal(ValidationCodes.Required, error.Code);
    }

    [Fact]
    public void Validate_DefaultValidate_HonoursTheDeclarationsKnobs() {
        var collector = new ValidationErrorCollector();

        new BulletinValidator().ValidateInto(collector, Valid() with { Batch = 3 });

        var error = Assert.Single(collector.ToResult().Errors);

        Assert.Equal("batch", error.Field);
        Assert.Equal("pair", error.Code);
        Assert.Equal("batch must come in pairs", error.Message);
    }

    [Fact]
    public void SharedInstance_IsConstructedOnceForAnyNumberOfPasses() {
        var validator = new BulletinValidator();
        var value = Valid();

        // First pass triggers the static initializer that builds the shared instance.
        Assert.True(validator.IsValid(value));

        var constructed = ChannelAttribute.Constructions;

        for (var i = 0; i < 25; i++) {
            validator.IsValid(value);
        }

        Assert.Equal(constructed, ChannelAttribute.Constructions);
        Assert.Equal(1, constructed);
    }

    [Fact]
    public void PerValidationInstance_ConstructsAFreshInstanceAtEveryCheck() {
        var validator = new BulletinValidator();
        var value = Valid();
        var constructed = StampedAttribute.Constructions;

        validator.IsValid(value);
        validator.IsValid(value);
        validator.IsValid(value);

        Assert.Equal(constructed + 3, StampedAttribute.Constructions);
    }
}
