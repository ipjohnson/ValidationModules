using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The straight-line <c>IsValid</c> is the one public entry point that reaches generated code
/// without a runtime method rejecting a null value first, so it rejects one itself.
/// </summary>
public class NullValueGuardTests
{
    [Fact]
    public void AClassModel_RejectsANullValueInIsValid()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class Signup {
                [Required] public string? Name { get; init; }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "global::System.ArgumentNullException.ThrowIfNull(value);",
            result.Sources["Sample.SignupValidator.g.cs"]
        );
    }

    /// <summary>
    /// A struct cannot be null, <c>value is null</c> does not compile against one, and
    /// <c>ThrowIfNull</c> would box it on every call.
    /// </summary>
    [Fact]
    public void AStructModel_HasNoGuard()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record struct Point {
                [Range(0, 10)] public int X { get; init; }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("ThrowIfNull", result.Sources["Sample.PointValidator.g.cs"]);
    }
}
