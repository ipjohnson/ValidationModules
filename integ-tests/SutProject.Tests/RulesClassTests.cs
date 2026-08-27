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

    /// <summary>
    /// <c>field:</c> renames the error. The rule is still anchored to the property it reads, so the
    /// two engines agree on ordering, but the name it reports under is its own - and a property
    /// carrying several rules must not collapse them onto the first one's name.
    /// </summary>
    [Fact]
    public void Ensure_WithAnExplicitField_ReportsUnderThatField() {
        var filing = new Filing { Reference = "R-1", Attachment = null, DaysLate = 0 };

        var generated = new FilingValidator().Validate(filing);
        var described = new DescribedValidator<Filing>(new FilingRules()).Validate(filing);

        var error = Assert.Single(generated.Errors);
        Assert.Equal("attachment", error.Field);
        Assert.Equal("attachment_required", error.Code);

        Assert.Equal(
            described.Errors.Select(e => (e.Field, e.Code)).OrderBy(x => x.Field),
            generated.Errors.Select(e => (e.Field, e.Code)).OrderBy(x => x.Field));
    }

    /// <summary>
    /// A warning is surfaced and the value stays valid. The generator dropped <c>severity:</c>, so
    /// the same rules class reported Error through generated code and Warning through the runtime -
    /// the two engines disagreeing on whether a value was acceptable.
    /// </summary>
    [Fact]
    public void Ensure_WithAWarning_SurfacesWithoutFailingTheValue() {
        var filing = new Filing { Reference = "R-1", Attachment = "a.pdf", DaysLate = 45 };

        var generated = new FilingValidator().Validate(filing);
        var described = new DescribedValidator<Filing>(new FilingRules()).Validate(filing);

        var error = Assert.Single(generated.Errors);
        Assert.Equal(ValidationSeverity.Warning, error.Severity);
        Assert.Equal("daysLate", error.Field);

        Assert.True(generated.IsValid);
        Assert.True(generated.HasErrors);
        Assert.Equal(described.IsValid, generated.IsValid);
        Assert.Equal(described.Errors.Single().Severity, error.Severity);
    }

    /// <summary>The boolean fast path agrees: a warning is not a failure.</summary>
    [Fact]
    public void IsValid_IgnoresAWarning() =>
        Assert.True(new FilingValidator().IsValid(
            new Filing { Reference = "R-1", Attachment = "a.pdf", DaysLate = 45 }));

    // ---- [EnumDefined] ----------------------------------------------------------------------

    /// <summary>
    /// The gap this closes: a deserialiser handed 99 produces (PaymentMethod)99, and a handler
    /// switching on it falls through every case it was written for. Nothing used to say so.
    /// </summary>
    [Fact]
    public void EnumDefined_RejectsAValueTheEnumDoesNotDeclare() {
        var result = new SutProject.Nesting.PaymentValidator().Validate(new SutProject.Nesting.Payment {
            Method = (SutProject.Nesting.PaymentMethod)99,
        });

        var error = Assert.Single(result.Errors);
        Assert.Equal("method", error.Field);
        Assert.Equal(ValidationCodes.Enum, error.Code);
        Assert.Contains("Card, Cash, Transfer", error.Message);
    }

    [Fact]
    public void EnumDefined_AcceptsEveryDeclaredMember() {
        foreach (var method in Enum.GetValues<SutProject.Nesting.PaymentMethod>()) {
            Assert.True(new SutProject.Nesting.PaymentValidator().IsValid(
                new SutProject.Nesting.Payment { Method = method }));
        }
    }

    /// <summary>
    /// A combination equals no declared member, so membership would reject what the type exists to
    /// express. The test is whether any bit outside the declared ones is set.
    /// </summary>
    [Fact]
    public void EnumDefined_OnFlags_AcceptsACombinationAndRejectsAnUndeclaredBit() {
        var combination = new SutProject.Nesting.Payment {
            Rights = SutProject.Nesting.Access.Read | SutProject.Nesting.Access.Delete,
        };

        Assert.True(new SutProject.Nesting.PaymentValidator().IsValid(combination));

        var undeclared = new SutProject.Nesting.Payment { Rights = (SutProject.Nesting.Access)64 };
        var error = Assert.Single(new SutProject.Nesting.PaymentValidator().Validate(undeclared).Errors);

        Assert.Equal("rights", error.Field);
        Assert.Contains("combination of", error.Message);
    }

    /// <summary>Absent is not undefined: [EnumDefined] does not imply [Required].</summary>
    [Fact]
    public void EnumDefined_OnANullable_AcceptsNullAndChecksAValue() {
        Assert.True(new SutProject.Nesting.PaymentValidator().IsValid(
            new SutProject.Nesting.Payment { Fallback = null }));

        Assert.False(new SutProject.Nesting.PaymentValidator().IsValid(
            new SutProject.Nesting.Payment { Fallback = (SutProject.Nesting.PaymentMethod)77 }));
    }
}
