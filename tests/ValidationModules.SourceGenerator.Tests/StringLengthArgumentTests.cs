using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>[StringLength]</c>'s arguments read as DataAnnotations reads them: the one positional
/// argument is the maximum, and the minimum is named.
/// </summary>
/// <remarks>
/// The native attribute used to read its first positional argument as the minimum, so a model
/// moved from DataAnnotations by changing its using directive still compiled, and every
/// <c>[StringLength(50)]</c> turned from a maximum into a minimum. These tests pin the agreement
/// the way <see cref="NativeFormatConstraintTests"/> does, as equality between the two emitted
/// validators, so the two readings cannot drift apart quietly.
/// </remarks>
public class StringLengthArgumentTests
{
    [Fact]
    public void OnePositionalArgument_EmitsIdenticallyFromEitherNamespace()
    {
        var native = GeneratorHarness.Run(
            Model("ValidationModules.Constraints", "[StringLength(50)]")
        );
        var bridged = GeneratorHarness.Run(
            Model("System.ComponentModel.DataAnnotations", "[StringLength(50)]")
        );

        Assert.Empty(native.CompilationErrors);
        Assert.Empty(bridged.CompilationErrors);
        Assert.Equal(
            bridged.Sources["Sample.SignupValidator.g.cs"],
            native.Sources["Sample.SignupValidator.g.cs"]
        );
    }

    [Fact]
    public void OnePositionalArgument_IsTheMaximum()
    {
        var result = GeneratorHarness.Run(
            Model("ValidationModules.Constraints", "[StringLength(50)]")
        );

        var emitted = result.Sources["Sample.SignupValidator.g.cs"];

        Assert.Contains("value.Name.Length > 50", emitted);
        Assert.DoesNotContain("value.Name.Length < 50", emitted);
    }

    [Fact]
    public void NamedMinimum_EmitsIdenticallyToMinimumLength()
    {
        var native = GeneratorHarness.Run(
            Model("ValidationModules.Constraints", "[StringLength(50, Min = 2)]")
        );
        var bridged = GeneratorHarness.Run(
            Model("System.ComponentModel.DataAnnotations", "[StringLength(50, MinimumLength = 2)]")
        );

        Assert.Empty(native.CompilationErrors);
        Assert.Contains(
            "value.Name.Length < 2 || value.Name.Length > 50",
            native.Sources["Sample.SignupValidator.g.cs"]
        );
        Assert.Equal(
            bridged.Sources["Sample.SignupValidator.g.cs"],
            native.Sources["Sample.SignupValidator.g.cs"]
        );
    }

    /// <summary>
    /// The forms that took the minimum first are gone rather than kept, so an existing declaration
    /// fails to compile instead of going on meaning something the one-argument form no longer does.
    /// The troubleshooting page names these two errors.
    /// </summary>
    [Theory]
    [InlineData("[StringLength(3, 40)]", "CS1729")]
    [InlineData("[StringLength(min: 3)]", "CS1739")]
    public void MinimumFirstForms_DoNotCompile(string attribute, string error)
    {
        var result = GeneratorHarness.Run(Model("ValidationModules.Constraints", attribute));

        Assert.Contains(result.CompilationErrors, diagnostic => diagnostic.Id == error);
    }

    private static string Model(string vocabulary, string attribute) =>
        $$"""
            using {{vocabulary}};

            namespace Sample;

            public record Signup {
                {{attribute}} public string? Name { get; init; }
            }
            """;
}
