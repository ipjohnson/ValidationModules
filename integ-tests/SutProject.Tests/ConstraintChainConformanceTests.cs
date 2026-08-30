using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// The rule: all errors are collected, and a failed
/// <c>Require</c> is the only short-circuit, scoped to its own chain - asserted through real
/// generated code.
/// </summary>
/// <remarks>
/// A regression suite with a specific bug behind it: the emitter used to chain a property's
/// constraints with <c>else if</c>, so the second failing constraint on a field was never reached.
/// Two constraints that both fail must both report, whether they share a chain or sit in separate
/// statements.
/// </remarks>
public class ConstraintChainConformanceTests {

    private static readonly IValidatorFor<Ticket> Validator = new TicketValidator();

    /// <summary>
    /// Two constraints failing on one field, in the three arrangements that matter: behind a
    /// passing <c>Require</c>, with no <c>Require</c> at all, and on a value type.
    /// </summary>
    private static Ticket TwoFailuresPerField() => new() {
        Code = "AB",
        Note = "AB",
        Amount = 5m,
    };

    [Fact]
    public void Validate_ReportsEveryFailingConstraintOnAField() {
        var errors = Validator.Validate(TwoFailuresPerField()).Errors;

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
    /// The one short-circuit that exists. A failed <c>Require</c> suppresses the rest of its own
    /// chain; the separate Pattern statement is null-guarded and skips a missing value on its own;
    /// the other two fields still report both of theirs.
    /// </summary>
    [Fact]
    public void Validate_WhenRequireFails_SuppressesOnlyItsOwnChain() {
        var errors = Validator.Validate(TwoFailuresPerField() with { Code = null }).Errors;

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

    [Fact]
    public void Validate_OnAValueSatisfyingEveryConstraint_ReportsNothing() {
        var valid = new Ticket { Code = "12345", Note = "67890", Amount = 12m };

        Assert.True(Validator.Validate(valid).IsValid);
    }

    /// <summary>
    /// The boolean path - here the interface default, since a region is present - may not disagree
    /// with <c>Validate</c> about whether the value is valid.
    /// </summary>
    [Fact]
    public void IsValid_AgreesWithValidate() {
        Assert.False(Validator.IsValid(TwoFailuresPerField()));
        Assert.True(Validator.IsValid(new Ticket { Code = "12345", Note = "67890", Amount = 12m }));
    }
}
