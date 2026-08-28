using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The <c>GeneratedCodeStyle</c> build property: Allman unless the project says otherwise.
/// </summary>
/// <remarks>
/// The property is deliberately unprefixed - DependencyModules reads the same one - so one csproj
/// line styles every generator's output. The readings must match, which is why the accepted
/// values and the silent fallback are pinned here rather than left to prose.
/// </remarks>
public class GeneratedCodeStyleTests {

    private const string Source = """
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed record Pet {
            [Required] public string? Name { get; init; }
        }
        """;

    private const string Declaration =
        "public sealed partial class PetValidator : global::ValidationModules.IValidatorFor<global::Sample.Pet>";

    private static string Validator(params (string Key, string Value)[] properties) {
        var result = GeneratorHarness.Run(Source, properties);

        Assert.Empty(result.CompilationErrors);

        return result.Sources["Sample.PetValidator.g.cs"];
    }

    [Fact]
    public void Default_IsAllman() {
        Assert.Contains($"{Declaration}\n{{", Validator());
    }

    [Theory]
    [InlineData("KAndR")]
    [InlineData("kandr")]
    [InlineData("K&R")]
    [InlineData(" k&r ")]
    public void KAndR_PutsTheBraceOnTheDeclarationLine(string value) {
        Assert.Contains($"{Declaration} {{", Validator(("GeneratedCodeStyle", value)));
    }

    /// <summary>
    /// Falling back rather than diagnosing matches DependencyModules' reading of the shared
    /// property, and the value only moves braces - a typo cannot change what the code does.
    /// </summary>
    [Fact]
    public void UnknownValue_FallsBackToAllman() {
        Assert.Contains($"{Declaration}\n{{", Validator(("GeneratedCodeStyle", "Whitesmiths")));
    }

    /// <summary>
    /// One property styles every file the generator writes, not just the validators.
    /// </summary>
    [Fact]
    public void TheRegistrationAndTheValidator_AgreeOnTheStyle() {
        var result = GeneratorHarness.Run(Source, ("GeneratedCodeStyle", "KAndR"));

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "namespace Microsoft.Extensions.DependencyInjection {",
            result.Sources["GeneratedValidatorRegistration.g.cs"]);
    }
}
