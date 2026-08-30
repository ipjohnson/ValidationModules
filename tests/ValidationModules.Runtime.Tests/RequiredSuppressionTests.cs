using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins the two scopes of the rule: a failed <c>Require</c>
/// suppresses the rest of its own chained statement through the <c>else if</c> the generator
/// emits, and the collector's path-keyed rule covers only the adapter path.
/// </summary>
/// <remarks>
/// The collector rule exists because an engine that maps errors from somewhere else, like the
/// FluentValidation adapter, has no <c>else</c> to put suppression in. It deliberately does not
/// cover <see cref="ValidationContext"/> reports: that path reports positions, and two positions
/// can render to the same bounded path - the sibling regression pinned below. Chain-scoped
/// suppression in real generated validators is the integ-tests' coverage.
/// </remarks>
public class RequiredSuppressionTests {

    [Fact]
    public void ContextAdd_DoesNotSuppressAcrossStatements() {
        // A bare context records what it is told - which is what stops two positions that render
        // alike from silencing each other. Statement-to-statement suppression is the else-if of a
        // single chain, emitted by the generator.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.ReportRequired("name");
        context.ReportStringLength("name", max: 10);

        Assert.Equal(2, collector.ToResult().Errors.Count);
    }

    [Fact]
    public void Add_AfterRequiredOnADifferentField_IsKept() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.ReportRequired("name");
        context.ReportStringLength("tag", max: 10);

        Assert.Equal(["name", "tag"], collector.ToResult().Errors.Select(error => error.Field));
    }

    [Fact]
    public void SiblingsThatRenderToTheSamePathEachKeepTheirError() {
        // The bug the move fixes. Three different items, one elided rendering, three failures.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        for (var i = 0; i < 3; i++) {
            context.Push("two").Push("three").PushIndex("items", i).Push("five").ReportRequired("req");
        }

        var errors = collector.ToResult().Errors;

        Assert.Equal(3, errors.Count);
        Assert.All(errors, error => Assert.Equal("two...five.req", error.Field));
    }

    [Fact]
    public void Suppression_MatchesTheWholePathNotTheLeaf() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.Push("home").ReportRequired("postalCode");
        context.Push("work").ReportStringLength("postalCode", max: 5);

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

        context.ReportRequired("home");
        context.Push("home").ReportRequired("postalCode");

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

        context.ReportStringLength("name", max: 10);
        context.ReportRequired("name");

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

        context.ReportRequired("name", ValidationSeverity.Warning);
        context.ReportStringLength("name", max: 10);

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

        context.ReportRequired("name");
        await Task.Yield();
        context.ReportStringLength("name", max: 10);

        Assert.Equal(2, collector.ToResult().Errors.Count);
    }

    [Fact]
    public void Reset_ClearsSuppressionState() {
        // A pooled collector must not carry one request's missing field into the next.
        var collector = new ValidationErrorCollector();
        new ValidationContext(collector).ReportRequired("name");

        collector.Reset();
        new ValidationContext(collector).ReportStringLength("name", max: 10);

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
            context.ReportStringLength($"f{i}", max: 10);
        }

        Assert.Equal(20, collector.ToResult().Errors.Count);
    }

    private sealed class RequiredOnly : IValidatorFor<Pet> {
        public static readonly RequiredOnly Instance = new();

        public ValidationFlow Validate(ref ValidationContext context, Pet value) =>
            value.Name is null ? context.ReportRequired("name") : ValidationFlow.Continue;
    }

    private sealed class LengthOnly : IValidatorFor<Pet> {
        public static readonly LengthOnly Instance = new();

        public ValidationFlow Validate(ref ValidationContext context, Pet value) =>
            value.Name is null || value.Name.Length > 3
                ? context.ReportStringLength("name", max: 3)
                : ValidationFlow.Continue;
    }
}
