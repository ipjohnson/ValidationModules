using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// VM0107 — an unhandled exception in an emit stage must fail the build, not surface as the
/// CS8785 warning Roslyn converts it into.
/// </summary>
/// <remarks>
/// The failure mode this closes: in a class library holding only models, nothing references a
/// generated symbol, so a generator that throws produces "Build succeeded" with zero validators
/// and every model silently validates nothing. The rc1015 trial hit it through
/// <c>[ValidateNested]</c> on a <c>List&lt;List&lt;T&gt;&gt;</c>; that trigger is now VM0106, and
/// VM0107 is the backstop for the class.
/// </remarks>
public class GeneratorFailureTests {

    [Fact]
    public void AnEmitStageThatThrows_IsAVM0107Error() {
        // A rules-class descent into a nested generic keeps its machinery - the region's
        // transcribed text owns the walk - so the constructed generic name still reaches the
        // emitter, whose TypeRef refuses it. That throw is the one remaining reachable trigger,
        // which makes it the honest way to drive the backstop.
        var result = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Section {
                [Required] public string? Name { get; init; }
            }

            public record Batch {
                public List<List<Section>> Rows { get; init; } = new();
            }

            public sealed class BatchRules : IValidationRulesFor<Batch> {
                public static void Describe(ValidationRules<Batch> rules, Batch x) {
                    rules.Each(x.Rows);
                }
            }
            """);

        var failure = result.Diagnostics.First(d => d.Id == "VM0107");

        Assert.Equal(DiagnosticSeverity.Error, failure.Severity);
        Assert.Contains("Batch", failure.GetMessage());
    }

    [Fact]
    public void AHealthyCompilation_ReportsNoVM0107() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Required] public string? Name { get; init; }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0107");
        Assert.Empty(result.CompilationErrors);
    }
}
