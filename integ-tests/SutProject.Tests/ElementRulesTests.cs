using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// <c>Each()</c> on a collection of strings: the chained rules apply per element, with indexed
/// paths and the element constraint's own code.
/// </summary>
/// <remarks>
/// Before this route existed, constraining a <c>List&lt;string&gt;</c> meant hand-writing the
/// check <c>[StringLength]</c> already implements - a <c>for</c> loop over
/// <c>rules.Context.Report</c>, because a rule declaration inside a loop is VM0089.
/// </remarks>
public class ElementRulesTests {

    private static ValidationResult Validate(Procedure procedure) =>
        new ProcedureValidator().Validate(procedure);

    [Fact]
    public void AnElementThatBreaksTheRule_ReportsAtItsIndexWithTheConstraintCode() {
        var result = Validate(new Procedure {
            Steps = ["ok", "this step is long enough to pass"],
        });

        var error = Assert.Single(result.Errors);

        Assert.Equal("steps[0]", error.Field);
        Assert.Equal(ValidationCodes.StringLength, error.Code);
        Assert.Contains("steps[0]", error.Message);
    }

    [Fact]
    public void EveryFailingElement_ReportsItsOwnIndex() {
        var result = Validate(new Procedure {
            Steps = ["ok", "this step is long enough to pass", "no"],
        });

        Assert.Equal(["steps[0]", "steps[2]"], result.Errors.Select(e => e.Field).ToArray());
    }

    [Fact]
    public void TheCollectionRule_AndTheElementRules_ComposeInOneChain() {
        // An empty list fails the Count and has no elements to walk; the two rules are one
        // statement and one suppression unit.
        var result = Validate(new Procedure { Steps = [] });

        var error = Assert.Single(result.Errors);

        Assert.Equal("steps", error.Field);
        Assert.Equal(ValidationCodes.ArrayBounds, error.Code);
    }

    [Fact]
    public void AnArrayOfStrings_WalksByLengthRatherThanCount() {
        var result = Validate(new Procedure {
            Steps = ["a step long enough to pass"],
            Tags = ["x", "reasonable"],
        });

        var error = Assert.Single(result.Errors);

        Assert.Equal("tags[0]", error.Field);
    }

    [Fact]
    public void CleanElements_Pass() {
        var result = Validate(new Procedure {
            Steps = ["first step, long enough", "second step, also fine"],
            Tags = ["ok", "fine"],
        });

        Assert.True(result.IsValid);
    }
}
