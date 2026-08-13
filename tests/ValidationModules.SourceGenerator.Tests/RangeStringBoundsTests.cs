using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>[Range]</c>'s string-bounds overload, for the types with no constant form in metadata.
/// </summary>
/// <remarks>
/// The bound is parsed against the member's own type at generation time and emitted as a
/// constructor call, which is what <c>RangeAttribute</c>'s documentation has always promised. A
/// bound that does not parse is VM0065 at the declaration, rather than a comparison between a
/// <c>DateOnly</c> and a <c>string</c> inside a generated file.
/// </remarks>
public class RangeStringBoundsTests {

    private static string Model(string member) => $$"""
        using System;
        using ValidationModules.Constraints;

        namespace Sample;

        public record Pet {
        {{member}}
        }
        """;

    [Theory]
    [InlineData("[Range(\"2000-01-01\", \"2100-01-01\")] public DateOnly Born { get; init; }")]
    [InlineData("[Range(\"2000-01-01\", \"2100-01-01\")] public DateTime Effective { get; init; }")]
    [InlineData("[Range(\"0.00\", \"9.99\")] public decimal Price { get; init; }")]
    [InlineData("[Range(\"00:00:00\", \"23:59:59\")] public TimeSpan Window { get; init; }")]
    [InlineData("[Range(\"00:00:00\", \"23:59:59\")] public TimeOnly OpensAt { get; init; }")]
    [InlineData("[Range(\"2000-01-01T00:00:00+00:00\", \"2100-01-01T00:00:00+00:00\")] public DateTimeOffset At { get; init; }")]
    [InlineData("[Range(\"2000-01-01\", \"2100-01-01\")] public DateOnly? MaybeBorn { get; init; }")]
    [InlineData("[Range(\"0.00\", \"9.99\")] public decimal? MaybePrice { get; init; }")]
    public void StringBounds_ParseAndCompile(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void DateOnlyBounds_BecomeConstructorCalls() {
        var result = GeneratorHarness.Run(Model(
            "[Range(\"2000-01-15\", \"2100-12-31\")] public DateOnly Born { get; init; }"));

        var emitted = result.Sources["Sample.PetValidator.g.cs"];

        Assert.Contains("new global::System.DateOnly(2000, 1, 15)", emitted);
        Assert.Contains("new global::System.DateOnly(2100, 12, 31)", emitted);
        Assert.DoesNotContain("\"2000-01-15\"", emitted);
    }

    [Fact]
    public void DecimalBounds_CarryTheSuffixSoTheyDoNotBecomeDoubles() {
        var result = GeneratorHarness.Run(Model(
            "[Range(\"0.00\", \"9.99\")] public decimal Price { get; init; }"));

        Assert.Contains("9.99m", result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void TimeSpanBounds_KeepDaysAndMilliseconds() {
        var result = GeneratorHarness.Run(Model(
            "[Range(\"1.02:03:04.005\", \"9.00:00:00\")] public TimeSpan Window { get; init; }"));

        Assert.Contains("new global::System.TimeSpan(1, 2, 3, 4, 5)", result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void DateTimeBounds_AreUnspecifiedRatherThanTheBuildMachinesZone() {
        // A bound written "2000-01-01" carries no zone. Anchoring it to whatever the build machine
        // happened to be in would make the same source mean two things.
        var result = GeneratorHarness.Run(Model(
            "[Range(\"2000-01-01\", \"2100-01-01\")] public DateTime Effective { get; init; }"));

        Assert.Contains("global::System.DateTimeKind.Unspecified", result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void StringBounds_AppearInTheMessageAsWellAsTheComparison() {
        // Both sites take the same expression, so the message cannot disagree with the check.
        var result = GeneratorHarness.Run(Model(
            "[Range(\"2000-01-01\", \"2100-01-01\")] public DateOnly Born { get; init; }"));

        var emitted = result.Sources["Sample.PetValidator.g.cs"];
        var occurrences = emitted.Split(["new global::System.DateOnly(2000, 1, 1)"], StringSplitOptions.None).Length - 1;

        Assert.Equal(2, occurrences);
    }

    [Theory]
    [InlineData("[Range(\"not-a-date\", \"2100-01-01\")] public DateOnly Born { get; init; }")]
    [InlineData("[Range(\"2000-01-01\", \"also-not\")] public DateOnly Born { get; init; }")]
    [InlineData("[Range(\"abc\", \"def\")] public decimal Price { get; init; }")]
    [InlineData("[Range(\"2000-01-01\", \"2100-01-01\")] public int Age { get; init; }")]
    [InlineData("[Range(\"25:00:00\", \"26:00:00\")] public TimeOnly OpensAt { get; init; }")]
    public void BoundsThatDoNotParse_AreVM0065(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0065");
    }

    [Fact]
    public void VM0065_NamesTheMemberAndTheTypeItWouldNotParseAs() {
        var result = GeneratorHarness.Run(Model(
            "[Range(\"not-a-date\", \"2100-01-01\")] public DateOnly Born { get; init; }"));

        var message = Assert.Single(result.Diagnostics, d => d.Id == "VM0065").GetMessage();

        Assert.Contains("Born", message);
        Assert.Contains("DateOnly", message);
    }

    [Fact]
    public void BoundsThatDoNotParse_DropTheConstraintRatherThanEmitBrokenCode() {
        // The build fails on VM0065 alone, not also on a comparison the compiler cannot make.
        var result = GeneratorHarness.Run(Model("""
            [Required]
            public string? Name { get; init; }

            [Range("not-a-date", "2100-01-01")]
            public DateOnly Born { get; init; }
            """));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0065");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("ctx.AddRequired(\"name\")", result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void StringBoundsOnAStringMember_AreVM0003AndNotAlsoVM0065() {
        // [Range] on a string is unordered, and saying so twice would be worse than saying it once.
        var result = GeneratorHarness.Run(Model(
            "[Range(\"a\", \"z\")] public string? Grade { get; init; }"));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0003");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0065");
    }

    [Fact]
    public void NumericBounds_AreUnaffected() {
        var result = GeneratorHarness.Run(Model("""
            [Range(0, 30)] public int Age { get; init; }
            [Range(0.0, 1.0)] public double Ratio { get; init; }
            [Range(0L, 9000000000L)] public long Ticks { get; init; }
            """));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void IntBoundsOnADecimal_StillWorkWithoutBeingRewritten() {
        // The int literal already has a type the comparison accepts, so nothing needs resolving.
        var result = GeneratorHarness.Run(Model("[Range(0, 30)] public decimal Price { get; init; }"));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ExclusiveBoundsSurviveTheRewrite() {
        var result = GeneratorHarness.Run(Model(
            "[Range(\"2000-01-01\", \"2100-01-01\", ExclusiveMax = true)] public DateOnly Born { get; init; }"));

        Assert.Contains(">= new global::System.DateOnly(2100, 1, 1)", result.Sources["Sample.PetValidator.g.cs"]);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void DataAnnotationsTypedRangeOverload_IsResolvedToo() {
        // [Range(typeof(DateTime), "…", "…")] is the DataAnnotations spelling of the same thing,
        // and reaches the emitter through the same bounds.
        var result = GeneratorHarness.Run("""
            using System;
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public class Customer {
                [Range(typeof(DateTime), "2000-01-01", "2100-01-01")]
                public DateTime Effective { get; set; }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }
}
