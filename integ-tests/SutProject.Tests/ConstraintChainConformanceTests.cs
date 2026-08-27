using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// The rule from IMPLEMENTATION-PLAN.md §4.2 - all errors are collected, and a failed
/// <c>[Required]</c> is the only short-circuit - asserted across both engines of API-SURFACE.md §19.
/// </summary>
/// <remarks>
/// <para>
/// This is a regression test with a specific bug behind it. The emitter used to chain a property's
/// constraints with <c>else if</c>, so the second failing constraint on a field was never reached
/// and the generated engine reported one error per field where the described engine reported all of
/// them. The comment on the chain called it "an optimization, not the suppression mechanism", which
/// is what it was meant to be and not what it did.
/// </para>
/// <para>
/// Written as a parity test rather than a count assertion on the generated arm alone: the failure
/// that matters is the two engines disagreeing, and a test that pins only one of them would have
/// passed throughout the period the bug existed.
/// </para>
/// </remarks>
public class ConstraintChainConformanceTests {

    private static readonly IValidatorFor<Ticket> Generated = new TicketValidator();
    private static readonly IValidatorFor<Ticket> Described = new DescribedValidator<Ticket>(new TicketRules());

    public static TheoryData<IValidatorFor<Ticket>> BothEngines => new() { Generated, Described };

    /// <summary>
    /// Two constraints failing on one field, in the three arrangements the divergence was
    /// reproduced with: behind a passing <c>Required</c>, with no <c>Required</c> at all, and on a
    /// value type.
    /// </summary>
    private static Ticket TwoFailuresPerField() => new() {
        Code = "AB",
        Note = "AB",
        Amount = 5m,
    };

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Validate_ReportsEveryFailingConstraintOnAField(IValidatorFor<Ticket> validator) {
        var errors = validator.Validate(TwoFailuresPerField()).Errors;

        Assert.Equal(
            [
                ("code", ValidationCodes.StringLength),
                ("code", ValidationCodes.Pattern),
                ("note", ValidationCodes.StringLength),
                ("note", ValidationCodes.Pattern),
                ("amount", ValidationCodes.Range),
                ("amount", ValidationCodes.MultipleOf),
            ],
            errors.Select(error => (error.Field, error.Code)));
    }

    /// <summary>
    /// The one short-circuit §4.2 does allow. A failed <c>Required</c> suppresses the rest of its
    /// own field and nothing else, so the other two fields still report both of theirs.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Validate_WhenRequiredFails_SuppressesOnlyItsOwnField(IValidatorFor<Ticket> validator) {
        var errors = validator.Validate(TwoFailuresPerField() with { Code = null }).Errors;

        Assert.Equal(
            [
                ("code", ValidationCodes.Required),
                ("note", ValidationCodes.StringLength),
                ("note", ValidationCodes.Pattern),
                ("amount", ValidationCodes.Range),
                ("amount", ValidationCodes.MultipleOf),
            ],
            errors.Select(error => (error.Field, error.Code)));
    }

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Validate_OnAValueSatisfyingEveryConstraint_ReportsNothing(IValidatorFor<Ticket> validator) {
        var valid = new Ticket { Code = "12345", Note = "67890", Amount = 12m };

        Assert.True(validator.Validate(valid).IsValid, validator.GetType().Name);
    }

    /// <summary>
    /// The boolean path skips the message, the path and the error record, but it may not disagree
    /// with <c>Validate</c> about whether the value is valid.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothEngines))]
    public void IsValid_AgreesWithValidate(IValidatorFor<Ticket> validator) {
        Assert.False(validator.IsValid(TwoFailuresPerField()));
        Assert.True(validator.IsValid(new Ticket { Code = "12345", Note = "67890", Amount = 12m }));
    }
}
