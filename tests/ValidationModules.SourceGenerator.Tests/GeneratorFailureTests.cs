using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// VM5002 — an unhandled exception in an emit stage must fail the build, not surface as the
/// CS8785 warning Roslyn converts it into.
/// </summary>
/// <remarks>
/// The failure mode this closes: in a class library holding only models, nothing references a
/// generated symbol, so a generator that throws produces "Build succeeded" with zero validators
/// and every model silently validates nothing. The rc1015 trial hit it through
/// <c>[ValidateNested]</c> on a <c>List&lt;List&lt;T&gt;&gt;</c>; that trigger is now VM1502, and
/// VM5002 is the backstop for the class.
/// </remarks>
public class GeneratorFailureTests
{
    [Fact]
    public void AnEmitStageThatThrows_IsAVM5002Error()
    {
        // A language pack's culture is part of its file's hint name, and a ':' in it makes AddSource
        // throw. That throw is a reachable trigger that no front end refuses, which makes it the
        // honest way to drive the backstop.
        var result = GeneratorHarness.RunWithFiles(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Required] public string? Name { get; init; }
            }
            """,
            [
                (
                    "messages.validation-messages.json",
                    """{ "culture": "fr:CA", "templates": { "required": "{field} est obligatoire." } }"""
                ),
            ]
        );

        var failure = result.Diagnostics.First(d => d.Id == "VM5002");

        Assert.Equal(DiagnosticSeverity.Error, failure.Severity);
        Assert.Contains("messages.validation-messages.json", failure.GetMessage());
    }

    [Fact]
    public void AHealthyCompilation_ReportsNoVM5002()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Required] public string? Name { get; init; }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
    }
}
