using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The default <c>Validate</c> on <c>IConstraintFor&lt;T&gt;</c>: the verdict from <c>IsValid</c>,
/// reported with the declaration's knobs when the attribute derives from the constraint base and
/// with the terse custom default when it does not. The knobs are the contract under test - they
/// are what makes <c>Code</c> and <c>Message</c> behave identically across every custom shape.
/// </summary>
public class ConstraintForTests {

    private sealed class BareEvenAttribute : Attribute, IConstraintFor<int> {
        public bool IsValid(int value) => value % 2 == 0;
    }

    private sealed class KnobbedEvenAttribute : Constraints.ValidationConstraintAttribute, IConstraintFor<int> {
        public bool IsValid(int value) => value % 2 == 0;
    }

    [Fact]
    public void DefaultValidate_PassingValue_ReportsNothingAndContinues() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);
        IConstraintFor<int> even = new BareEvenAttribute();

        var flow = even.Validate(ref context, 4, "count");

        Assert.Equal(ValidationFlow.Continue, flow);
        Assert.Empty(collector.ToResult().Errors);
    }

    [Fact]
    public void DefaultValidate_ReportsTheCustomCodeAndTerseMessage() {
        var error = Single(new BareEvenAttribute(), 3);

        Assert.Equal(ValidationCodes.Custom, error.Code);
        Assert.Equal("count is invalid.", error.Message);
        Assert.Equal("count", error.Field);
    }

    [Fact]
    public void DefaultValidate_HonoursACodeFromTheConstraintBase() {
        var error = Single(new KnobbedEvenAttribute { Code = "pair" }, 3);

        Assert.Equal("pair", error.Code);
        Assert.Equal("count is invalid.", error.Message);
    }

    [Fact]
    public void DefaultValidate_HonoursAMessageAndSubstitutesTheField() {
        // Substituted when the failure is reported rather than at generation time: the instance is
        // shared across every field it is declared on, so the message cannot be baked to one.
        var error = Single(new KnobbedEvenAttribute { Message = "{field} must come in pairs" }, 3);

        Assert.Equal(ValidationCodes.Custom, error.Code);
        Assert.Equal("count must come in pairs", error.Message);
    }

    [Fact]
    public void DefaultValidate_HonoursBothKnobsTogether() {
        var error = Single(new KnobbedEvenAttribute { Code = "pair", Message = "unpaired" }, 3);

        Assert.Equal("pair", error.Code);
        Assert.Equal("unpaired", error.Message);
    }

    private static ValidationError Single(IConstraintFor<int> constraint, int value) {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        constraint.Validate(ref context, value, "count");

        return Assert.Single(collector.ToResult().Errors);
    }
}
