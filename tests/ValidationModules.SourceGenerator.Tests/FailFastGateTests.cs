using ValidationModules.SourceGenerator.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>ValidationModules_FailFast</c>: on unless the project turns it off.
/// </summary>
/// <remarks>
/// The property exists because the returns are not free - 54 bytes per report site on an osx-arm64
/// Native AOT publish, 27 KB across 500 sites. ILC cannot trim them, because
/// <c>ValidationErrorCollector.StopMode</c> is a runtime field with a public setter, so a consumer
/// who never sets the mode would otherwise carry the cost with no way to decline it.
/// </remarks>
public class FailFastGateTests {

    private const string Source = """
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed record Address {
            [Required]
            public string? PostalCode { get; init; }
        }

        public sealed record Pet {
            [Required]
            public string? Name { get; init; }

            [ValidateNested]
            public Address? Home { get; init; }
        }
        """;

    private static string Emit(params (string Key, string Value)[] properties) {
        var result = GeneratorHarness.Run(Source, properties);

        Assert.Empty(result.CompilationErrors);

        return result.Sources.Single(source => source.Key.Contains("PetValidator")).Value;
    }

    [Fact]
    public void Unset_EmitsTheReturns() {
        var body = Emit();

        Assert.Contains("ctx.ReportRequired(\"name\").ShouldStop) return ValidationFlow.Stop;", body);
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("disabled")]
    [InlineData("false")]
    [InlineData("False")]
    public void TurnedOff_DiscardsTheAnswerInstead(string setting) {
        var body = Emit(("ValidationModules_FailFast", setting));

        Assert.Contains("if (string.IsNullOrWhiteSpace(value.Name)) ctx.ReportRequired(\"name\");", body);
        Assert.DoesNotContain("ShouldStop", body);
        Assert.DoesNotContain("return ValidationFlow.Stop;", body);
    }

    /// <summary>
    /// Any other value means on, mirroring how <c>ValidationModules_DataAnnotations</c> reads
    /// anything but <c>Ignore</c> as "compile". A typo therefore keeps the safer behaviour.
    /// </summary>
    [Fact]
    public void AnUnrecognizedValue_LeavesItOn() {
        var body = Emit(("ValidationModules_FailFast", "Enabled"));

        Assert.Contains("ShouldStop", body);
    }

    /// <summary>
    /// The signature is the interface's and does not move - only the body does. A per-project
    /// return type would make the assemblies incompatible with each other.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Disabled")]
    public void EitherWay_TheSignatureAndTerminalReturnAreTheSame(string? setting) {
        var body = setting is null ? Emit() : Emit(("ValidationModules_FailFast", setting));

        Assert.Contains("public ValidationFlow Validate(ref ValidationContext ctx", body);
        Assert.Contains("return ValidationFlow.Continue;", body);
    }

    [Fact]
    public void TurnedOff_TheNestedDescentAlsoDiscards() {
        var body = Emit(("ValidationModules_FailFast", "Disabled"));

        Assert.Contains("validatorsHome[vi].Validate(ref ctxHome, nestedHome);", body);
    }

    /// <summary>The boolean path never had returns to gate; it is unchanged either way.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Disabled")]
    public void IsValid_IsUnaffected(string? setting) {
        var body = setting is null ? Emit() : Emit(("ValidationModules_FailFast", setting));
        var isValid = body[body.IndexOf("public bool IsValid", System.StringComparison.Ordinal)..];

        Assert.Contains("return false;", isValid);
    }
}
