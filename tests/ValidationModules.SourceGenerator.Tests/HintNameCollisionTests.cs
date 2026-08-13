using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Pins that two same-named types in different namespaces both get a validator.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn requires the hint name passed to <c>AddSource</c> to be unique per generator, and throws
/// an <see cref="ArgumentException"/> when it is not. That throw is not contained: it fails the
/// whole generator, so every validator in the assembly disappears and the build collapses into a
/// wall of CS0103s pointing at types that were supposed to be generated - none of which names the
/// duplicate.
/// </para>
/// <para>
/// Naming the file after the validator alone made that reachable with two ordinary types.
/// <c>Api.V1.Customer</c> alongside <c>Api.V2.Customer</c> is not an exotic arrangement; it is the
/// shape §6 of the plan is built around, so this is the case profiles walk straight into.
/// </para>
/// </remarks>
public class HintNameCollisionTests {

    private const string SameNameInTwoNamespaces = """
        using ValidationModules.Constraints;

        namespace Api.V1 {
            public record Customer {
                [Required] public string? Name { get; init; }
            }
        }

        namespace Api.V2 {
            public record Customer {
                [Required] public string? Name { get; init; }
                [Required] public string? Tier { get; init; }
            }
        }
        """;

    [Fact]
    public void Generate_SameTypeNameInTwoNamespaces_EmitsBothValidators() {
        var result = GeneratorHarness.Run(SameNameInTwoNamespaces);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Api.V1.CustomerValidator.g.cs", result.Sources.Keys);
        Assert.Contains("Api.V2.CustomerValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void Generate_SameTypeNameInTwoNamespaces_KeepsEachValidatorInItsOwnNamespace() {
        var result = GeneratorHarness.Run(SameNameInTwoNamespaces);

        Assert.Contains("namespace Api.V1;", result.Sources["Api.V1.CustomerValidator.g.cs"]);
        Assert.Contains("namespace Api.V2;", result.Sources["Api.V2.CustomerValidator.g.cs"]);

        // The rule that differs between them, so this is not passing on the namespace header alone.
        Assert.DoesNotContain("tier", result.Sources["Api.V1.CustomerValidator.g.cs"]);
        Assert.Contains("tier", result.Sources["Api.V2.CustomerValidator.g.cs"]);
    }

    [Fact]
    public void Generate_SameTypeNameInTwoNamespaces_RegistersBoth() {
        var result = GeneratorHarness.Run(SameNameInTwoNamespaces);

        var registration = result.Sources["GeneratedValidatorRegistration.g.cs"];

        Assert.Contains("global::Api.V1.CustomerValidator.Instance", registration);
        Assert.Contains("global::Api.V2.CustomerValidator.Instance", registration);
    }

    /// <summary>
    /// The global namespace has no prefix to qualify with, so its validators keep the bare file name
    /// - and a global-namespace type must still not collide with a namespaced one of the same name.
    /// </summary>
    [Fact]
    public void Generate_GlobalNamespaceAlongsideNamespaced_EmitsBoth() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            public record Customer {
                [Required] public string? Name { get; init; }
            }

            namespace Api.V1 {
                public record Customer {
                    [Required] public string? Name { get; init; }
                }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("CustomerValidator.g.cs", result.Sources.Keys);
        Assert.Contains("Api.V1.CustomerValidator.g.cs", result.Sources.Keys);
    }
}
