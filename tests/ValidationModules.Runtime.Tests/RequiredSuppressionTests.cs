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
    public void Add_AfterRequiredOnTheSameField_IsSuppressed() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.AddRequired("name");
        context.AddStringLength("name", max: 10);

        var error = Assert.Single(collector.ToResult().Errors);
        Assert.Equal(ValidationCodes.Required, error.Code);
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
    public void Add_SecondRequiredOnTheSameField_IsSuppressed() {
        // Two validators registered for one type both find it missing. One error, not two.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.AddRequired("name");
        context.AddRequired("name");

        Assert.Single(collector.ToResult().Errors);
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
    public void Suppression_SpansValidatorsWithinOnePass() {
        var runner = new ValidationRunner<Pet>([RequiredOnly.Instance, LengthOnly.Instance], []);

        var result = runner.Validate(new Pet { Toys = [new Toy { Name = "ball" }] });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.Required, error.Code);
    }

    [Fact]
    public async Task Suppression_SpansTheAsyncBoundary() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.AddRequired("name");
        await Task.Yield();
        context.AddStringLength("name", max: 10);

        Assert.Single(collector.ToResult().Errors);
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
