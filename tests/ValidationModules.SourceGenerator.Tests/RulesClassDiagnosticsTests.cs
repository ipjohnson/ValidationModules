using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The diagnostics that keep a <c>Describe</c> body a whitelisted DSL rather than general C#.
/// </summary>
/// <remarks>
/// These matter more than the constraint diagnostics do, and for a reason particular to this
/// feature: the body <i>is</i> runnable C#, and one of its two consumers actually runs it. So a
/// statement the generator cannot compile does not look like a mistake — it compiles, and it works
/// under <c>DescribedValidator&lt;T&gt;</c>. Left unreported it would produce two engines that
/// disagree, which is precisely what API-SURFACE.md §19 promises cannot happen. Every one of these
/// is therefore an error rather than a warning.
/// </remarks>
public class RulesClassDiagnosticsTests {

    private static string Rules(string body, string extraMembers = "") => $$"""
        using System;
        using System.Collections.Generic;
        using ValidationModules;

        namespace Sample;

        public sealed record Reservation {
            public string? Guest { get; init; }
            public int Nights { get; init; }
            public DateOnly Start { get; init; }
            public DateOnly End { get; init; }
            public IReadOnlyList<string>? Notes { get; init; }
        }

        public sealed class ReservationRules : IValidationRulesFor<Reservation> {
        {{extraMembers}}
            public void Describe(ValidationRules<Reservation> rules) {
        {{body}}
            }
        }
        """;

    // VM0070 — a statement that is not a rule declaration.

    [Theory]
    [InlineData("var minimum = 2;")]
    [InlineData("Console.WriteLine(\"hello\");")]
    [InlineData("if (DateTime.Now.Year > 2000) { rules.Required(x => x.Guest); }")]
    [InlineData("foreach (var i in new[] { 1, 2 }) { rules.Range(x => x.Nights, 1, i); }")]
    [InlineData("for (var i = 0; i < 3; i++) { }")]
    [InlineData("throw new InvalidOperationException();")]
    [InlineData("return;")]
    public void StatementThatIsNotARuleDeclaration_IsVM0070(string statement) {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        var reported = result.Diagnostics.Where(d => d.Id == "VM0070").ToList();

        Assert.NotEmpty(reported);
        Assert.All(reported, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void CallToSomethingOtherThanTheBuilder_IsVM0070() {
        var result = GeneratorHarness.Run(Rules(
            "        Helper();",
            "    private static void Helper() { }\n"));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0070");
    }

    [Fact]
    public void VM0070_NamesTheRulesClassSoTheAuthorKnowsWhichBodyIsAtFault() {
        var result = GeneratorHarness.Run(Rules("        var minimum = 2;"));

        Assert.Contains("ReservationRules", Assert.Single(result.Diagnostics, d => d.Id == "VM0070").GetMessage());
    }

    [Fact]
    public void WholeDslVocabulary_IsAccepted() {
        // The complement of VM0070, and the assertion that keeps the theory above honest: if the
        // whitelist were empty every test in this class would pass for the wrong reason.
        var result = GeneratorHarness.Run(Rules("""
                    rules.Required(x => x.Guest).Length(2, 40);
                    rules.Range(x => x.Nights, 1, 30);
                    rules.Count(x => x.Notes, 0, 3);
                    rules.Ensure(x => x.Start < x.End);
                    rules.Ensure(x => x.Nights <= 7 || x.Notes != null, code: "long_stay_needs_notes");
            """));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    // VM0071 — a selector that is not a property path, so the error would have no field.

    [Theory]
    [InlineData("rules.Required(x => x.Guest!.Trim());")]
    [InlineData("rules.Range(x => x.Nights + 1, 1, 30);")]
    [InlineData("rules.Required(x => \"constant\");")]
    public void SelectorThatIsNotAPropertyPath_IsVM0071(string statement) {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM0071").Severity);
    }

    [Fact]
    public void PlainPropertySelector_IsSilent() {
        var result = GeneratorHarness.Run(Rules("        rules.Required(x => x.Guest);"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0071");
    }

    // VM0075 — an Ensure whose predicate touches no property, so no field can be inferred.

    [Fact]
    public void EnsureReadingNoProperty_IsVM0075() {
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(x => true);"));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM0075").Severity);
    }

    [Fact]
    public void EnsureReadingNoProperty_StillFiresEvenWhenFieldIsGivenExplicitly() {
        // Deliberate, per RulesFrontEnd.cs:288 — a rule is emitted inside its anchored property's
        // chain so both engines agree on ordering (§4.2), and a rule belonging to no property has
        // nowhere to go. field: renames the error; it does not detach the rule.
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(x => true, field: \"nights\");"));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0075");
    }

    [Fact]
    public void VM0075_DoesNotAdviseTheOneFixThatDoesNotWork() {
        // The message used to end "pass field: explicitly", which leaves this firing and sends the
        // reader round the loop a second time. It now says what to do and what not to bother with.
        var message = Assert
            .Single(GeneratorHarness.Run(Rules("        rules.Ensure(x => true);")).Diagnostics, d => d.Id == "VM0075")
            .GetMessage();

        Assert.DoesNotContain("pass field: explicitly", message);
        Assert.Contains("does not anchor the rule", message);
    }

    [Fact]
    public void EnsureReadingNoProperty_IsWhereTheTwoEnginesDiverge() {
        // Worth stating outright, because §19 otherwise promises they agree. DescribedValidator<T>
        // accepts this input — ValidationRules.Ensure takes `field ?? Named(...)`, so an explicit
        // field means the anchor is never consulted — while the generator rejects it. The generated
        // path is the stricter of the two, which is the safe direction for a divergence to run in:
        // the build fails rather than two deployments disagreeing.
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(x => true, field: \"nights\");"));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM0075").Severity);
    }

    [Fact]
    public void EnsureReadingAProperty_InfersTheFieldAndIsSilent() {
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(x => x.Start < x.End);"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0075");
    }

    // VM0072 — a predicate that captures anything but its own parameter.

    [Fact]
    public void PredicateCapturingAnInstanceField_IsVM0072() {
        // The generator lifts a predicate into a static method and the runtime holds it as a
        // delegate. A delegate closes over the rules class instance; a static method cannot. So a
        // capture is the one construct that would genuinely compile on one path and not the other.
        var result = GeneratorHarness.Run(Rules(
            "        rules.Ensure(x => x.Nights <= _limit);",
            "    private readonly int _limit = 7;\n"));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM0072").Severity);
    }

    [Fact]
    public void PredicateCapturingAConstructorParameter_IsVM0072() {
        var source = """
            using System;
            using ValidationModules;

            namespace Sample;

            public sealed record Reservation {
                public int Nights { get; init; }
            }

            public sealed class ReservationRules : IValidationRulesFor<Reservation> {
                private readonly int _limit;

                public ReservationRules(int limit) {
                    _limit = limit;
                }

                public void Describe(ValidationRules<Reservation> rules) {
                    rules.Ensure(x => x.Nights <= _limit, field: "nights");
                }
            }
            """;

        var result = GeneratorHarness.Run(source);

        Assert.Single(result.Diagnostics, d => d.Id == "VM0072");
    }

    [Fact]
    public void PredicateReadingAConstant_IsSilent() {
        // Static and constant state is reachable from a lifted static method, so it is allowed.
        var result = GeneratorHarness.Run(Rules(
            "        rules.Ensure(x => x.Nights <= Limit);",
            "    private const int Limit = 7;\n"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0072");
    }

    [Fact]
    public void PredicateReadingAStaticField_IsSilent() {
        var result = GeneratorHarness.Run(Rules(
            "        rules.Ensure(x => x.Nights <= Limit);",
            "    private static readonly int Limit = 7;\n"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0072");
    }

    [Fact]
    public void PredicateReadingOnlyItsOwnParameter_IsSilent() {
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(x => x.Start < x.End);"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0072");
    }
}
