using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// A generic type carrying constraints - VM1010.
/// </summary>
/// <remarks>
/// <para>
/// It cannot be generated for. The validator would have to be <c>EnvelopeValidator&lt;T&gt; :
/// IValidatorFor&lt;Envelope&lt;T&gt;&gt;</c>, and while that is emittable it cannot be registered:
/// MS.DI's open-generic registration matches <c>Foo&lt;&gt;</c> to <c>Bar&lt;&gt;</c>, and the
/// service type here has its parameter nested inside another construction. Closing it per
/// construction needs <c>MakeGenericType</c>, which plan §2 forbids outright.
/// </para>
/// <para>
/// So the choice is a diagnostic or a validator that exists but is absent from
/// <c>AddXValidators()</c> - and a nested value skipped in silence while every other constraint
/// reports is the failure mode this library goes out of its way to avoid. What shipped instead was
/// neither: a non-generic validator that referenced <c>T</c>, five CS0246 inside a generated file,
/// and nothing pointing at the cause.
/// </para>
/// </remarks>
public class GenericModelDiagnosticsTests {

    private const string Generic = """
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed record Envelope<T> {
            [Required] public string? TraceId { get; init; }
            public T? Payload { get; init; }
        }
        """;

    [Fact]
    public void GenericModel_IsVM1010() {
        var result = GeneratorHarness.Run(Generic);

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM1010").Severity);
    }

    [Fact]
    public void GenericModel_EmitsNoUncompilableValidator() {
        // The point of the diagnostic. Reporting and then emitting the broken file anyway would
        // leave the CS0246s on top of it.
        var result = GeneratorHarness.Run(Generic);

        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void VM1010_NamesTheTypeAndSaysWhatToDoInstead() {
        var message = Assert.Single(GeneratorHarness.Run(Generic).Diagnostics, d => d.Id == "VM1010").GetMessage();

        Assert.Contains("Envelope", message);
        Assert.Contains("closed", message);
    }

    [Fact]
    public void NonGenericModel_IsSilent() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Envelope {
                [Required] public string? TraceId { get; init; }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1010");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void GenericTypeCarryingNoConstraints_IsSilent() {
        // Nothing was asked of it, so there is nothing to refuse. A generic type in the compilation
        // is ordinary; only one declaring rules is a problem.
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Envelope<T> {
                public T? Payload { get; init; }
            }

            public sealed record Order {
                [Required] public string? Sku { get; init; }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1010");
        Assert.Empty(result.CompilationErrors);
    }
}
