using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// <see cref="ValidationStopMode.StopOnFirstError"/>: the rules after the first blocking failure are
/// not evaluated, rather than evaluated and discarded.
/// </summary>
/// <remarks>
/// <para>
/// The distinction is the whole feature, and it is not observable from the error list alone - a pass
/// that ran everything and reported one error looks identical. Every test that cares counts the
/// rules that actually ran, through a check with a side effect.
/// </para>
/// <para>
/// The validators here are hand-written mirrors of what the emitter writes -
/// <c>if (test &amp;&amp; ctx.ReportX(...).ShouldStop) return Stop;</c> per check - because the
/// semantics under test belong to the collector and the flow protocol, not to any one engine.
/// Real generated validators are covered by the integ-tests' own fail-fast suite.
/// </para>
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

    private sealed class AddressValidator : IValidatorFor<Address> {
        public static readonly AddressValidator Instance = new();

        public ValidationFlow Validate(ref ValidationContext context, Address value) {
            if (value.Line1 is null && context.ReportRequired("line1").ShouldStop) {
                return ValidationFlow.Stop;
            }

            if (value.PostalCode is null && context.ReportRequired("postalCode").ShouldStop) {
                return ValidationFlow.Stop;
            }

            return ValidationFlow.Continue;
        }
    }

    private sealed class OrderValidator : IValidatorFor<Order> {
        public static readonly OrderValidator Instance = new();

        public ValidationFlow Validate(ref ValidationContext context, Order value) {
            if (string.IsNullOrWhiteSpace(value.Reference) && context.ReportRequired("reference").ShouldStop) {
                return ValidationFlow.Stop;
            }

            if (string.IsNullOrWhiteSpace(value.Customer) && context.ReportRequired("customer").ShouldStop) {
                return ValidationFlow.Stop;
            }

            if (value.ShipTo is { } shipTo) {
                var nested = context.Push("shipTo");

                if (AddressValidator.Instance.Validate(ref nested, shipTo).ShouldStop) {
                    return ValidationFlow.Stop;
                }
            }

            return ValidationFlow.Continue;
        }
    }

    private static ValidationResult Run(Order order, ValidationStopMode mode) {
        var collector = new ValidationErrorCollector { StopMode = mode };

        OrderValidator.Instance.ValidateInto(collector, order);

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

        Assert.Equal("reference", Assert.Single(result.Errors).Field);
    }

    /// <summary>
    /// The one that separates this from filtering a full result: the second rule never ran.
    /// </summary>
    [Fact]
    public void StopOnFirstError_DoesNotEvaluateLaterRules() {
        _evaluated = 0;

        var collector = new ValidationErrorCollector { StopMode = ValidationStopMode.StopOnFirstError };

        CountingValidator.Instance.ValidateInto(collector, new Order());

        Assert.Equal(1, _evaluated);
    }

    [Fact]
    public void CollectAll_EvaluatesLaterRules() {
        _evaluated = 0;

        CountingValidator.Instance.Validate(new Order());

        Assert.Equal(2, _evaluated);
    }

    private sealed class CountingValidator : IValidatorFor<Order> {
        public static readonly CountingValidator Instance = new();

        public ValidationFlow Validate(ref ValidationContext context, Order value) {
            if (!(Count() && value.Reference is not null) &&
                context.Report("reference", "first", "reference is set.").ShouldStop) {
                return ValidationFlow.Stop;
            }

            if (!(Count() && value.Customer is not null) &&
                context.Report("customer", "second", "customer is set.").ShouldStop) {
                return ValidationFlow.Stop;
            }

            return ValidationFlow.Continue;
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

        Assert.Equal("shipTo.line1", Assert.Single(result.Errors).Field);
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
        var result = OrderValidator.Instance.ValidateFirst(new Order());

        Assert.Single(result.Errors);
    }

    [Fact]
    public void ValidateFirst_OnAValidValue_IsValid() {
        var order = new Order {
            Reference = "ORD-1",
            Customer = "Ada",
            ShipTo = new Address { Line1 = "12 Analytical Engine Way", PostalCode = "12345" }
        };

        Assert.True(OrderValidator.Instance.ValidateFirst(order).IsValid);
    }

    // -- a validator that cannot stop still gets the mode's answer --------------------------------

    /// <summary>
    /// The collector closes the pass at its first blocking failure, so the result does not depend on
    /// whether the validator running it propagates the flow.
    /// </summary>
    /// <remarks>
    /// This is what lets <c>ValidationModules_FailFast</c> be a size trade rather than a behaviour
    /// change: an assembly emitted without the returns evaluates every rule, and still reports one
    /// error. The same covers a hand-written rule that discards its flow, and an
    /// <see cref="IAsyncValidatorFor{T}"/>, neither of which the emitter controls.
    /// </remarks>
    [Fact]
    public void StopOnFirstError_HoldsForAValidatorThatIgnoresTheFlow() {
        var collector = new ValidationErrorCollector { StopMode = ValidationStopMode.StopOnFirstError };

        IgnoresTheFlow.Instance.ValidateInto(collector, new Order());

        Assert.Equal("a", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void CollectAll_ForTheSameValidator_ReportsAll() {
        var collector = new ValidationErrorCollector();

        IgnoresTheFlow.Instance.ValidateInto(collector, new Order());

        Assert.Equal(3, collector.ToResult().Errors.Count);
    }

    /// <summary>A warning before the failure is kept; it is not what closes the pass.</summary>
    [Fact]
    public void StopOnFirstError_KeepsAWarningRecordedBeforeTheFailure() {
        var collector = new ValidationErrorCollector { StopMode = ValidationStopMode.StopOnFirstError };
        var context = new ValidationContext(collector);

        context.Report("w", "advisory", "x", ValidationSeverity.Warning);
        context.Report("e", "blocked", "x");
        context.Report("z", "blocked", "dropped");

        Assert.Equal(["w", "e"], collector.ToResult().Errors.Select(error => error.Field));
    }

    private sealed class IgnoresTheFlow : IValidatorFor<Order> {
        public static readonly IgnoresTheFlow Instance = new();

        public ValidationFlow Validate(ref ValidationContext context, Order value) {
            context.Report("a", "blocked", "x");
            context.Report("b", "blocked", "x");
            context.Report("c", "blocked", "x");

            return ValidationFlow.Continue;
        }
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
