using System.ComponentModel.DataAnnotations;
using ValidationModules.Naming;
using Xunit;
using DataAnnotationsResult = System.ComponentModel.DataAnnotations.ValidationResult;
using DataAnnotationsContext = System.ComponentModel.DataAnnotations.ValidationContext;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The bridge that runs custom DataAnnotations surfaces from generated code. The behaviour under
/// test is fidelity: what <c>Validator.TryValidateObject</c> would have done, minus the discovery,
/// so several tests run the same input through the real Validator and compare.
/// </summary>
public class DataAnnotationsSupportTests {

    private sealed class EvenAttribute : ValidationAttribute {
        public override bool IsValid(object? value) => value is null || (value is int n && n % 2 == 0);
    }

    /// <summary>
    /// Overrides only the protected, context-taking IsValid and does not override
    /// RequiresValidationContext - the wild-attribute shape that works under Validator because a
    /// context is always supplied, and the reason the bridge never skips building one.
    /// </summary>
    private sealed class NeedsServiceAttribute : ValidationAttribute {
        protected override DataAnnotationsResult? IsValid(object? value, DataAnnotationsContext validationContext) =>
            validationContext.GetService(typeof(string)) is string
                ? DataAnnotationsResult.Success
                : new DataAnnotationsResult($"no service for {validationContext.DisplayName}");
    }

    private sealed class Payload {
        public int Count { get; set; }
    }

    private sealed class Provider : IServiceProvider {
        public object? GetService(Type serviceType) => serviceType == typeof(string) ? "here" : null;
    }

    [Fact]
    public void Validate_PassingValue_ReportsNothing() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        var flow = DataAnnotationsSupport.Validate(
            ref context, new EvenAttribute(), new Payload(), 4, "count", "Count", "Count");

        Assert.Equal(ValidationFlow.Continue, flow);
        Assert.False(collector.HasErrors);
    }

    [Fact]
    public void Validate_FailingValue_ReportsTheAttributesOwnMessageUnderCustom() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);
        var attribute = new EvenAttribute();

        DataAnnotationsSupport.Validate(ref context, attribute, new Payload(), 3, "count", "Count", "Count");

        var error = Assert.Single(collector.ToResult().Errors);

        Assert.Equal("count", error.Field);
        Assert.Equal(ValidationCodes.Custom, error.Code);

        // The message is the one DataAnnotations itself would have produced, {0} filled with the
        // display name resolved at build time.
        Assert.Equal(attribute.FormatErrorMessage("Count"), error.Message);
    }

    [Fact]
    public void Validate_MatchesWhatTryValidateObjectSays() {
        var model = new Payload { Count = 3 };
        var attribute = new EvenAttribute();

        var daResults = new List<DataAnnotationsResult>();
        var daContext = new DataAnnotationsContext(model) { MemberName = "Count", DisplayName = "Count" };

        Validator.TryValidateValue(3, daContext, daResults, [attribute]);

        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        DataAnnotationsSupport.Validate(ref context, attribute, model, 3, "count", "Count", "Count");

        Assert.Equal(
            Assert.Single(daResults).ErrorMessage,
            Assert.Single(collector.ToResult().Errors).Message);
    }

    [Fact]
    public void Validate_ContextRequiringAttribute_ReachesThePassesServices() {
        var collector = new ValidationErrorCollector(new Provider());
        var context = new ValidationContext(collector);

        var flow = DataAnnotationsSupport.Validate(
            ref context, new NeedsServiceAttribute(), new Payload(), 1, "count", "Count", "Count");

        Assert.Equal(ValidationFlow.Continue, flow);
        Assert.False(collector.HasErrors);
    }

    [Fact]
    public void Validate_ContextRequiringAttribute_SeesTheBuildTimeDisplayName() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        DataAnnotationsSupport.Validate(
            ref context, new NeedsServiceAttribute(), new Payload(), 1, "count", "Count", "The Count");

        Assert.Equal("no service for The Count", Assert.Single(collector.ToResult().Errors).Message);
    }

    [Fact]
    public void IsValid_AgreesWithValidateOnBothKindsOfAttribute() {
        var payload = new Payload();

        Assert.True(DataAnnotationsSupport.IsValid(new EvenAttribute(), payload, 4, "Count", "Count"));
        Assert.False(DataAnnotationsSupport.IsValid(new EvenAttribute(), payload, 3, "Count", "Count"));

        // The boolean pass has no services, which is what TryValidateObject hands attributes when
        // no provider was supplied - so a service-requiring attribute fails there.
        Assert.False(DataAnnotationsSupport.IsValid(new NeedsServiceAttribute(), payload, 1, "Count", "Count"));
    }

    [Fact]
    public void Apply_Success_ReportsNothing() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        var flow = DataAnnotationsSupport.Apply(
            ref context, DataAnnotationsResult.Success, "count", CamelCaseFieldNamer.Instance);

        Assert.Equal(ValidationFlow.Continue, flow);
        Assert.False(collector.HasErrors);
    }

    [Fact]
    public void Apply_MemberNames_AreNamedWithTheSamePolicyAsCompiledLiterals() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        DataAnnotationsSupport.Apply(
            ref context,
            new DataAnnotationsResult("mismatch", ["PostalCode", "HouseNumber"]),
            "address",
            CamelCaseFieldNamer.Instance);

        var errors = collector.ToResult().Errors;

        Assert.Equal(["postalCode", "houseNumber"], errors.Select(error => error.Field));
        Assert.All(errors, error => Assert.Equal("mismatch", error.Message));
        Assert.All(errors, error => Assert.Equal(ValidationCodes.Custom, error.Code));
    }

    [Fact]
    public void Apply_NoMemberNames_LandsOnTheDeclaringField() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        DataAnnotationsSupport.Apply(
            ref context, new DataAnnotationsResult("bad"), "count", CamelCaseFieldNamer.Instance);

        Assert.Equal("count", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Apply_TypeLevelWithNoMembers_ReportsAgainstTheObjectItself() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        DataAnnotationsSupport.Apply(
            ref context, new DataAnnotationsResult("bad"), field: null, CamelCaseFieldNamer.Instance);

        Assert.Equal(string.Empty, Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Apply_NullMessage_ComposesAFallbackRatherThanAnEmptyError() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        DataAnnotationsSupport.Apply(
            ref context, new DataAnnotationsResult(null), "count", CamelCaseFieldNamer.Instance);

        Assert.Equal("count is invalid.", Assert.Single(collector.ToResult().Errors).Message);
    }

    private sealed class SelfChecking : IValidatableObject {
        public int Start { get; set; }
        public int End { get; set; }

        public IEnumerable<DataAnnotationsResult> Validate(DataAnnotationsContext validationContext) {
            if (Start > End) {
                yield return new DataAnnotationsResult("start is after end", [nameof(Start)]);
            }
        }
    }

    [Fact]
    public void ValidateObject_MapsEveryYieldedResult() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        var flow = DataAnnotationsSupport.ValidateObject(
            ref context, new SelfChecking { Start = 5, End = 1 }, CamelCaseFieldNamer.Instance);

        Assert.Equal(ValidationFlow.Continue, flow);

        var error = Assert.Single(collector.ToResult().Errors);

        Assert.Equal("start", error.Field);
        Assert.Equal("start is after end", error.Message);
    }

    [Fact]
    public void ValidateObject_CleanObject_ReportsNothing() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        DataAnnotationsSupport.ValidateObject(
            ref context, new SelfChecking { Start = 1, End = 5 }, CamelCaseFieldNamer.Instance);

        Assert.False(collector.HasErrors);
    }

    [Fact]
    public void CreateContext_CarriesMemberDisplayAndServices() {
        var services = new Provider();
        var payload = new Payload();

        var context = DataAnnotationsSupport.CreateContext(services, payload, "Count", "The Count");

        Assert.Same(payload, context.ObjectInstance);
        Assert.Equal("Count", context.MemberName);

        // Reading DisplayName must return the assigned value without entering the reflective
        // resolution - on net8.0 that is the whole justification for the suppression in the
        // factory, so this assertion is the suppression's evidence.
        Assert.Equal("The Count", context.DisplayName);
        Assert.Equal("here", context.GetService(typeof(string)));
    }
}
