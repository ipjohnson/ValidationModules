using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// The two engines of API-SURFACE.md §19, run against the same declaration.
/// </summary>
/// <remarks>
/// <para>
/// <c>ReservationRules</c> is compiled by the generator into <c>ReservationValidator</c> and is also
/// handed to <c>DescribedValidator&lt;Reservation&gt;</c>, which runs it. §19.9 claims the two are
/// substitutable on codes, messages, ordering, suppression and paths; <see cref="BothEngines"/> is
/// what makes that a fact rather than a claim, and it is the reason the parity assertions here
/// compare full sequences rather than sets.
/// </para>
/// <para>
/// This is the integration project, so the generated arm is the real emitted code compiled against
/// the real runtime, not a golden file.
/// </para>
/// </remarks>
public class RulesClassTests {

    private static readonly IValidatorFor<Reservation> Generated = new ReservationValidator();
    private static readonly IValidatorFor<Reservation> Described = new DescribedValidator<Reservation>(new ReservationRules());

    /// <summary>
    /// Both arms of §19, as theory data. xUnit names each case by the validator's own type, so a
    /// failure says which engine produced it without a label parameter.
    /// </summary>
    public static TheoryData<IValidatorFor<Reservation>> BothEngines => new() { Generated, Described };

    private static Reservation Valid() => new() {
        Guest = "Ada",
        Reference = "AB-123456",
        Nights = 3,
        Start = new DateOnly(2026, 1, 1),
        End = new DateOnly(2026, 1, 4),
        Notes = null,
        Guests = 2,
        Deposit = 1.50m,
        Rooms = ["101", "102"],
    };

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Validate_OnAValidValue_ReportsNothing(IValidatorFor<Reservation> validator) {
        Assert.True(validator.Validate(Valid()).IsValid, validator.GetType().Name);
    }

    /// <summary>
    /// The three constraints declared through the fluent API rather than through attributes, on both
    /// engines. A kind only one front end can produce is a kind whose two paths cannot be compared.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Validate_EnforcesTheOneSidedRangeTheMultipleAndTheUniqueness(
        IValidatorFor<Reservation> validator) {

        Assert.True(validator.Validate(Valid() with { Guests = 1 }).IsValid);
        Assert.False(validator.Validate(Valid() with { Guests = 0 }).IsValid);

        Assert.True(validator.Validate(Valid() with { Deposit = 0.05m }).IsValid);
        Assert.False(validator.Validate(Valid() with { Deposit = 0.03m }).IsValid);

        Assert.True(validator.Validate(Valid() with { Rooms = ["a", "b"] }).IsValid);
        Assert.False(validator.Validate(Valid() with { Rooms = ["a", "a"] }).IsValid);
    }

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Validate_RendersAPredicateAsItsOwnMessage(IValidatorFor<Reservation> validator) {
        var result = validator.Validate(Valid() with { End = new DateOnly(2025, 1, 1) });

        var error = Assert.Single(result.Errors);
        Assert.Equal("start", error.Field);
        Assert.Equal(ValidationCodes.Predicate, error.Code);
        Assert.Equal("start < end.", error.Message);
    }

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Validate_OnAPredicateWithANamedCode_UsesIt(IValidatorFor<Reservation> validator) {
        var result = validator.Validate(Valid() with { Nights = 20, Notes = null });

        var error = Assert.Single(result.Errors);
        Assert.Equal("long_stay_needs_notes", error.Code);
    }

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Validate_RunsAPredicateEvenWhenAnotherRuleOnTheSameFieldFailed(IValidatorFor<Reservation> validator) {
        // A predicate is anchored to a field but may read others, so it stays out of the else-if
        // chain. Emitting it as `else if` would have this engine report one error where the other
        // reports two - the divergence this test exists to stop.
        var result = validator.Validate(Valid() with { Nights = 99, Notes = null });

        Assert.Equal(
            [ValidationCodes.Range, "long_stay_needs_notes"],
            result.Errors.Select(error => error.Code));
    }

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Validate_AppliesAHandWrittenRuleLast(IValidatorFor<Reservation> validator) {
        var result = validator.Validate(Valid() with { Guest = "Zed" });

        var error = Assert.Single(result.Errors);
        Assert.Equal("guest_initial", error.Code);
    }

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Validate_WhenRequiredFails_SuppressesTheRestOfTheField(IValidatorFor<Reservation> validator) {
        // §4.3: enforced by the collector, so both engines get it without either implementing it.
        // Asserted on the field under test rather than on the whole result, because suppression is
        // an exact path match and not a prefix one - the applied rule reports against `reference`
        // and is meant to survive.
        var result = validator.Validate(Valid() with { Guest = "  " });

        var error = Assert.Single(result.Errors, error => error.Field == "guest");
        Assert.Equal(ValidationCodes.Required, error.Code);
    }

    [Fact]
    public void BothEngines_ProduceIdenticalErrorsForEveryFailingShape() {
        // The substitutability claim itself, over full sequences: same fields, same codes, same
        // messages, same order. Ordering is the one that was actually wrong first - the generator
        // walks properties and the runtime walks the body, and they only agree because both group by
        // field and take fields in first-mention order.
        Reservation[] cases = [
            new(),
            Valid() with { Guest = null, Nights = 0 },
            Valid() with { Guest = "A", Reference = "nope", Nights = 99 },
            Valid() with { Notes = ["a", "b", "c", "d"], End = new DateOnly(2020, 1, 1) },
            Valid() with { Guest = "Zed", Nights = 40, Notes = null, End = new DateOnly(2020, 1, 1) },
            Valid() with { Guests = 0 },
            Valid() with { Deposit = 1.51m },
            Valid() with { Rooms = ["101", "101"] },
            Valid() with { Guests = 0, Deposit = 0.03m, Rooms = ["a", "a"] },
        ];

        foreach (var value in cases) {
            Assert.Equal(
                Generated.Validate(value).Errors.Select(error => (error.Field, error.Code, error.Message)),
                Described.Validate(value).Errors.Select(error => (error.Field, error.Code, error.Message)));
        }
    }
}
