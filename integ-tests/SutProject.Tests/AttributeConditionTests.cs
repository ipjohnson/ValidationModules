using SutProject.Conditions;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// <c>When</c> and <c>Unless</c> on constraint attributes, against really-compiled generated code.
/// </summary>
public class AttributeConditionTests {

    private static readonly ClaimValidator Validator = new();

    /// <summary>Everything the conditions guard is satisfied or switched off.</summary>
    private static Claim Quiet() => new() { Reference = "R-1" };

    [Fact]
    public void ConditionFalse_SkipsTheConstraint() {
        Assert.True(Validator.Validate(Quiet()).IsValid);
    }

    [Fact]
    public void ConditionTrue_EnforcesTheConstraint() {
        var errors = Validator.Validate(Quiet() with { IsAuto = true }).Errors;

        Assert.Contains(errors, error => error.Field == "plateNumber");
        Assert.Contains(errors, error => error.Field == "policyNumber");
    }

    [Fact]
    public void Unless_IsTheNegation() {
        Assert.Contains(
            Validator.Validate(new Claim()).Errors,
            error => error.Field == "reference");

        Assert.DoesNotContain(
            Validator.Validate(new Claim { IsDraft = true }).Errors,
            error => error.Field == "reference");
    }

    /// <summary>
    /// A guarded <c>Required</c> suppresses only when it runs. With the condition false it records
    /// nothing, so the unguarded length check on the same field still reports.
    /// </summary>
    [Fact]
    public void GuardedRequiredThatDoesNotRun_SuppressesNothing() {
        var errors = Validator.Validate(Quiet() with { PolicyNumber = "X" }).Errors;

        Assert.Equal(
            ValidationCodes.StringLength,
            Assert.Single(errors, error => error.Field == "policyNumber").Code);
    }

    /// <summary>
    /// And when it does run and fails, it suppresses the rest of its field as always.
    /// </summary>
    [Fact]
    public void GuardedRequiredThatRunsAndFails_SuppressesItsField() {
        var errors = Validator.Validate(Quiet() with { IsAuto = true, PolicyNumber = null }).Errors;

        Assert.Equal(
            ValidationCodes.Required,
            Assert.Single(errors, error => error.Field == "policyNumber").Code);
    }

    [Fact]
    public void GuardedDescent_DoesNotRecurseWhenTheConditionIsFalse() {
        // A nested value that is invalid on its own terms, reached only when the discriminator says
        // this half of the model is the meaningful one.
        var claim = Quiet() with { Auto = new AutoDetail() };

        Assert.True(Validator.Validate(claim).IsValid);

        // Switching the discriminator on satisfies the other two conditions as well, so those are
        // supplied rather than left to report alongside the descent.
        var auto = claim with { IsAuto = true, PlateNumber = "AB-123", PolicyNumber = "P-1234" };

        Assert.Equal("auto.vin", Assert.Single(Validator.Validate(auto).Errors).Field);
    }

    [Fact]
    public void IsValid_AgreesWithValidateUnderConditions() {
        Assert.True(Validator.IsValid(Quiet()));
        Assert.False(Validator.IsValid(Quiet() with { IsAuto = true }));
    }

    /// <summary>
    /// The test hoisting exists for. A condition may read live static state, so evaluating it once
    /// per pass and once per naming constraint are different answers - and this fails against the
    /// naive design that tests the condition at each guarded site.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConditionIsEvaluatedExactlyOncePerPass(bool gate) {
        var validator = new CountedValidator();

        Counted.Evaluations = 0;
        validator.Validate(new Counted { Gate = gate });

        Assert.Equal(1, Counted.Evaluations);
    }

    [Fact]
    public void ConditionIsEvaluatedExactlyOncePerPass_OnTheBooleanPathToo() {
        var validator = new CountedValidator();

        Counted.Evaluations = 0;
        validator.IsValid(new Counted { Gate = false });

        Assert.Equal(1, Counted.Evaluations);
    }
}
