using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>[ValidateNested]</c> on a nullable value type.
/// </summary>
/// <remarks>
/// The descent target is the underlying type. Reading the property's own type instead named
/// <c>System.Nullable</c>, and the validator name is built by appending <c>Validator</c> to it - so
/// the emitted file referenced <c>global::System.NullableValidator</c>, which does not exist. CS0234
/// inside generated code, with no diagnostic to say why.
/// </remarks>
public class NullableNestedTests {

    private const string Source = """
        using ValidationModules.Constraints;

        namespace Sample;

        public readonly record struct Money {
            [Required] public string? Currency { get; init; }
        }

        public sealed record Invoice {
            [ValidateNested] public Money? Total { get; init; }
        }
        """;

    [Fact]
    public void NullableStruct_Compiles() {
        var result = GeneratorHarness.Run(Source);

        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void NullableStruct_DescendsThroughTheUnderlyingTypesValidator() {
        var result = GeneratorHarness.Run(Source);

        var emitted = result.Sources["Sample.InvoiceValidator.g.cs"];

        Assert.Contains("IValidatorFor<global::Sample.Money>", emitted);
        Assert.DoesNotContain("NullableValidator", emitted);
    }

    [Fact]
    public void NullableStruct_RaisesNoDiagnostic() {
        // It is a supported shape, not a rejected one - the value types and nullable value types
        // already covered elsewhere would be odd to accept everywhere but here.
        Assert.Empty(GeneratorHarness.Run(Source).Diagnostics);
    }

    [Fact]
    public void NonNullableStruct_IsUnaffected() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public readonly record struct Money {
                [Required] public string? Currency { get; init; }
            }

            public sealed record Invoice {
                [ValidateNested] public Money Total { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("IValidatorFor<global::Sample.Money>", result.Sources["Sample.InvoiceValidator.g.cs"]);
    }
}
