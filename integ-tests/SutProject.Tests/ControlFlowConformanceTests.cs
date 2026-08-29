using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Conditional rules written as C# control flow, run through the real generated region.
/// </summary>
/// <remarks>
/// Conditions are <c>if</c>/<c>else</c> now, evaluated where written - so what these pin is that
/// guarding neither reorders anything nor changes what a guard that did not run can suppress, and
/// that a condition's evaluation count is exactly what the body says it is.
/// </remarks>
public class ControlFlowConformanceTests {

    private static readonly IValidatorFor<Claim> Validator = new ClaimValidator();

    private static IEnumerable<string> Fields(Claim value) =>
        Validator.Validate(value).Errors.Select(error => error.Field);

    /// <summary>
    /// Nothing switched on: the expedited pair is guarded away, the draft flag switches the
    /// reference off, and the else half asks for notes.
    /// </summary>
    [Fact]
    public void ConditionsFalse_LeaveOnlyTheElseHalf() {
        Assert.Equal(
            ["reference", "notes"],
            Fields(new Claim { IsDraft = false }));
    }

    [Fact]
    public void AGuardedChain_GuardsBothOfItsConstraints() {
        // Expedited on, reason present but too short: the Length half of the same chain fires.
        var errors = Validator.Validate(new Claim {
            IsExpedited = true, Reason = "x", Reference = "R", Notes = "n",
        }).Errors;

        Assert.Equal(
            ValidationCodes.StringLength,
            Assert.Single(errors, error => error.Field == "reason").Code);
    }

    [Fact]
    public void ANegatedGuard_IsJustANegation() {
        Assert.DoesNotContain("reference", Fields(new Claim { IsDraft = true, Notes = "n" }));
        Assert.Contains("reference", Fields(new Claim { IsDraft = false, Notes = "n" }));
    }

    [Fact]
    public void IfAndElse_AreExclusive() {
        Assert.Equal(["plate"], Fields(new Claim { IsAuto = true, IsDraft = true }));
        Assert.Equal(["notes"], Fields(new Claim { IsAuto = false, IsDraft = true }));
    }

    /// <summary>
    /// A guarded <c>Require</c> that does not run records nothing, so it suppresses nothing.
    /// </summary>
    [Fact]
    public void GuardedRequireThatDoesNotRun_SuppressesNothing() {
        // Not expedited, so Reason's Require is off - and so is its Length, being the same chain.
        // Nothing on reason at all.
        Assert.DoesNotContain("reason", Fields(new Claim { IsDraft = true, Notes = "n" }));
    }

    /// <summary>
    /// And when it runs and fails, it suppresses the rest of its own chain as always.
    /// </summary>
    [Fact]
    public void GuardedRequireThatRunsAndFails_SuppressesItsChain() {
        var errors = Validator.Validate(new Claim {
            IsExpedited = true, IsDraft = true, Notes = "n", Reason = null,
        }).Errors;

        Assert.Equal(
            ValidationCodes.Required,
            Assert.Single(errors, error => error.Field == "reason").Code);
    }

    /// <summary>
    /// Guarding must not reorder anything: statements report in body order, and a condition is
    /// part of the flow rather than a change of position.
    /// </summary>
    [Fact]
    public void Ordering_IsUnchangedByGuarding() {
        Assert.Equal(
            ["reason", "reference", "plate"],
            Fields(new Claim { IsExpedited = true, IsAuto = true }));
    }

    // -- evaluated where written -----------------------------------------------------------------

    private static readonly IValidatorFor<Metered> MeteredValidator = new MeteredValidator();

    /// <summary>
    /// One <c>if</c> in the body is one evaluation per pass, however many rules the branch
    /// declares. The old surface promised this through condition hoisting; the body now simply
    /// says it.
    /// </summary>
    [Fact]
    public void ACondition_EvaluatesOncePerPassWhenWrittenOnce() {
        Metered.Evaluations = 0;

        MeteredValidator.Validate(new Metered { Gate = true });

        Assert.Equal(1, Metered.Evaluations);
    }

    [Fact]
    public void ACondition_EvaluatesEvenWhenItGuardsNothingOff() {
        Metered.Evaluations = 0;

        MeteredValidator.Validate(new Metered { Gate = false });

        Assert.Equal(1, Metered.Evaluations);
    }

    // -- allocation ------------------------------------------------------------------------------

    /// <summary>
    /// A guarded clean pass allocates what an unguarded one does, which is nothing. This is the
    /// claim the design rests on, so it is a test rather than a benchmark someone remembers
    /// running - and it now runs against the real generated region.
    /// </summary>
    [Fact]
    public void GuardedCleanPass_AllocatesNothing() {
        var validator = new ClaimValidator();
        var collector = new ValidationErrorCollector();
        var claim = new Claim { IsAuto = true, IsExpedited = true, Reason = "reason", Reference = "R", Plate = "P" };

        for (var i = 0; i < 200; i++) {
            collector.Reset();
            validator.ValidateInto(collector, claim);
        }

        // Best of several windows: tiered JIT can rejit inside a window and the rejit itself
        // allocates. A validator that genuinely allocated per call would allocate in every window.
        var best = long.MaxValue;

        for (var window = 0; window < 5; window++) {
            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 500; i++) {
                collector.Reset();
                validator.ValidateInto(collector, claim);
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, best);
    }
}
