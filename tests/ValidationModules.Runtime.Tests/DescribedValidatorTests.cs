using System.Text.RegularExpressions;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The generator-less engine from API-SURFACE.md §19.8, end to end.
/// </summary>
/// <remarks>
/// These also stand in for a compile test of the builder: the rules class below is the shape §19
/// documents, so overload resolution across the three <c>Required</c> forms, the anchored chaining
/// and the extension-method split all have to work for this file to build at all.
/// </remarks>
public partial class DescribedValidatorTests {

    public sealed record Booking {
        public string? Name { get; init; }
        public string? Sku { get; init; }
        public int Age { get; init; }
        public int? Seats { get; init; }
        public string? Status { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
        public DateOnly Start { get; init; }
        public DateOnly End { get; init; }
    }

    public static partial class Patterns {
        [GeneratedRegex("^[A-Z]{3}-[0-9]{4}$")]
        public static partial Regex Sku();
    }

    public sealed class BookingRules : IValidationRulesFor<Booking> {
        public void Describe(ValidationRules<Booking> rules) {
            rules.Required(x => x.Name).Length(1, 10);
            rules.Pattern(x => x.Sku, Patterns.Sku);
            rules.Range(x => x.Age, 0, 30);
            rules.Required(x => x.Seats);
            rules.AllowedValues(x => x.Status, ["open", "closed"]);
            rules.Count(x => x.Tags, 1, 3);

            rules.Ensure(x => x.Start < x.End);
            rules.Apply(Checks.SkuMatchesName);
        }
    }

    public static class Checks {
        public static ValidationFlow SkuMatchesName(ref ValidationContext context, Booking value) =>
            value.Sku is { } sku && value.Name is { } name && !sku.StartsWith(name[..1])
                ? context.Report("sku", "sku_prefix", "sku must start with the first letter of name.")
                : ValidationFlow.Continue;
    }

    private static readonly DescribedValidator<Booking> Validator = new(new BookingRules());

    private static Booking Valid() => new() {
        Name = "Ada",
        Sku = "ABC-1234",
        Age = 20,
        Seats = 2,
        Status = "open",
        Tags = ["one"],
        Start = new DateOnly(2026, 1, 1),
        End = new DateOnly(2026, 1, 2),
    };

    [Fact]
    public void Validate_OnAValidValue_ReportsNothing() {
        Assert.True(Validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void Validate_InfersFieldNamesFromSelectors() {
        var result = Validator.Validate(Valid() with { Name = null });

        var error = Assert.Single(result.Errors);
        Assert.Equal("name", error.Field);
        Assert.Equal(ValidationCodes.Required, error.Code);
    }

    [Fact]
    public void Validate_UsesTheSameCodesAndMessagesAsTheGeneratedEngine() {
        // Not a separate vocabulary: these rules reach the error through the same ctx.Add* helpers
        // the emitter calls, which is what keeps the two engines substitutable (§19.9).
        // The name still starts with 'A', so Checks.SkuMatchesName stays quiet and this asserts the
        // two constraints under test rather than three errors.
        var result = Validator.Validate(Valid() with { Name = "Aaaaaaaaaaaa", Age = 99 });

        Assert.Equal(
            [("name", ValidationCodes.StringLength, "name must be between 1 and 10 characters."),
             ("age", ValidationCodes.Range, "age must be between 0 and 30.")],
            result.Errors.Select(error => (error.Field, error.Code, error.Message)));
    }

    [Fact]
    public void Validate_OnAFailedEnsure_RendersThePredicateAsTheMessage() {
        var result = Validator.Validate(Valid() with { Start = new DateOnly(2026, 5, 1) });

        var error = Assert.Single(result.Errors);
        Assert.Equal("start", error.Field);
        Assert.Equal(ValidationCodes.Predicate, error.Code);
        Assert.Equal("start < end.", error.Message);
    }

    [Fact]
    public void Validate_OnAFailedApply_RecordsWhatTheMethodRecorded() {
        var result = Validator.Validate(Valid() with { Name = "Zed" });

        var error = Assert.Single(result.Errors);
        Assert.Equal("sku_prefix", error.Code);
    }

    [Fact]
    public void Validate_CollectsEveryFailure() {
        // No first-failure exit, per §4.2. Fields in first-mention order, per §19.7.
        //
        // Sku, Status and Tags are absent from this list on purpose: a null value is Required's
        // business, so a pattern, allowed-set or count rule on one skips rather than reporting a
        // second failure for the same missing value.
        var result = Validator.Validate(new Booking { Start = new DateOnly(2026, 1, 1) });

        Assert.Equal(
            ["name", "seats", "start"],
            result.Errors.Select(error => error.Field));
    }

    [Fact]
    public void Validate_WhenRequiredFails_SuppressesTheRestOfTheField() {
        // §4.3, and it comes free: the collector owns suppression and this engine reaches the same
        // collector rather than reimplementing the rule. Asserted on the field under test, because
        // suppression is an exact path match and not a prefix one - the applied rule reports against
        // `sku` and is meant to survive.
        var result = Validator.Validate(Valid() with { Name = "   " });

        var error = Assert.Single(result.Errors, error => error.Field == "name");
        Assert.Equal(ValidationCodes.Required, error.Code);
    }

    [Fact]
    public void Validate_HoistsRequiredAheadOfAnEarlierRuleOnTheSameField() {
        // Declared the wrong way round on purpose. Suppression is forward-only, so a Required
        // written second would otherwise fail to suppress the length check above it.
        var validator = new DescribedValidator<Booking>(new OutOfOrderRules());

        var error = Assert.Single(validator.Validate(new Booking()).Errors);
        Assert.Equal(ValidationCodes.Required, error.Code);
    }

    private sealed class OutOfOrderRules : IValidationRulesFor<Booking> {
        public void Describe(ValidationRules<Booking> rules) {
            rules.Length(x => x.Name, 1, 10);
            rules.Required(x => x.Name);
        }
    }

    [Fact]
    public void Validate_WithAnExplicitFieldName_UsesIt() {
        // The escape from §19.9's one divergence: the generator reads [JsonPropertyName] and this
        // engine cannot, so a property carrying one names its field on the rule.
        var validator = new DescribedValidator<Booking>(new RenamedRules());

        Assert.Equal("pet_name", Assert.Single(validator.Validate(new Booking()).Errors).Field);
    }

    private sealed class RenamedRules : IValidationRulesFor<Booking> {
        public void Describe(ValidationRules<Booking> rules) =>
            rules.Required(x => x.Name, field: "pet_name");
    }

    [Fact]
    public void Describe_WithAnUninferableEnsureAndNoField_FailsAtBuildTime() {
        // The runtime analogue of VM0075. It throws when the rule set is built, not per validation,
        // so a misdeclared rule cannot reach production as a silently mis-pathed error.
        var exception = Assert.Throws<InvalidOperationException>(
            () => new DescribedValidator<Booking>(new UnanchoredRules()));

        Assert.Contains("pass field: explicitly", exception.Message);
    }

    private sealed class UnanchoredRules : IValidationRulesFor<Booking> {
        public void Describe(ValidationRules<Booking> rules) =>
            rules.Ensure(x => DateOnly.MinValue < DateOnly.MaxValue);
    }

    [Fact]
    public void Describe_WithNestedRulesAndNoProvider_SaysSo() {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new DescribedValidator<Holder>(new HolderRules()));

        Assert.Contains("IValidatorProvider", exception.Message);
    }

    public sealed record Holder(Booking? Inner);

    private sealed class HolderRules : IValidationRulesFor<Holder> {
        public void Describe(ValidationRules<Holder> rules) => rules.Nested(x => x.Inner);
    }

    [Fact]
    public void Validate_DescendsIntoANestedObject() {
        var provider = new StubProvider(Validator);
        var validator = new DescribedValidator<Holder>(new HolderRules(), provider);

        var result = validator.Validate(new Holder(new Booking { Name = null }));

        // Every path is prefixed by the pushed segment, and the nested validator is the same
        // instance the flat tests use - descending changes the path and nothing else.
        Assert.Equal(["inner.name", "inner.seats", "inner.start"], result.Errors.Select(e => e.Field));
    }

    private sealed class StubProvider(IValidatorFor<Booking> booking) : IValidatorProvider {
        public IValidatorFor<TValue>? GetValidator<TValue>() => booking as IValidatorFor<TValue>;
    }
}
