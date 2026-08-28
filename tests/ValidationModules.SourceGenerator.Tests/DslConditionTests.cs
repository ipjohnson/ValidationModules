using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>When</c> and <c>Unless</c> declared through the rules DSL, in both shapes: chained onto a
/// statement, and opening a block.
/// </summary>
/// <remarks>
/// Both compile to the same thing the attribute surface does - a conjunct on a test, and a local
/// hoisted once per method body - so the emitter cannot tell the three surfaces apart. That is what
/// keeps one mechanism underneath all of them.
/// </remarks>
public class DslConditionTests {

    private static GeneratorHarness.Result Run(string body) => GeneratorHarness.Run($$"""
        using ValidationModules;

        namespace Sample;

        public sealed record Claim {
            public bool IsAuto { get; init; }
            public bool IsDraft { get; init; }
            public bool IsExpedited { get; init; }
            public string? Plate { get; init; }
            public string? Reason { get; init; }
            public string? Reference { get; init; }
        }

        public sealed class ClaimRules : IValidationRulesFor<Claim> {
            public void Describe(ValidationRules<Claim> rules) {
        {{body}}
            }
        }
        """);

    private static string Validator(string body) {
        var result = Run(body);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        return result.Sources.Single(source => source.Key.Contains("ClaimValidator")).Value;
    }

    private static string Lifted(string body) =>
        Run(body).Sources.Single(source => source.Key.Contains("_Rules")).Value;

    // -- chained -------------------------------------------------------------------------------

    /// <summary>
    /// A chained <c>.When()</c> conditions every constraint its own statement declared - which is
    /// the obvious reading of the line, and the reason no <c>ApplyConditionTo</c> is needed.
    /// </summary>
    [Fact]
    public void ChainedWhen_ConditionsEveryConstraintInItsStatement() {
        var body = Validator("""
                    rules.Required(x => x.Reason).Length(2, 500).When(x => x.IsExpedited);
            """);

        Assert.Contains("var missingReason = c0 && (", body);
        Assert.Contains("c0 && !missingReason && (", body);
    }

    /// <summary>
    /// And nothing beyond it. Splitting one statement into two is how you guard less.
    /// </summary>
    [Fact]
    public void ChainedWhen_DoesNotReachPastItsOwnStatement() {
        var body = Validator("""
                    rules.Required(x => x.Reason);
                    rules.For(x => x.Reason).Length(2, 500).When(x => x.IsExpedited);
            """);

        Assert.Contains("if (missingReason && global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"reason\").ShouldStop)", body);
        Assert.Contains("c0 && !missingReason && (", body);
    }

    [Fact]
    public void ChainedUnless_BakesTheNegationIn() {
        var body = Validator("""
                    rules.Required(x => x.Reference).Unless(x => x.IsDraft);
            """);

        Assert.Contains("var c0 = !(global::Sample.ClaimRules_Rules.Cond0(value));", body);
    }

    [Fact]
    public void ChainedWhen_OnAnEnsure_GuardsThePredicate() {
        var body = Validator("""
                    rules.Ensure(x => x.Plate != null).When(x => x.IsAuto);
            """);

        Assert.Contains("c0 && (!global::Sample.ClaimRules_Rules.Rule0(value))", body);
    }

    // -- blocks --------------------------------------------------------------------------------

    [Fact]
    public void WhenBlock_ConditionsEverythingItDeclares() {
        var body = Validator("""
                    rules.When(x => x.IsAuto, () => {
                        rules.Required(x => x.Plate);
                        rules.Required(x => x.Reference);
                    });
            """);

        Assert.Contains("var c0 = global::Sample.ClaimRules_Rules.Cond0(value);", body);
        Assert.Contains("if (c0 && (string.IsNullOrWhiteSpace(value.Plate)) && global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"plate\").ShouldStop)", body);
        Assert.Contains("if (c0 && (string.IsNullOrWhiteSpace(value.Reference)) && global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"reference\").ShouldStop)", body);
    }

    /// <summary>
    /// <c>Otherwise</c> negates the block's own condition rather than taking a second predicate, so
    /// the two halves cannot drift and only one method is lifted for both.
    /// </summary>
    [Fact]
    public void Otherwise_ReusesTheBlocksLiftedMethodNegated() {
        var body = Validator("""
                    rules.When(x => x.IsAuto, () => {
                        rules.Required(x => x.Plate);
                    }).Otherwise(() => {
                        rules.Required(x => x.Reference);
                    });
            """);

        Assert.Contains("var c0 = global::Sample.ClaimRules_Rules.Cond0(value);", body);
        Assert.Contains("var c1 = !(global::Sample.ClaimRules_Rules.Cond0(value));", body);
        Assert.DoesNotContain("Cond1", body);
    }

    [Fact]
    public void UnlessBlock_IsTheNegatedForm() {
        var body = Validator("""
                    rules.Unless(x => x.IsDraft, () => {
                        rules.Required(x => x.Reference);
                    });
            """);

        Assert.Contains("var c0 = !(global::Sample.ClaimRules_Rules.Cond0(value));", body);
    }

    /// <summary>
    /// The case compositional hoisting exists for. A nested block conjoins, and the outer condition
    /// must still be evaluated exactly once - so the inner local is built from the outer local
    /// rather than by calling the outer predicate again.
    /// </summary>
    [Fact]
    public void NestedBlocks_ConjoinWithoutEvaluatingTheOuterConditionTwice() {
        var body = Validator("""
                    rules.When(x => x.IsAuto, () => {
                        rules.Required(x => x.Plate);

                        rules.When(x => x.IsExpedited, () => {
                            rules.Required(x => x.Reason);
                        });
                    });
            """);

        Assert.Contains("var c0 = global::Sample.ClaimRules_Rules.Cond0(value);", body);
        Assert.Contains("var c1 = global::Sample.ClaimRules_Rules.Cond1(value);", body);
        Assert.Contains("var c2 = c0 && c1;", body);

        // Once each, per method body. Building the conjunction out of the operands rather than out
        // of the calls is the whole point.
        var validate = Method(body, "public global::ValidationModules.ValidationFlow Validate");

        Assert.Equal(1, Occurrences(validate, "Cond0(value)"));
        Assert.Equal(1, Occurrences(validate, "Cond1(value)"));
    }

    /// <summary>
    /// A chained <c>.When()</c> written inside a block means both, not either.
    /// </summary>
    [Fact]
    public void ChainedWhenInsideABlock_ConjoinsWithTheBlock() {
        var body = Validator("""
                    rules.When(x => x.IsAuto, () => {
                        rules.Required(x => x.Reason).When(x => x.IsExpedited);
                    });
            """);

        Assert.Contains("var c2 = c0 && c1;", body);
        Assert.Contains("if (c2 && (", body);
    }

    [Fact]
    public void ABlockThatSaysOneThing_NeedsNoBraces() {
        var body = Validator("""
                    rules.When(x => x.IsAuto, () => rules.Required(x => x.Plate));
            """);

        Assert.Contains("if (c0 && (string.IsNullOrWhiteSpace(value.Plate)) && global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"plate\").ShouldStop)", body);
    }

    // -- lifting -------------------------------------------------------------------------------

    /// <summary>
    /// Conditions and <c>Ensure</c> predicates are numbered separately, so adding a condition to a
    /// rules class does not renumber methods that already appear in generated code people read.
    /// </summary>
    [Fact]
    public void ConditionsAndPredicates_AreNumberedSeparately() {
        var lifted = Lifted("""
                    rules.Ensure(x => x.Plate != null).When(x => x.IsAuto);
                    rules.Ensure(x => x.Reason != null).When(x => x.IsExpedited);
            """);

        Assert.Contains("public static bool Rule0(", lifted);
        Assert.Contains("public static bool Rule1(", lifted);
        Assert.Contains("public static bool Cond0(", lifted);
        Assert.Contains("public static bool Cond1(", lifted);
    }

    // -- diagnostics ---------------------------------------------------------------------------

    [Fact]
    public void AnEmptyConditionalBlock_IsVM0076() {
        Assert.Contains(
            Run("""
                    rules.When(x => x.IsAuto, () => { });
            """).Diagnostics,
            d => d.Id == "VM0076");
    }

    [Fact]
    public void AnEmptyOtherwise_IsVM0076() {
        Assert.Contains(
            Run("""
                    rules.When(x => x.IsAuto, () => {
                        rules.Required(x => x.Plate);
                    }).Otherwise(() => { });
            """).Diagnostics,
            d => d.Id == "VM0076");
    }

    /// <summary>
    /// <c>For</c> anchors without declaring anything, so a <c>.When()</c> straight after it guards
    /// nothing at all.
    /// </summary>
    [Fact]
    public void AChainedWhenOnAStatementThatDeclaredNothing_IsVM0077() {
        Assert.Contains(
            Run("""
                    rules.For(x => x.Reason).When(x => x.IsExpedited);
            """).Diagnostics,
            d => d.Id == "VM0077");
    }

    /// <summary>
    /// A condition is vetted by the same self-containment rule an <c>Ensure</c> predicate is, and
    /// VM0072's existing message reads correctly for it without alteration.
    /// </summary>
    [Fact]
    public void AConditionCapturingState_IsVM0072() {
        var result = GeneratorHarness.Run("""
            using ValidationModules;

            namespace Sample;

            public sealed record Claim {
                public string? Plate { get; init; }
            }

            public sealed class ClaimRules : IValidationRulesFor<Claim> {
                private bool _enabled;

                public void Describe(ValidationRules<Claim> rules) {
                    rules.Required(x => x.Plate).When(x => _enabled);
                }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0072");
    }

    /// <summary>The text of one emitted method, so a per-method claim is checked per method.</summary>
    private static string Method(string body, string signature) {
        var start = body.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not emitted");

        var end = body.IndexOf("\n    }", start, StringComparison.Ordinal);
        return end < 0 ? body[start..] : body[start..end];
    }

    /// <summary>
    /// A condition the compiler can fold is either noise or a rule that can never fire. Roslyn does
    /// the folding, so an expression that reduces to a constant is caught along with the literal.
    /// </summary>
    [Theory]
    [InlineData("rules.Required(x => x.Plate).When(x => true);", "true")]
    [InlineData("rules.Required(x => x.Plate).When(x => false);", "false")]
    [InlineData("rules.Required(x => x.Plate).Unless(x => true);", "false")]
    [InlineData("rules.Required(x => x.Plate).Unless(x => false);", "true")]
    [InlineData("rules.Required(x => x.Plate).When(x => 1 > 2);", "false")]
    public void AConstantCondition_IsVM0034(string statement, string folded) {
        var reported = Run($"            {statement}").Diagnostics.Where(d => d.Id == "VM0034").ToList();

        var message = Assert.Single(reported).GetMessage();

        Assert.Contains($"always evaluates to {folded}", message);
    }

    [Fact]
    public void AConstantConditionOnABlock_IsAlsoVM0034() {
        Assert.Contains(
            Run("""
                    rules.When(x => true, () => {
                        rules.Required(x => x.Plate);
                    });
            """).Diagnostics,
            d => d.Id == "VM0034");
    }

    [Fact]
    public void AGenuineCondition_IsNotVM0034() {
        Assert.DoesNotContain(
            Run("""
                    rules.Required(x => x.Plate).When(x => x.IsAuto);
            """).Diagnostics,
            d => d.Id == "VM0034");
    }

    /// <summary>
    /// A method group has no body to lift, and the lifted method would come out as <c>=&gt; true</c>
    /// - a condition that silently always holds. Reported rather than emitted.
    /// </summary>
    [Fact]
    public void AConditionThatIsNotALambda_IsRejectedRatherThanCompiledToTrue() {
        var result = GeneratorHarness.Run("""
            using ValidationModules;

            namespace Sample;

            public sealed record Claim {
                public bool IsAuto { get; init; }
                public string? Plate { get; init; }
            }

            public sealed class ClaimRules : IValidationRulesFor<Claim> {
                public static bool Auto(Claim value) => value.IsAuto;

                public void Describe(ValidationRules<Claim> rules) {
                    rules.Required(x => x.Plate).When(Auto);
                }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0070");

        // And nothing was emitted claiming the condition holds.
        Assert.DoesNotContain(
            result.Sources,
            source => source.Value.Contains("Cond0", StringComparison.Ordinal)
                && source.Value.Contains("=> true", StringComparison.Ordinal));
    }

    private static int Occurrences(string text, string value) {
        var count = 0;

        for (var i = text.IndexOf(value, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }
}
