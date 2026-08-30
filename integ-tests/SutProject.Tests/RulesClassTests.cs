using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// A rules class transcribed into real generated code, compiled against the real runtime and run.
/// </summary>
/// <remarks>
/// <c>ReservationRules</c> targets a type with no attributes and no way to take one - the case the
/// feature exists for. Its body is read by the generator, expanded into
/// <c>ReservationRules_Rules.Describe</c>, and called by <c>ReservationValidator</c>; nothing here
/// is a golden file, so what these assert is the emitted code's behaviour, ordering included.
/// </remarks>
public class RulesClassTests {

    private static readonly IValidatorFor<Reservation> Validator = new ReservationValidator();

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

    [Fact]
    public void Validate_OnAValidValue_ReportsNothing() {
        Assert.True(Validator.Validate(Valid()).IsValid);
    }

    /// <summary>
    /// The constraints only the rules surface declares here - one-sided range, multiple-of,
    /// uniqueness - expand through the same check writer the attributes use.
    /// </summary>
    [Fact]
    public void Validate_EnforcesTheOneSidedRangeTheMultipleAndTheUniqueness() {
        Assert.True(Validator.Validate(Valid() with { Guests = 1 }).IsValid);
        Assert.False(Validator.Validate(Valid() with { Guests = 0 }).IsValid);

        Assert.True(Validator.Validate(Valid() with { Deposit = 0.05m }).IsValid);
        Assert.False(Validator.Validate(Valid() with { Deposit = 0.03m }).IsValid);

        Assert.True(Validator.Validate(Valid() with { Rooms = ["a", "b"] }).IsValid);
        Assert.False(Validator.Validate(Valid() with { Rooms = ["a", "a"] }).IsValid);
    }

    [Fact]
    public void Validate_RendersAnEnsureAsItsOwnMessageAndCode() {
        var result = Validator.Validate(Valid() with { End = new DateOnly(2025, 1, 1) });

        var error = Assert.Single(result.Errors);
        Assert.Equal("start", error.Field);
        Assert.Equal("start_less_than_end", error.Code);
        Assert.Equal("start < end.", error.Message);
    }

    [Fact]
    public void ADerivedCode_TranslatesOneRuleWithoutTouchingTheOthers() {
        // What deriving a code is for. Every Ensure used to report "predicate", so a catalogue
        // keyed by code could not translate one predicate without translating all of them.
        var french = new ValidationMessageMap()
            .Map("start_less_than_end",
                static (in ValidationError _) => "la date de début doit précéder la date de fin.");

        var derived = Assert.Single(Validator.Validate(Valid() with { End = new DateOnly(2025, 1, 1) }).Errors);
        var other = Assert.Single(Validator.Validate(Valid() with { Nights = 20, Notes = null }).Errors);

        Assert.Equal("la date de début doit précéder la date de fin.", derived.ToMessage(french));
        Assert.Equal(other.Message, other.ToMessage(french));
    }

    [Fact]
    public void Validate_OnAnEnsureWithANamedCode_UsesIt() {
        var result = Validator.Validate(Valid() with { Nights = 20, Notes = null });

        var error = Assert.Single(result.Errors);
        Assert.Equal("long_stay_needs_notes", error.Code);
    }

    [Fact]
    public void Validate_RunsAnEnsureEvenWhenAnotherRuleOnTheSameFieldFailed() {
        // Separate statements report independently: the Range on nights and the Ensure anchored to
        // nights are two statements, and an Ensure may read fields other than its anchor - so a
        // failure on the anchor says nothing about it.
        var result = Validator.Validate(Valid() with { Nights = 99, Notes = null });

        Assert.Equal(
            [ValidationCodes.Range, "long_stay_needs_notes"],
            result.Errors.Select(error => error.Code));
    }

    [Fact]
    public void Validate_AppliesAHandWrittenRuleLast() {
        var result = Validator.Validate(Valid() with { Guest = "Zed" });

        var error = Assert.Single(result.Errors);
        Assert.Equal("guest_initial", error.Code);
    }

    [Fact]
    public void Validate_WhenRequireFails_SuppressesTheRestOfItsChain() {
        // Chain-scoped suppression: the Length chained after Require never reports on a missing
        // guest. The applied rule reports against `reference` and is meant to survive.
        var result = Validator.Validate(Valid() with { Guest = "  " });

        var error = Assert.Single(result.Errors, error => error.Field == "guest");
        Assert.Equal(ValidationCodes.Required, error.Code);
    }

    /// <summary>
    /// Full sequences over failing shapes: the body is the validator, so errors come out in body
    /// order, chains suppressed, null-guarded checks skipped, the applied rule last.
    /// </summary>
    [Fact]
    public void Validate_ReportsInBodyOrder() {
        Assert.Equal(
            [
                ("guest", ValidationCodes.Required),
                ("nights", ValidationCodes.Range),
                ("guests", ValidationCodes.Range),
                ("start", "start_less_than_end"),
            ],
            Validator.Validate(new Reservation()).Errors.Select(error => (error.Field, error.Code)));

        Assert.Equal(
            [
                ("guest", ValidationCodes.StringLength),
                ("reference", ValidationCodes.Pattern),
                ("nights", ValidationCodes.Range),
                ("nights", "long_stay_needs_notes"),
                ("reference", "guest_initial"),
            ],
            Validator.Validate(Valid() with { Guest = "A", Reference = "nope", Nights = 99 })
                .Errors.Select(error => (error.Field, error.Code)));
    }

    /// <summary>
    /// <c>field:</c> renames the error. The rule is still anchored to the property it reads, but
    /// the name it reports under is its own - and a property carrying several rules must not
    /// collapse them onto the first one's name.
    /// </summary>
    [Fact]
    public void Ensure_WithAnExplicitField_ReportsUnderThatField() {
        var filing = new Filing { Reference = "R-1", Attachment = null, DaysLate = 0 };

        var result = new FilingValidator().Validate(filing);

        var error = Assert.Single(result.Errors);
        Assert.Equal("attachment", error.Field);
        Assert.Equal("attachment_required", error.Code);
    }

    /// <summary>
    /// A warning is surfaced and the value stays valid - <c>severity:</c> flows through the
    /// transcribed Ensure.
    /// </summary>
    [Fact]
    public void Ensure_WithAWarning_SurfacesWithoutFailingTheValue() {
        var filing = new Filing { Reference = "R-1", Attachment = "a.pdf", DaysLate = 45 };

        var result = new FilingValidator().Validate(filing);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationSeverity.Warning, error.Severity);
        Assert.Equal("daysLate", error.Field);

        Assert.True(result.IsValid);
        Assert.True(result.HasErrors);
    }

    /// <summary>
    /// A type with a region loses the straight-line <c>IsValid</c> and falls back to the interface
    /// default, which walks Validate - so it still agrees: a warning is not a failure.
    /// </summary>
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
