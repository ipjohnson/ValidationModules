using Microsoft.Extensions.DependencyInjection;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Two faults where the generator emitted C# that did not compile, and said nothing about it.
/// </summary>
/// <remarks>
/// These are integration tests rather than golden-file tests on purpose. The emitted text was never
/// the problem - it was that the text did not build, which only a real compilation can catch. The
/// fact that this file compiles at all is half of each assertion; the other half is that the
/// comparison it compiled to is the right one.
/// </remarks>
public class EnumAndAccessibilityTests {

    [Theory]
    [InlineData(Tier.Pro, true)]
    [InlineData(Tier.Enterprise, true)]
    [InlineData(Tier.Free, false)]
    public void AllowedValues_OverEnumMembers_ComparesAgainstTheMembers(Tier plan, bool expected) {
        // Rendered from TypedConstant.Value this was `value.Plan != 1`, which is CS0019 against an
        // enum. Rendered from the member it is a comparison, and one that means what it says.
        var result = new AccountValidator().Validate(new Account { Plan = plan, Unnamed = Tier.Pro });

        Assert.Equal(expected, result.IsValid);
    }

    [Fact]
    public void AllowedValues_OverEnumMembers_NamesTheMembersInTheMessage() {
        // The other half of the same bug: even compiling, "must be one of: 1, 2" tells a caller
        // nothing they can act on.
        var result = new AccountValidator().Validate(new Account { Plan = Tier.Free, Unnamed = Tier.Pro });

        var error = Assert.Single(result.Errors);

        Assert.Equal(ValidationCodes.Enum, error.Code);
        Assert.Equal("plan must be one of: Pro, Enterprise.", error.Message);
    }

    [Fact]
    public void AllowedValues_WithAValueThatHasNoMember_SurvivesAsACast() {
        // (Tier)7 is legal C# and a legal attribute argument. It has no name to emit, so the
        // comparison casts and the message shows the number a caller would have sent.
        Assert.True(new AccountValidator().Validate(new Account { Plan = Tier.Pro, Unnamed = (Tier)7 }).IsValid);

        var error = Assert.Single(
            new AccountValidator().Validate(new Account { Plan = Tier.Pro, Unnamed = Tier.Free }).Errors);

        Assert.Equal("unnamed must be one of: Pro, 7.", error.Message);
    }

    [Fact]
    public void InternalType_GetsAnInternalValidator() {
        // A public validator over an internal type is CS0051. This test compiling is the assertion;
        // the rest is checking it validates rather than merely binds.
        var result = new InternalReadingValidator().Validate(new InternalReading { Label = null, Level = 99 });

        Assert.Equal(["label", "level"], result.Errors.Select(error => error.Field));
    }

    [Fact]
    public void PublicTypeNestedInAnInternalOne_GetsAnInternalValidator() {
        // Effective accessibility is the minimum along the containing chain, so this is internal
        // despite the declaration saying public.
        var result = new NestedValidator().Validate(new Enclosing.Nested { Name = null });

        Assert.Equal("name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void InternalValidators_AreStillRegistered() {
        // The registration extension is public and its body names internal types. That is legal -
        // a generic argument in an invocation is not part of the method's signature - and worth
        // pinning, because making the emitted class internal could plausibly have broken it.
        var services = new ServiceCollection();

        services.AddSutProjectValidators();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IValidatorFor<InternalReading>>());
    }
}
