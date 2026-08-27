using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins the rule from IMPLEMENTATION-PLAN.md §4: a failed <c>[Required]</c> suppresses every other
/// constraint on the same field.
/// </summary>
/// <remarks>
/// These assert it at the collector rather than through generated control flow, because that is
/// where it is now enforced - an engine that maps errors from somewhere else, like the
/// FluentValidation adapter, has no <c>else</c> to put it in and would otherwise be unable to
/// conform.
/// </remarks>
public class RequiredSuppressionTests {

    [Fact]
    public void RuleChain_AfterRequiredOnTheSameField_IsSuppressed() {
        var validator = new DescribedValidator<Pet>(new NameRequiredThenLength());

        var error = Assert.Single(validator.Validate(new Pet()).Errors);
        Assert.Equal(ValidationCodes.Required, error.Code);
    }

    [Fact]
    public void RuleChain_SuppressesEvenWhenTheRulesAreDeclaredApart() {
        // Build groups a field's rules together regardless of where they were written, so the
        // chain still sees them as one field.
        var validator = new DescribedValidator<Pet>(new NameRulesSplitByAnotherField());

        var errors = validator.Validate(new Pet()).Errors;

        Assert.Equal(ValidationCodes.Required, Assert.Single(errors, e => e.Field == "name").Code);
    }

    [Fact]
    public void ContextAdd_NoLongerSuppressesAcrossTheWholePass() {
        // The rule moved to where a field's rules are composed. Generated code short-circuits with
        // an else if; a described validator with a field chain. A bare context does neither, so it
        // records what it is told - which is what stops two positions that render alike from
        // silencing each other.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.AddRequired("name");
        context.AddStringLength("name", max: 10);

        Assert.Equal(2, collector.ToResult().Errors.Count);
    }

    [Fact]
    public void Add_AfterRequiredOnADifferentField_IsKept() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.AddRequired("name");
        context.AddStringLength("tag", max: 10);

        Assert.Equal(["name", "tag"], collector.ToResult().Errors.Select(error => error.Field));
    }

    [Fact]
    public void SiblingsThatRenderToTheSamePathEachKeepTheirError() {
        // The bug the move fixes. Three different items, one elided rendering, three failures.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        for (var i = 0; i < 3; i++) {
            context.Push("two").Push("three").PushIndex("items", i).Push("five").AddRequired("req");
        }

        var errors = collector.ToResult().Errors;

        Assert.Equal(3, errors.Count);
        Assert.All(errors, error => Assert.Equal("two...five.req", error.Field));
    }

    [Fact]
    public void Suppression_MatchesTheWholePathNotTheLeaf() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.Push("home").AddRequired("postalCode");
        context.Push("work").AddStringLength("postalCode", max: 5);

        Assert.Equal(
            ["home.postalCode", "work.postalCode"],
            collector.ToResult().Errors.Select(error => error.Field));
    }

    [Fact]
    public void Suppression_IsNotAPrefixMatch() {
        // A failed Required on `home` must not swallow errors under `home.`. Nothing recurses into
        // a value that failed Required anyway, so there would be nothing to suppress - and treating
        // it as a prefix would hide unrelated failures on a sibling path.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.AddRequired("home");
        context.Push("home").AddRequired("postalCode");

        Assert.Equal(
            ["home", "home.postalCode"],
            collector.ToResult().Errors.Select(error => error.Field));
    }

    [Fact]
    public void Suppression_IsForwardOnly() {
        // An error already recorded is not removed retroactively. A result whose contents change
        // because of a later unrelated add is harder to reason about than a rare duplicate.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.AddStringLength("name", max: 10);
        context.AddRequired("name");

        Assert.Equal(
            [ValidationCodes.StringLength, ValidationCodes.Required],
            collector.ToResult().Errors.Select(error => error.Code));
    }

    [Fact]
    public void Suppression_RequiresErrorSeverity() {
        // Required reported as a warning is advisory; silencing the field on the strength of it
        // would drop a real failure.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.AddRequired("name", ValidationSeverity.Warning);
        context.AddStringLength("name", max: 10);

        Assert.Equal(2, collector.ToResult().Errors.Count);
    }

    [Fact]
    public void Suppression_AppliesToPrePathedErrorsFromAnAdapter() {
        // The case the whole change exists for. An engine that maps failures produced elsewhere
        // reaches the collector through Add(in ValidationError) and has no control flow of its own.
        var collector = new ValidationErrorCollector();

        collector.Add(new ValidationError("name", ValidationCodes.Required, "name is required."));
        collector.Add(new ValidationError("name", ValidationCodes.StringLength, "too short."));
        collector.Add(new ValidationError("tag", ValidationCodes.StringLength, "too short."));

        Assert.Equal(["name", "tag"], collector.ToResult().Errors.Select(error => error.Field));
    }

    [Fact]
    public void Suppression_NoLongerSpansTwoValidatorsForOneType() {
        // Narrowed deliberately. Each validator short-circuits its own fields; neither can see
        // what the other recorded, so a hand-written validator composed with a generated one now
        // reports alongside it rather than being silenced by it.
        var runner = new ValidationRunner<Pet>([RequiredOnly.Instance, LengthOnly.Instance], []);

        var result = runner.Validate(new Pet { Toys = [new Toy { Name = "ball" }] });

        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task Suppression_NoLongerSpansTheAsyncBoundary() {
        // Same narrowing: an async validator composes rather than being suppressed by a sync one.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.AddRequired("name");
        await Task.Yield();
        context.AddStringLength("name", max: 10);

        Assert.Equal(2, collector.ToResult().Errors.Count);
    }

    [Fact]
    public void Reset_ClearsSuppressionState() {
        // A pooled collector must not carry one request's missing field into the next.
        var collector = new ValidationErrorCollector();
        new ValidationContext(collector).AddRequired("name");

        collector.Reset();
        new ValidationContext(collector).AddStringLength("name", max: 10);

        Assert.Equal(
            ValidationCodes.StringLength,
            Assert.Single(collector.ToResult().Errors).Code);
    }

    [Fact]
    public void Suppression_CostsNothingWhenNothingIsMissing() {
        // The scan is gated on a Required having been seen, so the ordinary failure path - a length
        // or range error with nothing missing - never touches it.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        for (var i = 0; i < 20; i++) {
            context.AddStringLength($"f{i}", max: 10);
        }

        Assert.Equal(20, collector.ToResult().Errors.Count);
    }

    private sealed class NameRequiredThenLength : IValidationRulesFor<Pet> {
        public void Describe(ValidationRules<Pet> rules) =>
            rules.Required(x => x.Name).Length(5, 10);
    }

    private sealed class NameRulesSplitByAnotherField : IValidationRulesFor<Pet> {
        public void Describe(ValidationRules<Pet> rules) {
            rules.Required(x => x.Name);
            rules.Required(x => x.Tag);
            rules.Length(x => x.Name, 5, 10);
        }
    }

    private sealed class RequiredOnly : IValidatorFor<Pet> {
        public static readonly RequiredOnly Instance = new();

        public void Validate(ref ValidationContext context, Pet value) {
            if (value.Name is null) {
                context.AddRequired("name");
            }
        }
    }

    private sealed class LengthOnly : IValidatorFor<Pet> {
        public static readonly LengthOnly Instance = new();

        public void Validate(ref ValidationContext context, Pet value) {
            if (value.Name is null || value.Name.Length > 3) {
                context.AddStringLength("name", max: 3);
            }
        }
    }
}
