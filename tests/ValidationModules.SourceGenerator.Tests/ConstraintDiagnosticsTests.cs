using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The nonsense-pairing diagnostics from plan §5 — a constraint applied to a member whose type
/// cannot carry it.
/// </summary>
/// <remarks>
/// Each of these is a rule the emitter would otherwise have to guess at. Left unreported, the
/// generator either emits code that does not compile — landing the error in a generated file the
/// author cannot edit — or silently drops the constraint, which is worse, because the model looks
/// validated and is not. Both halves are asserted: that the diagnostic fires on the bad pairing,
/// and that it stays silent on the good one.
/// </remarks>
public class ConstraintDiagnosticsTests {

    private static string Model(string members) => $$"""
        using System;
        using System.Collections.Generic;
        using ValidationModules.Constraints;

        namespace Sample;

        public record Pet {
        {{members}}
        }
        """;

    // VM0001 — a string constraint on something that is not a string.

    [Theory]
    [InlineData("[StringLength(1, 10)] public int Age { get; init; }", "[StringLength]")]
    [InlineData("[StringLength(1, 10)] public List<string> Tags { get; init; } = new();", "[StringLength]")]
    [InlineData("[Pattern(\"^a$\")] public int Age { get; init; }", "[Pattern]")]
    public void StringConstraint_OnNonString_IsVM0001(string member, string mentioned) {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0001");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(mentioned, diagnostic.GetMessage());
    }

    [Fact]
    public void StringConstraint_OnString_IsSilent() {
        var result = GeneratorHarness.Run(Model("""
            [StringLength(1, 10)]
            [Pattern("^a$")]
            public string? Name { get; init; }
            """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0001");
    }

    // VM0002 — [ItemCount] on something with no elements.

    [Theory]
    [InlineData("[ItemCount(1, 10)] public int Age { get; init; }")]
    [InlineData("[ItemCount(1, 10)] public string? Name { get; init; }")]
    public void ItemCount_OnNonCollection_IsVM0002(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM0002").Severity);
    }

    [Fact]
    public void ItemCount_OnString_IsVM0002_BecauseAStringIsNotACollectionHere() {
        // string implements IEnumerable<char>, so the reading that makes [ItemCount] legal here is
        // available and deliberately not taken — it would turn a length check into a per-character
        // walk. TypeFacts.ElementTypeOf excludes string for exactly this.
        var result = GeneratorHarness.Run(Model("[ItemCount(1, 10)] public string? Name { get; init; }"));

        Assert.Contains("string", Assert.Single(result.Diagnostics, d => d.Id == "VM0002").GetMessage());
    }

    [Theory]
    [InlineData("[ItemCount(1, 10)] public List<string> Tags { get; init; } = new();")]
    [InlineData("[ItemCount(1, 10)] public string[] Tags { get; init; } = [];")]
    [InlineData("[ItemCount(1, 10)] public IReadOnlyList<string> Tags { get; init; } = [];")]
    public void ItemCount_OnCollection_IsSilent(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0002");
    }

    // VM0003 — [Range] on a type that does not compare.

    [Theory]
    [InlineData("[Range(0, 30)] public string? Name { get; init; }")]
    [InlineData("[Range(0, 30)] public bool Flag { get; init; }")]
    public void Range_OnUnorderedType_IsVM0003(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM0003").Severity);
    }

    [Theory]
    [InlineData("[Range(0, 30)] public int Age { get; init; }")]
    [InlineData("[Range(0, 30)] public long Ticks { get; init; }")]
    [InlineData("[Range(0.0, 1.0)] public double Ratio { get; init; }")]
    [InlineData("[Range(0, 30)] public decimal Price { get; init; }")]
    [InlineData("[Range(0, 30)] public int? Optional { get; init; }")]
    [InlineData("[Range(\"2000-01-01\", \"2100-01-01\")] public DateTime Effective { get; init; }")]
    [InlineData("[Range(\"2000-01-01\", \"2100-01-01\")] public DateOnly Day { get; init; }")]
    public void Range_OnOrderedType_IsSilent(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0003");
    }

    // VM0004 — [Required] that can never fail.

    [Fact]
    public void Required_OnNonNullableValueType_IsVM0004AndOnlyAWarning() {
        // A warning rather than an error: the declaration is harmless, just pointless. Making it an
        // error would break a build over a no-op.
        var result = GeneratorHarness.Run(Model("[Required] public int Age { get; init; }"));

        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(result.Diagnostics, d => d.Id == "VM0004").Severity);
    }

    [Theory]
    [InlineData("[Required] public string? Name { get; init; }")]
    [InlineData("[Required] public int? Age { get; init; }")]
    [InlineData("[Required] public List<string>? Tags { get; init; }")]
    public void Required_OnSomethingThatCanBeMissing_IsSilent(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0004");
    }

    // VM0006 — a pattern the regex engine will not parse.

    [Theory]
    [InlineData("[")]
    [InlineData("(unclosed")]
    [InlineData("a{2,1}")]
    [InlineData("*")]
    public void InvalidPattern_IsVM0006(string pattern) {
        var result = GeneratorHarness.Run(Model(
            $"[Pattern(\"{pattern}\")] public string? Sku {{ get; init; }}"));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM0006").Severity);
    }

    [Fact]
    public void InvalidPattern_CarriesTheParserMessage() {
        // The regex parser's own text, forwarded. Re-describing it would be a worse message than the
        // one the engine already produces.
        var result = GeneratorHarness.Run(Model("[Pattern(\"[\")] public string? Sku { get; init; }"));

        var message = Assert.Single(result.Diagnostics, d => d.Id == "VM0006").GetMessage();
        Assert.Contains("Sku", message);
        Assert.DoesNotContain("{1}", message);
    }

    [Fact]
    public void ValidPattern_IsSilent() {
        var result = GeneratorHarness.Run(Model("[Pattern(\"^[A-Z]{3}$\")] public string? Sku { get; init; }"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0006");
    }

    // VM0008 — bounds that cannot both be satisfied.

    [Theory]
    [InlineData("[StringLength(10, 1)] public string? Name { get; init; }")]
    [InlineData("[StringLength(Min = 10, Max = 1)] public string? Name { get; init; }")]
    [InlineData("[ItemCount(10, 1)] public List<string> Tags { get; init; } = new();")]
    public void InvertedBounds_IsVM0008(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM0008").Severity);
    }

    [Theory]
    [InlineData("[StringLength(1, 10)] public string? Name { get; init; }")]
    [InlineData("[StringLength(5, 5)] public string? Name { get; init; }")]
    [InlineData("[StringLength(Max = 500)] public string? Notes { get; init; }")]
    [InlineData("[StringLength(Min = 1)] public string? Name { get; init; }")]
    public void SatisfiableBounds_IsSilent(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0008");
    }

    // VM0009 — a constrained property the validator cannot read.

    [Fact]
    public void SetOnlyProperty_IsVM0009() {
        var result = GeneratorHarness.Run(Model("""
            [Required]
            public string? Name { set { } }
            """));

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Id == "VM0009").Severity);
    }

    [Fact]
    public void PrivateGetter_IsVM0009() {
        var result = GeneratorHarness.Run(Model("""
            [Required]
            public string? Name { private get; set; }
            """));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0009");
    }

    [Fact]
    public void InaccessibleProperty_IsSkippedWhileTheRestOfTheTypeIsStillEmitted() {
        // The unreadable property is dropped rather than emitted anyway, so the build fails on
        // VM0009 alone and not also on generated code that will not compile.
        var result = GeneratorHarness.Run(Model("""
            [Required]
            public string? Hidden { set { } }

            [Required]
            public string? Name { get; init; }
            """));

        Assert.Single(result.Diagnostics, d => d.Id == "VM0009");

        var emitted = result.Sources["Sample.PetValidator.g.cs"];
        Assert.Contains("\"name\"", emitted);
        Assert.DoesNotContain("Hidden", emitted);
    }

    [Fact]
    public void InternalGetter_IsReadableAndSilent() {
        // Internal is visible to the generated validator, which lands in the same assembly.
        var result = GeneratorHarness.Run(Model("""
            [Required]
            public string? Name { internal get; set; }
            """));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0009");
    }

    // VM0016 — RegexOptions.Compiled asked for where it means nothing.

    [Fact]
    public void CompiledRegexOption_IsVM0016() {
        // Carrying this over is the exact habit §2 of the plan exists to remove: under AOT,
        // RegexOptions.Compiled emits IL through Reflection.Emit. Patterns here go through
        // [GeneratedRegex], so the flag is not honoured and saying so beats ignoring it.
        var source = """
            using System.Text.RegularExpressions;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Pattern("^[A-Z]{3}$", Options = RegexOptions.Compiled)]
                public string? Sku { get; init; }
            }
            """;

        var result = GeneratorHarness.Run(source);

        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(result.Diagnostics, d => d.Id == "VM0016").Severity);
    }

    [Fact]
    public void OtherRegexOptions_AreSilent() {
        var source = """
            using System.Text.RegularExpressions;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Pattern("^[a-z]{3}$", Options = RegexOptions.IgnoreCase)]
                public string? Sku { get; init; }
            }
            """;

        var result = GeneratorHarness.Run(source);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0016");
    }

    // VM0051 — a constraint on a record parameter, which binds to the parameter and is never read.

    [Fact]
    public void ConstraintOnARecordParameter_IsVM0051() {
        // Without this the type looks entirely unconstrained: no validator is emitted, nothing is
        // registered, IValidatorFor<Pet> does not resolve, and a runner merging zero validators
        // calls every value valid. Silent in every direction, which is why it is reported before
        // any property is read rather than as part of reading one.
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([Required] string Name);
            """);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0051");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void VM0051_SuggestsTheFixAsItWouldBeTyped() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([Required] string Name);
            """);

        // "[property: Required]", not "[property: RequiredAttribute]".
        Assert.Contains("[property: Required]", Assert.Single(result.Diagnostics, d => d.Id == "VM0051").GetMessage());
    }

    [Fact]
    public void ConstraintOnARecordParameter_ReportsOncePerAttribute() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([Required][StringLength(1, 10)] string Name, [Range(0, 30)] int Age);
            """);

        Assert.Equal(3, result.Diagnostics.Count(d => d.Id == "VM0051"));
    }

    [Fact]
    public void ConstraintOnARecordParameterWithThePropertyTarget_IsReadNormallyAndIsSilent() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([property: Required] string Name);
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0051");
        Assert.Contains("ctx.AddRequired(\"name\")", result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void MixedTargets_ReportOnlyTheUntargetedOne() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([property: Required] string Name, [StringLength(1, 10)] string Tag);
            """);

        Assert.Contains("StringLength", Assert.Single(result.Diagnostics, d => d.Id == "VM0051").GetMessage());
    }

    [Fact]
    public void DataAnnotationsConstraintOnARecordParameter_IsAlsoVM0051() {
        var result = GeneratorHarness.Run("""
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public record Customer([Required] string Name);
            """);

        Assert.Single(result.Diagnostics, d => d.Id == "VM0051");
    }

    [Fact]
    public void DataAnnotationsConstraintOnARecordParameter_IsSilentWhenTheFrontEndIsOff() {
        // With the vocabulary switched off the attribute is inert wherever it sits, and VM0010 is
        // the diagnostic with that news. Reporting both would be two answers to one question.
        var result = GeneratorHarness.Run(
            """
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public record Customer([Required] string Name);
            """,
            ("ValidationModules_DataAnnotations", "Ignore"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0051");
    }

    [Fact]
    public void NonConstraintAttributeOnARecordParameter_IsSilent() {
        var result = GeneratorHarness.Run("""
            using System;
            using ValidationModules.Constraints;

            namespace Sample;

            [AttributeUsage(AttributeTargets.Parameter)]
            public sealed class NoteAttribute : Attribute { }

            public record Pet([Note] string Name) {
                [Required] public string? Tag { get; init; }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0051");
    }

    [Fact]
    public void ConstraintOnAnOrdinaryConstructorParameter_IsNotVM0051() {
        // Equally inert, but [property:] is not legal there, so this diagnostic's advice would be
        // wrong. Scoped to the primary constructor for exactly that reason.
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                public Pet([Required] string name) => Name = name;

                [Required] public string? Name { get; init; }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0051");
    }

    [Fact]
    public void ConstraintOnARecordParameter_DoesNotAlsoEmitAValidatorWithNoRules() {
        // The diagnostic is the whole output. Emitting an empty validator as well would register
        // something that validates nothing, which is the state this exists to make visible.
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([Required] string Name);
            """);

        Assert.DoesNotContain("Sample.PetValidator.g.cs", result.Sources.Keys);
    }

    // A model with no mistakes in it produces no diagnostics at all, which is the assertion that
    // keeps the ones above from passing for the wrong reason.

    [Fact]
    public void WellFormedModel_ProducesNoDiagnosticsAndCompiles() {
        var result = GeneratorHarness.Run("""
            using System;
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Sample;

            // Sealed, which is what "well formed" now means for a nested target: an unsealed one
            // leaves open what should happen to a value of a more derived type, and VM0031 asks.
            public sealed record Address {
                [Required]
                [StringLength(1, 100)]
                public string? Street { get; init; }
            }

            public record Pet {
                [Required]
                [StringLength(1, 100)]
                public string? Name { get; init; }

                [Range(0, 30)]
                public int Age { get; init; }

                [Pattern("^[A-Z]{3}$")]
                public string? Sku { get; init; }

                [AllowedValues("available", "pending", "sold")]
                public string? Status { get; init; }

                [ItemCount(1, 10)]
                public List<string> Tags { get; init; } = new();

                [ValidateNested]
                public Address? Home { get; init; }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    // VM0021 — [MultipleOf] on a member with no numeric type.

    [Theory]
    [InlineData("[MultipleOf(5)] public string? Name { get; init; }")]
    [InlineData("[MultipleOf(5)] public bool Flag { get; init; }")]
    [InlineData("[MultipleOf(5)] public DateTime Starts { get; init; }")]
    [InlineData("[MultipleOf(5)] public List<int> Sizes { get; init; } = new();")]
    public void MultipleOf_OnNonNumeric_IsVM0021(string member) {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0021");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    /// Every numeric shape, including the floating-point ones, which are checked in the decimal
    /// domain rather than refused - see <c>ConstraintChecks.IsMultipleOf</c>.
    /// </summary>
    [Theory]
    [InlineData("[MultipleOf(5)] public int Quantity { get; init; }")]
    [InlineData("[MultipleOf(5)] public long Total { get; init; }")]
    [InlineData("[MultipleOf(5)] public int? Optional { get; init; }")]
    [InlineData("[MultipleOf(\"0.05\")] public decimal Price { get; init; }")]
    [InlineData("[MultipleOf(0.05)] public decimal Rounded { get; init; }")]
    [InlineData("[MultipleOf(0.01)] public double Ratio { get; init; }")]
    [InlineData("[MultipleOf(0.01)] public float Share { get; init; }")]
    public void MultipleOf_OnNumeric_IsSilentAndCompiles(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    // VM0022 — a divisor that would divide by zero, or invert the question.

    [Theory]
    [InlineData("[MultipleOf(0)] public int Quantity { get; init; }")]
    [InlineData("[MultipleOf(-5)] public int Negative { get; init; }")]
    [InlineData("[MultipleOf(0.0)] public double Ratio { get; init; }")]
    [InlineData("[MultipleOf(\"0\")] public decimal Price { get; init; }")]
    public void MultipleOf_WithANonPositiveDivisor_IsVM0022(string member) {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0022");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

        // The point of the diagnostic: `value % 0` is CS0020 for an integral member, so the
        // constraint has to be dropped rather than emitted.
        Assert.Empty(result.CompilationErrors);
    }

    // VM0023 — a divisor with no form the member's type can be checked against.

    [Theory]
    [InlineData("[MultipleOf(\"not a number\")] public decimal Price { get; init; }")]
    [InlineData("[MultipleOf(\"2.5\")] public int Quantity { get; init; }")]
    [InlineData("[MultipleOf(2.5)] public int Whole { get; init; }")]
    public void MultipleOf_WithAnUnparseableDivisor_IsVM0023(string member) {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0023");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Empty(result.CompilationErrors);
    }

    // VM0024 — [UniqueItems] on something with no elements.

    [Theory]
    [InlineData("[UniqueItems] public int Age { get; init; }")]
    [InlineData("[UniqueItems] public string? Name { get; init; }")]
    public void UniqueItems_OnNonCollection_IsVM0024(string member) {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0024");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("[UniqueItems] public List<string> Tags { get; init; } = new();")]
    [InlineData("[UniqueItems] public int[] Sizes { get; init; } = Array.Empty<int>();")]
    [InlineData("[UniqueItems] public IEnumerable<string>? Codes { get; init; }")]
    public void UniqueItems_OnACollection_IsSilentAndCompiles(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    // VM0025 — elements with no equality of their own, which compare by reference.

    [Fact]
    public void UniqueItems_OverAClassWithNoEquality_IsVM0025() {
        var result = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Sample;

            public class Tag {
                public string? Value { get; init; }
            }

            public record Pet {
                [UniqueItems]
                public List<Tag> Tags { get; init; } = new();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0025");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Sample.Tag", diagnostic.GetMessage());
    }

    /// <summary>
    /// The four ways an element type earns value equality. None of them should warn.
    /// </summary>
    [Fact]
    public void UniqueItems_OverElementsWithEquality_IsSilent() {
        var result = GeneratorHarness.Run("""
            using System;
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Named(string Value);

            public class Explicitly : IEquatable<Explicitly> {
                public bool Equals(Explicitly? other) => true;
                public override bool Equals(object? obj) => true;
                public override int GetHashCode() => 0;
            }

            public record Pet {
                [UniqueItems] public List<string> Strings { get; init; } = new();
                [UniqueItems] public List<int> Numbers { get; init; } = new();
                [UniqueItems] public List<Named> Records { get; init; } = new();
                [UniqueItems] public List<Explicitly> Equatables { get; init; } = new();
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0025");
    }

    // VM0026 — a [Range] that declares neither bound.

    [Fact]
    public void Range_WithNoBounds_IsVM0026() {
        var result = GeneratorHarness.Run(Model("[Range] public int Age { get; init; }"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0026");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("[Range(Min = 1)] public int Age { get; init; }")]
    [InlineData("[Range(Max = 99)] public int Count { get; init; }")]
    [InlineData("[Range(1, 99)] public int Both { get; init; }")]
    public void Range_WithOneBoundOrTwo_IsSilent(string member) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// The regression VM0065 did not cover: a fractional bound written as a numeric literal against
    /// a <c>decimal</c> member. C# has no implicit double-to-decimal conversion, so the emitted
    /// comparison was CS0019 - an error inside generated code, which plan §7.5 rules out.
    /// </summary>
    [Fact]
    public void Range_WithAFractionalLiteralOnADecimal_Compiles() {
        var result = GeneratorHarness.Run(Model("[Range(0.5, 9.99)] public decimal Price { get; init; }"));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    // ---- a diagnosed constraint must not also break the build it diagnosed ------------------

    /// <summary>
    /// One mistake, one error. A constraint whose type cannot support it is dropped as well as
    /// reported: emitting it anyway produced a second failure out of generated code - <c>.Length</c>
    /// on an int, <c>&gt;</c> on a type with no ordering - naming a file the author never wrote,
    /// while the useful diagnostic named their property.
    /// </summary>
    [Theory]
    [InlineData("[StringLength(1, 10)] public int Quantity { get; init; }", "VM0001")]
    [InlineData("[Pattern(\"^a$\")] public int Quantity { get; init; }", "VM0001")]
    [InlineData("[ItemCount(1, 5)] public int Quantity { get; init; }", "VM0002")]
    [InlineData("[Range(1, 10)] public object? Thing { get; init; }", "VM0003")]
    [InlineData("[Required] public int Quantity { get; init; }", "VM0004")]
    [InlineData("[Pattern(\"([unclosed\")] public string? Name { get; init; }", "VM0006")]
    [InlineData("[MultipleOf(5)] public string? Name { get; init; }", "VM0021")]
    [InlineData("[UniqueItems] public int Quantity { get; init; }", "VM0024")]
    public void DiagnosedConstraint_DoesNotAlsoEmitUncompilableCode(string member, string diagnostic) {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Contains(result.Diagnostics, d => d.Id == diagnostic);
        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// Dropping the constraint does not drop the type. A model whose only constraint was rejected
    /// still gets a validator, so anything referencing it - a [ValidateNested] on another type -
    /// keeps compiling.
    /// </summary>
    [Fact]
    public void DiagnosedConstraint_LeavesTheRestOfTheModelIntact() {
        var result = GeneratorHarness.Run(Model("""
            [StringLength(1, 10)] public int Quantity { get; init; }

            [Required] public string? Name { get; init; }
            """));

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0001");
        Assert.Empty(result.CompilationErrors);

        var emitted = Assert.Single(result.Sources, pair => pair.Key.EndsWith("Validator.g.cs")).Value;

        Assert.Contains("AddRequired(\"name\")", emitted);
        Assert.DoesNotContain("Quantity", emitted);
    }

    [Theory]
    [InlineData("[EnumDefined] public int Quantity { get; init; }", "int")]
    [InlineData("[EnumDefined] public string? Name { get; init; }", "string")]
    public void EnumDefined_OnANonEnum_IsVM0027(string member, string mentioned) {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0027");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(mentioned, diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }
}
