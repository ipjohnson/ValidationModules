using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// An expression-bodied <c>Describe</c> is one rule without a block, which
/// <c>RulesFrontEnd</c> accepts deliberately: a rules class that says one thing should not have to
/// open a block to say it.
/// </summary>
/// <remarks>
/// <para>
/// It used to crash the generator. The arrow's expression was wrapped in a synthesized
/// <c>ExpressionStatementSyntax</c> so it could reuse the statement path, and a synthesized node
/// belongs to no syntax tree - so the first <c>GetSymbolInfo</c> against it threw
/// <c>ArgumentException("Syntax node is not within syntax tree")</c>.
/// </para>
/// <para>
/// The blast radius is what makes this worth its own file. A generator that throws produces
/// <i>nothing</i> for the compilation, so every validator in the project disappeared at once and the
/// only visible symptom was CS0246 on a missing validator type - pointing at the consumer rather
/// than at the one rules class responsible.
/// </para>
/// </remarks>
public class ExpressionBodiedRulesTests {

    private static string Rules(string describe) => $$"""
        using ValidationModules;

        namespace Sample;

        public sealed record Model {
            public string? Name { get; init; }
        }

        public sealed class ModelRules : IValidationRulesFor<Model> {
            public void Describe(ValidationRules<Model> rules){{describe}}
        }
        """;

    private const string Arrow = " => rules.Required(x => x.Name);";
    private const string Block = " { rules.Required(x => x.Name); }";

    [Fact]
    public void ArrowForm_GeneratesTheValidatorRatherThanCrashingTheGenerator() {
        var result = GeneratorHarness.Run(Rules(Arrow));

        // CS8785 is how a generator exception surfaces, and it is a warning - so a test asserting
        // only on errors would have passed while the generator produced nothing at all.
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CS8785");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains(result.Sources, source => source.Key.Contains("ModelValidator"));
    }

    /// <summary>
    /// The two spellings are one rule, so they have to emit one validator. Comparing the whole file
    /// rather than probing it for a substring is what pins that the arrow form carries the rule
    /// through the front end, and not merely that something was emitted under the right name.
    /// </summary>
    [Fact]
    public void ArrowForm_EmitsExactlyWhatTheBlockFormEmits() {
        var arrow = GeneratorHarness.Run(Rules(Arrow));
        var block = GeneratorHarness.Run(Rules(Block));

        Assert.Equal(
            block.Sources.Single(s => s.Key.Contains("ModelValidator")).Value,
            arrow.Sources.Single(s => s.Key.Contains("ModelValidator")).Value);
    }

    /// <summary>
    /// The arrow form still has to be a rule declaration. VM0070 is anchored to the expression
    /// itself here, there being no statement to anchor it to.
    /// </summary>
    [Fact]
    public void ArrowFormThatIsNotARuleDeclaration_IsStillVM0070() {
        var result = GeneratorHarness.Run(Rules(" => System.Console.WriteLine(\"hello\");"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CS8785");
        Assert.Contains(result.Diagnostics, d => d.Id == "VM0070");
    }
}
