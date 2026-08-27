using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Conditional rules declared through the DSL, run under both engines of API-SURFACE.md §19 and
/// compared error for error.
/// </summary>
/// <remarks>
/// <para>
/// The substitutability promise is what conditions put most at risk, so it gets the most tests. A
/// condition may read live static state, which makes "evaluated once per pass" a semantic
/// commitment rather than an optimization: an engine testing the condition at each guarded rule and
/// one testing it once up front return different answers, not the same answer twice.
/// </para>
/// <para>
/// Full sequences are compared rather than sets, because §4.2 pins ordering too and guarding must
/// not perturb it.
/// </para>
/// </remarks>
public class DslConditionConformanceTests {

    private static readonly IValidatorFor<Claim> Generated = new ClaimValidator();
    private static readonly IValidatorFor<Claim> Described = new DescribedValidator<Claim>(new ClaimRules());

    public static TheoryData<IValidatorFor<Claim>> BothEngines => new() { Generated, Described };

    private static IEnumerable<string> Fields(IValidatorFor<Claim> validator, Claim value) =>
        validator.Validate(value).Errors.Select(error => error.Field);

    /// <summary>
    /// Nothing switched on: the expedited pair is unguarded away, the draft flag switches the
    /// reference off, and the Otherwise half asks for notes.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothEngines))]
    public void ConditionsFalse_LeaveOnlyTheOtherwiseHalf(IValidatorFor<Claim> validator) {
        Assert.Equal(
            ["reference", "notes"],
            Fields(validator, new Claim { IsDraft = false }));
    }

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void ChainedWhen_GuardsBothConstraintsOfItsStatement(IValidatorFor<Claim> validator) {
        // Expedited on, reason present but too short: the Length half of the same statement fires.
        var errors = validator.Validate(new Claim {
            IsExpedited = true, Reason = "x", Reference = "R", Notes = "n",
        }).Errors;

        Assert.Equal(
            ValidationCodes.StringLength,
            Assert.Single(errors, error => error.Field == "reason").Code);
    }

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void ChainedUnless_IsTheNegation(IValidatorFor<Claim> validator) {
        Assert.DoesNotContain("reference", Fields(validator, new Claim { IsDraft = true, Notes = "n" }));
        Assert.Contains("reference", Fields(validator, new Claim { IsDraft = false, Notes = "n" }));
    }

    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Block_AndOtherwise_AreExclusive(IValidatorFor<Claim> validator) {
        Assert.Equal(["plate"], Fields(validator, new Claim { IsAuto = true, IsDraft = true }));
        Assert.Equal(["notes"], Fields(validator, new Claim { IsAuto = false, IsDraft = true }));
    }

    /// <summary>
    /// A guarded <c>Required</c> that does not run records nothing, so it suppresses nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothEngines))]
    public void GuardedRequiredThatDoesNotRun_SuppressesNothing(IValidatorFor<Claim> validator) {
        // Not expedited, so Reason's Required is off - but so is its Length, being the same
        // statement. Nothing on reason at all.
        Assert.DoesNotContain("reason", Fields(validator, new Claim { IsDraft = true, Notes = "n" }));
    }

    /// <summary>
    /// And when it runs and fails, it suppresses the rest of its own field as always.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothEngines))]
    public void GuardedRequiredThatRunsAndFails_SuppressesItsField(IValidatorFor<Claim> validator) {
        var errors = validator.Validate(new Claim {
            IsExpedited = true, IsDraft = true, Notes = "n", Reason = null,
        }).Errors;

        Assert.Equal(
            ValidationCodes.Required,
            Assert.Single(errors, error => error.Field == "reason").Code);
    }

    /// <summary>
    /// Guarding must not reorder anything: §4.2 groups by property in declaration order, and a
    /// condition is part of a test rather than a change of position.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothEngines))]
    public void Ordering_IsUnchangedByGuarding(IValidatorFor<Claim> validator) {
        Assert.Equal(
            ["reason", "reference", "plate"],
            Fields(validator, new Claim { IsExpedited = true, IsAuto = true }));
    }

    /// <summary>
    /// The two engines agree on the whole sequence, for every combination of the three flags. This
    /// is the assertion the rest of the file exists to make credible.
    /// </summary>
    [Fact]
    public void BothEngines_AgreeOnEveryCombinationOfConditions() {
        for (var mask = 0; mask < 8; mask++) {
            var claim = new Claim {
                IsAuto = (mask & 1) != 0,
                IsDraft = (mask & 2) != 0,
                IsExpedited = (mask & 4) != 0,
                Reason = mask % 3 == 0 ? null : "x",
            };

            Assert.Equal(
                Generated.Validate(claim).Errors.Select(error => (error.Field, error.Code)),
                Described.Validate(claim).Errors.Select(error => (error.Field, error.Code)));
        }
    }

    // -- once per pass ---------------------------------------------------------------------------

    private static readonly IValidatorFor<Metered> GeneratedMetered = new MeteredValidator();

    private static readonly IValidatorFor<Metered> DescribedMetered =
        new DescribedValidator<Metered>(new MeteredRules());

    public static TheoryData<IValidatorFor<Metered>> BothMeteredEngines =>
        new() { GeneratedMetered, DescribedMetered };

    /// <summary>
    /// The test §3 of the plan exists for, and the one that fails against the naive design where
    /// each guarded rule re-tests its own condition.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothMeteredEngines))]
    public void ConditionIsEvaluatedExactlyOncePerPass(IValidatorFor<Metered> validator) {
        Metered.Evaluations = 0;

        validator.Validate(new Metered { Gate = true });

        Assert.Equal(1, Metered.Evaluations);
    }

    [Theory]
    [MemberData(nameof(BothMeteredEngines))]
    public void ConditionIsEvaluatedExactlyOncePerPass_WhenItGuardsNothingOff(
        IValidatorFor<Metered> validator) {
        Metered.Evaluations = 0;

        validator.Validate(new Metered { Gate = false });

        Assert.Equal(1, Metered.Evaluations);
    }

    // -- allocation ------------------------------------------------------------------------------

    /// <summary>
    /// A guarded clean pass allocates what an unguarded one does, which is nothing. This is the
    /// claim the design rests on, so it is a test rather than a benchmark someone remembers running.
    /// </summary>
    [Fact]
    public void GuardedCleanPass_AllocatesNothing() {
        var validator = new DescribedValidator<Claim>(new ClaimRules());
        var collector = new ValidationErrorCollector();
        var claim = new Claim { IsAuto = true, IsExpedited = true, Reason = "reason", Reference = "R", Plate = "P" };

        for (var i = 0; i < 200; i++) {
            collector.Reset();
            validator.ValidateInto(collector, claim);
        }

        // Best of several windows, for the reason GeneratedValidatorTests gives: tiered JIT can
        // rejit inside a window and the rejit itself allocates. A validator that genuinely
        // allocated per call would allocate in every window.
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
