using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The diagnostics that hold a <c>Describe</c> body's two invariants, now that almost everything
/// else transcribes.
/// </summary>
/// <remarks>
/// A body is C# that is read, never run - so a statement the generator cannot carry must break the
/// build rather than transcribe into a call on the inert builder that validates nothing. The
/// blacklist is short (VM3001); the load-bearing rules are the builder flowing only where the
/// reader can follow (VM3002) and transcribed code compiling at the emission site (VM3004). Every
/// one of these is an error rather than a warning.
/// </remarks>
public class RulesClassDiagnosticsTests {

    private static string Rules(string body, string extraMembers = "") => $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using ValidationModules;

        namespace Sample;

        public sealed record Reservation {
            public string? Guest { get; init; }
            public int Nights { get; init; }
            public DateOnly Start { get; init; }
            public DateOnly End { get; init; }
            public IReadOnlyList<string>? Notes { get; init; }
            public string? Mutable { get; set; }
        }

        public sealed class ReservationRules : IValidationRulesFor<Reservation> {
        {{extraMembers}}
            public static void Describe(ValidationRules<Reservation> rules, Reservation x) {
        {{body}}
            }
        }
        """;

    /// <summary>
    /// What a body was refused for. VM3103 is advisory - it states the code an <c>Ensure</c>
    /// derived, and the rule is emitted either way - so it is not a complaint about the body and
    /// asserting its absence would be asserting the wrong thing.
    /// </summary>
    private static IEnumerable<Diagnostic> Refusals(GeneratorHarness.Result result) =>
        result.Diagnostics.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning);

    // What used to be rejected wholesale now transcribes: computation is the feature.

    [Theory]
    [InlineData("var minimum = 2; rules.Length(x.Guest, minimum, 40);")]
    [InlineData("Console.WriteLine(\"hello\");")]
    [InlineData("if (x.Nights > 7) { rules.Require(x.Notes); }")]
    [InlineData("return;")]
    [InlineData("throw new InvalidOperationException();")]
    [InlineData("var total = x.Notes?.Sum(n => n.Length) ?? 0; rules.Ensure(total <= x.Nights);")]
    public void OrdinaryStatements_Transcribe(string statement) {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        Assert.Empty(Refusals(result));
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ReporterCallsInsideALoop_Transcribe() {
        // The reporter tier is transcription, not an island - legal anywhere, loops included. The
        // per-element field is a computed string, on the author's head.
        var result = GeneratorHarness.Run(Rules("""
                    if (x.Notes is { } notes) {
                        for (var i = 0; i < notes.Count; i++) {
                            if (notes[i].Length > 100) {
                                rules.Context.Report($"notes[{i}]", "too_long", "a note is too long");
                            }
                        }
                    }
            """));

        Assert.Empty(Refusals(result));
        Assert.Empty(result.CompilationErrors);
    }

    // VM3001 - the blacklist: v1-rejected exotica and mutation of the subject.

    [Theory]
    [InlineData("goto done; done: rules.Require(x.Guest);")]
    [InlineData("try { rules.Require(x.Guest); } catch { }")]
    [InlineData("lock (string.Empty) { }")]
    [InlineData("using var reader = System.IO.File.OpenText(\"x\");")]
    [InlineData("x.Mutable = \"a\";")]
    public void BlacklistedStatement_IsVM3001(string statement) {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        var reported = result.Diagnostics.Where(d => d.Id == "VM3001").ToList();

        Assert.NotEmpty(reported);
        Assert.All(reported, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void ApplyAnywhereButTheTop_IsVM3001() {
        // Applied rules run last and unconditionally - a guarded Apply would promise a condition
        // the emitted ordering cannot honour.
        var result = GeneratorHarness.Run(Rules(
            "        if (x.Nights > 7) { rules.Apply(Checks.Audit); }",
            """
                public static class Checks {
                    public static ValidationFlow Audit(ref ValidationContext ctx, Reservation value) =>
                        ValidationFlow.Continue;
                }
            """));

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3001");
    }

    [Fact]
    public void VM3001_NamesTheRulesClassSoTheAuthorKnowsWhichBodyIsAtFault() {
        var result = GeneratorHarness.Run(Rules("        x.Mutable = \"a\";"));

        Assert.Contains("ReservationRules", Assert.Single(result.Diagnostics, d => d.Id == "VM3001").GetMessage());
    }

    [Fact]
    public void WholeDslVocabulary_IsAccepted() {
        // The complement of the blacklist, and the assertion that keeps the theories above honest.
        var result = GeneratorHarness.Run(Rules("""
                    rules.Require(x.Guest).Length(2, 40);
                    rules.Range(x.Nights, 1, 30);
                    rules.Count(x.Notes, 0, 3);
                    rules.Ensure(x.Start < x.End);
                    rules.Ensure(x.Nights <= 7 || x.Notes != null, code: "long_stay_needs_notes");
            """));

        Assert.Empty(Refusals(result));
        Assert.Empty(result.CompilationErrors);
    }

    // VM3007 - a value argument that is not a member path, so the error would have no field.

    [Theory]
    [InlineData("rules.Require(x.Guest!.Trim());")]
    [InlineData("rules.Range(x.Nights + 1, 1, 30);")]
    [InlineData("rules.Require(\"constant\");")]
    public void ValueThatIsNotAMemberPath_IsVM3007(string statement) {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM3007").Severity);
    }

    [Fact]
    public void PlainMemberPath_IsSilent() {
        var result = GeneratorHarness.Run(Rules("        rules.Require(x.Guest);"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3007");
    }

    // VM3103 - the code an Ensure derived, stated where the rule was written.

    [Fact]
    public void EnsureWithoutACode_IsVM3103NamingTheDerivedCode() {
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(x.Start < x.End);"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3103");

        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("start_less_than_end", diagnostic.GetMessage());
    }

    [Fact]
    public void EnsureWithAnExplicitCode_IsSilent() {
        // The code is already in the source, so stating it back says nothing.
        var result = GeneratorHarness.Run(
            Rules("        rules.Ensure(x.Start < x.End, code: \"date_order\");"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3103");
    }

    [Fact]
    public void EnsureIsStillEmittedAlongsideVM3103() {
        // The gate that drops a rules class counts refusals, not every diagnostic. An advisory one
        // used to take the whole class down with it.
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(x.Start < x.End);"));

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("start_less_than_end", string.Concat(result.Sources.Values));
    }

    // ValidationModules_CodeNamespace - opt-in, and only over the codes this assembly owns.

    [Fact]
    public void WithACodeNamespace_TheDerivedCodeCarriesIt() {
        var result = GeneratorHarness.Run(
            Rules("        rules.Ensure(x.Start < x.End);"),
            ("ValidationModules_CodeNamespace", "myapp"));

        Assert.Contains("myapp.start_less_than_end", string.Concat(result.Sources.Values));
    }

    [Fact]
    public void WithACodeNamespace_AnAuthoredCodeCarriesIt() {
        var result = GeneratorHarness.Run(
            Rules("        rules.Ensure(x.Start < x.End, code: \"date_order\");"),
            ("ValidationModules_CodeNamespace", "myapp"));

        Assert.Contains("\"myapp.date_order\"", string.Concat(result.Sources.Values));
    }

    [Fact]
    public void WithACodeNamespace_TheBuiltInVocabularyIsUntouched() {
        // required has to stay required in every assembly. The fixed vocabulary is what lets a
        // client switch on a code without knowing which engine produced the error, so a namespace
        // reaches only the codes this assembly invented.
        var result = GeneratorHarness.Run(
            Rules("        rules.Require(x.Guest);"),
            ("ValidationModules_CodeNamespace", "myapp"));

        Assert.DoesNotContain("myapp.", string.Concat(result.Sources.Values));
    }

    [Fact]
    public void WithACodeNamespace_AnAttributeCodeOverrideCarriesIt() {
        // The other front end. An authored Code on a constraint attribute is as much this
        // assembly's invention as an Ensure's.
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Booking {
                [Required(Code = "guest_missing")]
                public string? Guest { get; init; }
            }
            """,
            ("ValidationModules_CodeNamespace", "myapp"));

        Assert.Contains("\"myapp.guest_missing\"", string.Concat(result.Sources.Values));
    }

    [Fact]
    public void WithoutACodeNamespace_NothingIsPrefixed() {
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(x.Start < x.End);"));

        Assert.Contains("\"start_less_than_end\"", string.Concat(result.Sources.Values));
    }

    // VM3102 - an Ensure whose condition touches no property and names no field.

    [Fact]
    public void EnsureReadingNoProperty_IsVM3102() {
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(1 < 2);"));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM3102").Severity);
    }

    [Fact]
    public void EnsureWithAnExplicitField_IsSilent() {
        // field: anchors a condition that reads nothing off the subject - a fragment computing
        // over its extra parameters is the sanctioned case. The raw wire name is the author's.
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(1 < 2, field: \"nights\");"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3102");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void EnsureReadingAProperty_InfersTheFieldAndIsSilent() {
        var result = GeneratorHarness.Run(Rules("        rules.Ensure(x.Start < x.End);"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3102");
    }

    // Instance state is impossible by the language now, not by diagnostics.

    [Fact]
    public void InstanceState_IsACompilerErrorInTheRulesClassItself() {
        // Describe is static abstract, so `this` does not exist and an instance field is CS0120 in
        // the author's own file - the language enforces what a diagnostic used to police.
        var result = GeneratorHarness.Run(Rules(
            "        rules.Ensure(x.Nights <= _limit);",
            "    private readonly int _limit = 7;\n"));

        Assert.NotEmpty(result.CompilationErrors);
    }

    // VM3002 - the builder flowing where the reader cannot follow.

    [Theory]
    [InlineData("var chain = rules.Require(x.Guest);")]
    [InlineData("Helper(rules);", "    private static bool Helper(ValidationRules<Reservation> r) => true;\n")]
    [InlineData("System.Func<ValidationModules.PropertyRules<Reservation, string?>> f = () => rules.Require(x.Guest);")]
    public void BuilderInAnUnfollowableFlow_IsVM3002(string statement, string extraMembers = "") {
        var result = GeneratorHarness.Run(Rules($"        {statement}", extraMembers));

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3002");
    }

    // VM3004 - transcribed code that cannot compile in the companion file.

    [Theory]
    [InlineData("    private static bool Allowed(string? s) => true;\n",
        "if (!Allowed(x.Guest)) { rules.Context.ReportHere(\"c\", \"m\"); }")]
    [InlineData("    private static readonly int Limit = 7;\n",
        "rules.Ensure(x.Nights <= Limit);")]
    public void APrivateNonConstantMember_IsVM3004(string extraMembers, string statement) {
        var result = GeneratorHarness.Run(Rules($"        {statement}", extraMembers));

        var reported = Assert.Single(result.Diagnostics, d => d.Id == "VM3004");

        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
        Assert.Contains("internal", reported.GetMessage());
    }

    // VM3003 - islands inside scopes the reader cannot expand them in.

    [Theory]
    [InlineData("foreach (var i in new[] { 1, 2 }) { rules.Range(x.Nights, 1, i); }")]
    [InlineData("for (var i = 0; i < 3; i++) { rules.Require(x.Guest); }")]
    [InlineData("void Local() { rules.Require(x.Guest); } Local();")]
    public void IslandInALoopOrLocalFunction_IsVM3003(string statement) {
        var result = GeneratorHarness.Run(Rules($"        {statement}"));

        Assert.Contains(result.Diagnostics, d => d.Id == "VM3003");
    }

    // VM3101 - a Require that can never fail.

    [Fact]
    public void RequireOnANonNullableValueType_WithoutATypeArgument_IsVM3101Alone() {
        // The bare spelling binds through Require's object? catch-all - inference cannot unwrap
        // Nullable, and the non-nullable twin would be CS0111 against the reference-type
        // overload - so the author reads VM3101's "can never fail" as the only error on the
        // line, not a CS0452 about the wrong overload.
        var result = GeneratorHarness.Run(Rules("        rules.Require(x.Nights);"));

        Assert.Empty(result.CompilationErrors);
        Assert.Single(result.Diagnostics, d => d.Id == "VM3101");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM3001");
    }

    [Fact]
    public void RequireOnANonNullableValueType_WithAnExplicitTypeArgument_IsVM3101() {
        // Naming the type argument gets past inference - int converts to int? - so the generator
        // diagnoses the rule that can never fail.
        var result = GeneratorHarness.Run(Rules("        rules.Require<int>(x.Nights);"));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM3101").Severity);
    }
}
