using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Characterization tests for <c>[Range]</c>'s string-bounds overload, which does not work.
/// </summary>
/// <remarks>
/// <para>
/// <b>These pin a defect, not a design.</b> They assert what the generator does today so that
/// fixing it fails here and forces this file to be rewritten, rather than leaving the behaviour
/// undescribed.
/// </para>
/// <para>
/// <b>What is wrong.</b> <c>RangeAttribute</c>'s string overload exists for types with no constant
/// form — <c>decimal</c>, <c>DateTime</c>, <c>DateOnly</c>, <c>TimeSpan</c> — and its documentation
/// says the bounds are "parsed invariantly at generation time, so a malformed bound is a build
/// error rather than a runtime one". No such parsing happens. <c>NativeConstraintReader.Literal</c>
/// renders the bound as a quoted C# string and <c>ValidatorEmitter</c> drops it straight into a
/// comparison, so the emitted code reads <c>value.Born &lt; "2000-01-01"</c> — comparing a
/// <c>DateOnly</c> with a <c>string</c>, which does not compile.
/// </para>
/// <para>
/// <b>Why it has gone unnoticed.</b> The failure lands in generated code rather than in the
/// author's source, which plan §7.5 names as the worst possible place for an error to surface. The
/// declared diagnostic for it — VM0065, "Range bounds do not parse as the member's type" — is
/// released in <c>AnalyzerReleases.Unshipped.md</c> and never constructed anywhere in the product.
/// The dead descriptor and this bug are the same hole.
/// </para>
/// <para>
/// <b>The fix, when it is made.</b> Parse the bound against the member's type in the front end;
/// emit a constructor call rather than a literal (<c>new global::System.DateOnly(2000, 1, 1)</c>,
/// which is a constant expression a comparison accepts) and report VM0065 when the parse fails.
/// The example in <c>RangeAttribute</c>'s own <c>&lt;example&gt;</c> block is the acceptance case.
/// </para>
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
    public void StringBounds_EmitCodeThatDoesNotCompile(string member) {
        var result = GeneratorHarness.Run(Model(member));

        // No diagnostic warns the author, and the generated file is broken. Both halves matter:
        // the second is the symptom, the first is why it reaches the consumer.
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0065");
        Assert.NotEmpty(result.CompilationErrors);
    }

    [Fact]
    public void StringBounds_AreEmittedAsQuotedStringsRatherThanParsedValues() {
        var result = GeneratorHarness.Run(Model(
            "[Range(\"2000-01-01\", \"2100-01-01\")] public DateOnly Born { get; init; }"));

        // The precise defect: the bound reaches the comparison still quoted.
        Assert.Contains("\"2000-01-01\"", result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void StringBounds_OnAStringMemberAreCaughtByVM0003Instead() {
        // The one string-bounds case that does report something, and it reports the wrong thing:
        // [Range] on a string is unordered, so VM0003 fires before the bounds are ever considered.
        var result = GeneratorHarness.Run(Model(
            "[Range(\"a\", \"z\")] public string? Grade { get; init; }"));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0003");
    }

    [Fact]
    public void NumericBounds_AreUnaffectedAndCompile() {
        // The overloads that take real constants are fine, which is why this has stayed hidden:
        // every [Range] anyone has written in the integration projects is an int or a double.
        var result = GeneratorHarness.Run(Model("""
            [Range(0, 30)] public int Age { get; init; }
            [Range(0.0, 1.0)] public double Ratio { get; init; }
            [Range(0L, 9000000000L)] public long Ticks { get; init; }
            """));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }
}
