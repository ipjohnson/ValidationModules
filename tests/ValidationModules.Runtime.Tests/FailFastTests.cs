using ValidationModules.Rules;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// <see cref="ValidationStopMode.StopOnFirstError"/>: the rules after the first blocking failure are
/// not evaluated, rather than evaluated and discarded.
/// </summary>
/// <remarks>
/// The distinction is the whole feature, and it is not observable from the error list alone - a pass
/// that ran everything and reported one error looks identical. Every test that cares counts the
/// rules that actually ran, through a predicate with a side effect.
/// </remarks>
public class FailFastTests {

    private sealed record Order {
        public string? Reference { get; init; }
        public string? Customer { get; init; }
        public Address? ShipTo { get; init; }
    }

    private sealed record Address {
        public string? Line1 { get; init; }
        public string? PostalCode { get; init; }
    }

    private static int _evaluated;

    private sealed class AddressRules : IValidationRulesFor<Address> {
        public void Describe(ValidationRules<Address> rules) {
            rules.Required(x => x.Line1);
            rules.Required(x => x.PostalCode);
        }
    }

    private sealed class OrderRules : IValidationRulesFor<Order> {
        public void Describe(ValidationRules<Order> rules) {
            rules.Required(x => x.Reference);
            rules.Required(x => x.Customer);
            rules.Nested(x => x.ShipTo);
        }
    }

    private static DescribedValidator<Order> Validator() =>
        new(new OrderRules(), new AddressProvider());

    private sealed class AddressProvider : IValidatorProvider {
        public IValidatorFor<T>? GetValidator<T>() =>
            new DescribedValidator<Address>(new AddressRules()) as IValidatorFor<T>;
    }

    private static ValidationResult Run(Order order, ValidationStopMode mode) {
        var collector = new ValidationErrorCollector { StopMode = mode };

        Validator().ValidateInto(collector, order);

        return collector.ToResult();
    }

    // -- the mode itself -------------------------------------------------------------------------

    [Fact]
    public void CollectAll_IsTheDefault() =>
        Assert.Equal(ValidationStopMode.CollectAll, new ValidationErrorCollector().StopMode);

    [Fact]
    public void CollectAll_ReportsEveryFailure() {
        var result = Run(new Order(), ValidationStopMode.CollectAll);

        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void StopOnFirstError_ReportsOnlyTheFirst() {
        var result = Run(new Order(), ValidationStopMode.StopOnFirstError);

        Assert.Equal("Reference", Assert.Single(result.Errors).Field, ignoreCase: true);
    }

    /// <summary>
    /// The one that separates this from filtering a full result: the second rule never ran.
    /// </summary>
    [Fact]
    public void StopOnFirstError_DoesNotEvaluateLaterRules() {
        _evaluated = 0;

        var rules = new CountingRules();
        var collector = new ValidationErrorCollector { StopMode = ValidationStopMode.StopOnFirstError };

        new DescribedValidator<Order>(rules).ValidateInto(collector, new Order());

        Assert.Equal(1, _evaluated);
    }

    [Fact]
    public void CollectAll_EvaluatesLaterRules() {
        _evaluated = 0;

        new DescribedValidator<Order>(new CountingRules()).Validate(new Order());

        Assert.Equal(2, _evaluated);
    }

    private sealed class CountingRules : IValidationRulesFor<Order> {
        public void Describe(ValidationRules<Order> rules) {
            rules.Ensure(x => Count() && x.Reference is not null, code: "first");
            rules.Ensure(x => Count() && x.Customer is not null, code: "second");
        }

        private static bool Count() {
            _evaluated++;

            return true;
        }
    }

    // -- descent ---------------------------------------------------------------------------------

    /// <summary>A nested failure stops the parent, which is what makes the tree walk cheap.</summary>
    [Fact]
    public void StopOnFirstError_StopsDescendingAfterANestedFailure() {
        var order = new Order {
            Reference = "ORD-1",
            Customer = "Ada",
            ShipTo = new Address()
        };

        var result = Run(order, ValidationStopMode.StopOnFirstError);

        Assert.Equal("ShipTo.Line1", Assert.Single(result.Errors).Field, ignoreCase: true);
    }

    // -- severity --------------------------------------------------------------------------------

    /// <summary>
    /// A warning does not make a value invalid, so stopping on one would hide the error behind it.
    /// </summary>
    [Fact]
    public void AWarning_DoesNotStopThePass() {
        var collector = new ValidationErrorCollector { StopMode = ValidationStopMode.StopOnFirstError };
        var context = new ValidationContext(collector);

        var afterWarning = context.Report("a", "advisory", "x", ValidationSeverity.Warning);
        var afterError = context.Report("b", "blocked", "x");

        Assert.False(afterWarning.ShouldStop);
        Assert.True(afterError.ShouldStop);
    }

    [Fact]
    public void CollectAll_NeverStops() {
        var context = new ValidationContext(new ValidationErrorCollector());

        Assert.False(context.Report("a", "blocked", "x").ShouldStop);
    }

    // -- Reset keeps the mode, as it keeps PathMode and Services ---------------------------------

    [Fact]
    public void Reset_KeepsTheStopMode() {
        var collector = new ValidationErrorCollector { StopMode = ValidationStopMode.StopOnFirstError };

        new ValidationContext(collector).Report("a", "blocked", "x");
        collector.Reset();

        Assert.Equal(ValidationStopMode.StopOnFirstError, collector.StopMode);
    }

    // -- the entry point -------------------------------------------------------------------------

    [Fact]
    public void ValidateFirst_ReturnsAtMostOneError() {
        var result = Validator().ValidateFirst(new Order());

        Assert.Single(result.Errors);
    }

    [Fact]
    public void ValidateFirst_OnAValidValue_IsValid() {
        var order = new Order {
            Reference = "ORD-1",
            Customer = "Ada",
            ShipTo = new Address { Line1 = "12 Analytical Engine Way", PostalCode = "12345" }
        };

        Assert.True(Validator().ValidateFirst(order).IsValid);
    }

    // -- ValidationFlow --------------------------------------------------------------------------

    [Fact]
    public void DefaultFlow_Continues() {
        Assert.False(default(ValidationFlow).ShouldStop);
        Assert.Equal(ValidationFlow.Continue, default);
    }

    [Fact]
    public void Stop_AndContinue_AreDistinct() {
        Assert.True(ValidationFlow.Stop.ShouldStop);
        Assert.NotEqual(ValidationFlow.Continue, ValidationFlow.Stop);
    }
}
