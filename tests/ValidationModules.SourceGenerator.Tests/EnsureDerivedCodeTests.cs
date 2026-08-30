using System.Linq;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The codes an <c>Ensure</c> derives, through the real generator rather than through the text
/// transform on its own.
/// </summary>
/// <remarks>
/// <see cref="ValidationModules.Rules.RuleText"/> is unit-tested against the runtime, but the
/// generator compiles its own copy of that file and reaches it by a different route: a syntax node
/// read off a rules class, not a string handed to a method. These assert the two agree, and that
/// what lands in the emitted <c>ctx.Report</c> call is the code the derivation promised.
/// </remarks>
public class EnsureDerivedCodeTests {

    private static string Rules(string condition) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using ValidationModules;

        namespace Sample;

        public sealed record Booking {
            public string? Guest { get; init; }
            public string? Reference { get; init; }
            public int Nights { get; init; }
            public DateOnly Start { get; init; }
            public DateOnly End { get; init; }
            public IReadOnlyList<string> Notes { get; init; } = [];
        }

        public sealed class BookingRules : IValidationRulesFor<Booking> {
            public static void Describe(ValidationRules<Booking> rules, Booking x) {
                rules.Ensure({{condition}});
            }
        }
        """;

    private static string CodeFrom(string condition) {
        var result = GeneratorHarness.Run(Rules(condition));

        Assert.Empty(result.CompilationErrors);

        // The code is the second argument of the emitted report call, and VM0092 states it. Reading
        // it off the diagnostic keeps the assertion on what an author is told, not on emitter
        // formatting that is pinned by the golden files anyway.
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0092").GetMessage();
        var opening = diagnostic.IndexOf('\'') + 1;

        return diagnostic.Substring(opening, diagnostic.IndexOf('\'', opening) - opening);
    }

    [Theory]
    [InlineData("x.Start < x.End", "start_less_than_end")]
    [InlineData("x.Start <= x.End", "start_less_than_or_equal_end")]
    [InlineData("x.Nights > 0 && x.Guest != null", "nights_greater_than_0_and_guest_is_not_null")]
    [InlineData("!(x.Nights > 0 && x.Guest != null)", "not_group_nights_greater_than_0_and_guest_is_not_null")]
    [InlineData("x.Nights * 2 <= 30", "nights_times_2_less_than_or_equal_30")]
    public void TheGeneratorDerivesTheSameCodeTheTransformDoes(string condition, string expected) {
        Assert.Equal(expected, CodeFrom(condition));
    }

    [Theory]
    [InlineData("string.IsNullOrEmpty(x.Guest)", "guest_is_null_or_empty")]
    [InlineData("!string.IsNullOrWhiteSpace(x.Guest)", "guest_is_not_null_or_blank")]
    [InlineData("x.Guest == null", "guest_is_null")]
    [InlineData("x.Guest != null", "guest_is_not_null")]
    [InlineData("x.Notes.Count == 0", "notes_is_empty")]
    [InlineData("x.Notes.Count > 0", "notes_is_not_empty")]
    public void IdiomsAreRecognizedThroughTheGenerator(string condition, string expected) {
        Assert.Equal(expected, CodeFrom(condition));
    }

    [Theory]
    [InlineData("x.Reference!.Contains(\"@\")", "reference_contains_at")]
    [InlineData("x.Reference!.Contains(\".\")", "reference_contains_dot")]
    [InlineData("x.Reference!.StartsWith(\"AB-\")", "reference_starts_with_ab_dash")]
    public void PunctuationInALiteralSurvivesTheGenerator(string condition, string expected) {
        // These two collided before punctuation was named, which is one wire code for two rules.
        Assert.Equal(expected, CodeFrom(condition));
    }

    [Fact]
    public void ALambdaParameterDoesNotReachTheCode() {
        Assert.Equal(
            CodeFrom("x.Notes.Any(n => n.Length > 2)"),
            CodeFrom("x.Notes.Any(note => note.Length > 2)"));
    }

    [Fact]
    public void TheNullForgivingOperatorDoesNotReadAsANegation() {
        Assert.Equal(CodeFrom("x.Guest!.Length > 3"), CodeFrom("x.Guest.Length > 3"));
    }

    [Fact]
    public void TheDerivedCodeIsWhatTheValidatorReports() {
        // The diagnostic and the emitted call have to agree, or the IDE would name a code the
        // client never sees.
        var result = GeneratorHarness.Run(Rules("x.Start < x.End"));

        Assert.Contains("\"start_less_than_end\"", string.Concat(result.Sources.Values));
    }

    [Fact]
    public void AnExplicitCodeStillWins() {
        var result = GeneratorHarness.Run(Rules("x.Start < x.End, code: \"date_order\""));

        Assert.Contains("\"date_order\"", string.Concat(result.Sources.Values));
        Assert.DoesNotContain("start_less_than_end", string.Concat(result.Sources.Values));
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0092");
    }
}
