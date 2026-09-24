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
public class ConstraintDiagnosticsTests
{
    private static string Model(string members) =>
        $$"""
            using System;
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
            {{members}}
            }
            """;

    // VM1001 — a string constraint on something that is not a string.

    [Theory]
    [InlineData("[StringLength(10, Min = 1)] public int Age { get; init; }", "[StringLength]")]
    [InlineData(
        "[StringLength(10, Min = 1)] public List<string> Tags { get; init; } = new();",
        "[StringLength]"
    )]
    [InlineData("[Pattern(\"^a$\")] public int Age { get; init; }", "[Pattern]")]
    public void StringConstraint_OnNonString_IsVM1001(string member, string mentioned)
    {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1001");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(mentioned, diagnostic.GetMessage());
    }

    [Fact]
    public void StringConstraint_OnString_IsSilent()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [StringLength(10, Min = 1)]
                [Pattern("^a$")]
                public string? Name { get; init; }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1001");
    }

    // VM1002 — [ItemCount] on something with no elements.

    [Theory]
    [InlineData("[ItemCount(1, 10)] public int Age { get; init; }")]
    [InlineData("[ItemCount(1, 10)] public string? Name { get; init; }")]
    public void ItemCount_OnNonCollection_IsVM1002(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Equal(
            DiagnosticSeverity.Error,
            Assert.Single(result.Diagnostics, d => d.Id == "VM1002").Severity
        );
    }

    /// <summary>
    /// The emitter used to read <c>.Count</c> from every collection that is not an array. On a bare
    /// sequence that bound to the LINQ method group and failed with CS0019 inside generated code.
    /// <c>ImmutableArray&lt;T&gt;</c> implements <c>Count</c> only explicitly, so it failed with
    /// CS1061. A type with neither property is counted with <c>Enumerable.Count</c>, once, in one
    /// pattern.
    /// </summary>
    [Theory]
    [InlineData(
        "[ItemCount(1, 3)] public IEnumerable<string>? Tags { get; init; }",
        "global::System.Linq.Enumerable.Count(value.Tags) is < 1 or > 3"
    )]
    [InlineData(
        "[ItemCount(min: 1)] public IEnumerable<string>? Tags { get; init; }",
        "global::System.Linq.Enumerable.Count(value.Tags) is < 1)"
    )]
    [InlineData(
        "[System.ComponentModel.DataAnnotations.MaxLength(3)] public IEnumerable<string>? Tags { get; init; }",
        "global::System.Linq.Enumerable.Count(value.Tags) is > 3)"
    )]
    [InlineData(
        "[ItemCount(1, 3)] public System.Collections.Immutable.ImmutableArray<string> Tags { get; init; }",
        "value.Tags.Length < 1 || value.Tags.Length > 3"
    )]
    public void ItemCount_WithoutAPublicCount_ReadsTheCountAnotherWay(
        string member,
        string expected
    )
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(expected, result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void ValidateNested_OnAnImmutableArray_BoundsTheLoopOnLength()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required] public string? Name { get; init; }
                [ValidateNested] public System.Collections.Immutable.ImmutableArray<Pet> Litter { get; init; }
                """
            )
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(".Length;", result.Sources["Sample.PetValidator.g.cs"]);
    }

    /// <summary>
    /// A default <c>ImmutableArray&lt;T&gt;</c> has no array behind it, so its <c>Length</c>, its
    /// enumerator and its <c>IReadOnlyList&lt;T&gt;</c> view all throw. Every read tests
    /// <c>IsDefault</c> first and passes a default value as missing, the way a reference-typed
    /// collection passes null.
    /// </summary>
    [Theory]
    [InlineData(
        "[ItemCount(1, 3)] public System.Collections.Immutable.ImmutableArray<string> Tags { get; init; }",
        "!value.Tags.IsDefault && (value.Tags.Length < 1 || value.Tags.Length > 3)"
    )]
    [InlineData(
        "[UniqueItems] public System.Collections.Immutable.ImmutableArray<string> Tags { get; init; }",
        "!value.Tags.IsDefault && !global::ValidationModules.ConstraintChecks.AllUnique(value.Tags)"
    )]
    [InlineData(
        "[System.ComponentModel.DataAnnotations.Length(1, 3)] public System.Collections.Immutable.ImmutableArray<string> Tags { get; init; }",
        "!value.Tags.IsDefault && (value.Tags.Length < 1 || value.Tags.Length > 3)"
    )]
    [InlineData(
        "[System.ComponentModel.DataAnnotations.MinLength(1)] public System.Collections.Immutable.ImmutableArray<string> Tags { get; init; }",
        "!value.Tags.IsDefault && (value.Tags.Length < 1)"
    )]
    [InlineData(
        "[System.ComponentModel.DataAnnotations.MaxLength(3)] public System.Collections.Immutable.ImmutableArray<string> Tags { get; init; }",
        "!value.Tags.IsDefault && (value.Tags.Length > 3)"
    )]
    public void ImmutableArray_DefaultValue_PassesAsMissing(string member, string expected)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(expected, result.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void ValidateNested_OnADefaultImmutableArray_SkipsTheWalk()
    {
        var result = GeneratorHarness.Run(
            """
            using System.Collections.Immutable;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Toy {
                [Required] public string? Name { get; init; }
            }

            public record Pet {
                [ValidateNested] public ImmutableArray<Toy> Toys { get; init; }
            }
            """
        );

        var emitted = result.Sources["Sample.PetValidator.g.cs"];

        // Validate and IsValid each walk the array.
        Assert.Empty(result.CompilationErrors);
        Assert.Equal(2, emitted.Split("value.Toys is { IsDefault: false } itemsToys").Length - 1);
        Assert.DoesNotContain("value.Toys is { } itemsToys", emitted);
    }

    [Theory]
    [InlineData(
        "rules.Count(x.Skus, 1, 3);",
        "!x.Skus.IsDefault && (x.Skus.Length < 1 || x.Skus.Length > 3)"
    )]
    [InlineData(
        "rules.Unique(x.Skus);",
        "!x.Skus.IsDefault && !global::ValidationModules.ConstraintChecks.AllUnique(x.Skus)"
    )]
    [InlineData("rules.Each(x.Skus).Length(1, 5);", "x.Skus is { IsDefault: false } items0")]
    [InlineData("rules.Each(x.Lines);", "x.Lines is { IsDefault: false } items0")]
    public void ImmutableArray_DefaultValue_PassesAsMissingInARulesClass(
        string statement,
        string expected
    )
    {
        var result = GeneratorHarness.Run(
            $$"""
            using System.Collections.Immutable;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Line {
                [Required] public string? Sku { get; init; }
            }

            public sealed record Order {
                public ImmutableArray<string> Skus { get; init; }
                public ImmutableArray<Line> Lines { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    {{statement}}
                }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(expected, result.Sources["Sample.OrderRules_Rules.g.cs"]);
    }

    [Fact]
    public void ItemCount_OnString_IsVM1002_BecauseAStringIsNotACollectionHere()
    {
        // string implements IEnumerable<char>, so the reading that makes [ItemCount] legal here is
        // available and deliberately not taken — it would turn a length check into a per-character
        // walk. TypeFacts.ElementTypeOf excludes string for exactly this.
        var result = GeneratorHarness.Run(
            Model("[ItemCount(1, 10)] public string? Name { get; init; }")
        );

        Assert.Contains(
            "string",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1002").GetMessage()
        );
    }

    [Theory]
    [InlineData("[ItemCount(1, 10)] public List<string> Tags { get; init; } = new();")]
    [InlineData("[ItemCount(1, 10)] public string[] Tags { get; init; } = [];")]
    [InlineData("[ItemCount(1, 10)] public IReadOnlyList<string> Tags { get; init; } = [];")]
    public void ItemCount_OnCollection_IsSilent(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1002");
    }

    // VM1003 — [Range] on a type that does not compare.

    [Theory]
    [InlineData("[Range(0, 30)] public string? Name { get; init; }")]
    [InlineData("[Range(0, 30)] public bool Flag { get; init; }")]
    public void Range_OnUnorderedType_IsVM1003(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Equal(
            DiagnosticSeverity.Error,
            Assert.Single(result.Diagnostics, d => d.Id == "VM1003").Severity
        );
    }

    [Theory]
    [InlineData("[Range(0, 30)] public int Age { get; init; }")]
    [InlineData("[Range(0, 30)] public long Ticks { get; init; }")]
    [InlineData("[Range(0.0, 1.0)] public double Ratio { get; init; }")]
    [InlineData("[Range(0, 30)] public decimal Price { get; init; }")]
    [InlineData("[Range(0, 30)] public int? Optional { get; init; }")]
    [InlineData("[Range(\"2000-01-01\", \"2100-01-01\")] public DateTime Effective { get; init; }")]
    [InlineData("[Range(\"2000-01-01\", \"2100-01-01\")] public DateOnly Day { get; init; }")]
    public void Range_OnOrderedType_IsSilent(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1003");
    }

    // VM1201 — [Required] that can never fail.

    [Fact]
    public void Required_OnNonNullableValueType_IsVM1201AndOnlyAWarning()
    {
        // A warning rather than an error: the declaration is harmless, just pointless. Making it an
        // error would break a build over a no-op.
        var result = GeneratorHarness.Run(Model("[Required] public int Age { get; init; }"));

        Assert.Equal(
            DiagnosticSeverity.Warning,
            Assert.Single(result.Diagnostics, d => d.Id == "VM1201").Severity
        );
    }

    [Theory]
    [InlineData("[Required] public string? Name { get; init; }")]
    [InlineData("[Required] public int? Age { get; init; }")]
    [InlineData("[Required] public List<string>? Tags { get; init; }")]
    public void Required_OnSomethingThatCanBeMissing_IsSilent(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1201");
    }

    // VM1106 — a pattern the regex engine will not parse.

    [Theory]
    [InlineData("[")]
    [InlineData("(unclosed")]
    [InlineData("a{2,1}")]
    [InlineData("*")]
    public void InvalidPattern_IsVM1106(string pattern)
    {
        var result = GeneratorHarness.Run(
            Model($"[Pattern(\"{pattern}\")] public string? Sku {{ get; init; }}")
        );

        Assert.Equal(
            DiagnosticSeverity.Error,
            Assert.Single(result.Diagnostics, d => d.Id == "VM1106").Severity
        );
    }

    [Fact]
    public void InvalidPattern_CarriesTheParserMessage()
    {
        // The regex parser's own text, forwarded. Re-describing it would be a worse message than the
        // one the engine already produces.
        var result = GeneratorHarness.Run(
            Model("[Pattern(\"[\")] public string? Sku { get; init; }")
        );

        var message = Assert.Single(result.Diagnostics, d => d.Id == "VM1106").GetMessage();
        Assert.Contains("Sku", message);
        Assert.DoesNotContain("{1}", message);
    }

    [Fact]
    public void ValidPattern_IsSilent()
    {
        var result = GeneratorHarness.Run(
            Model("[Pattern(\"^[A-Z]{3}$\")] public string? Sku { get; init; }")
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1106");
    }

    // VM1101 — bounds that cannot both be satisfied.

    [Theory]
    [InlineData("[StringLength(1, Min = 10)] public string? Name { get; init; }")]
    [InlineData("[StringLength(Min = 10, Max = 1)] public string? Name { get; init; }")]
    [InlineData("[ItemCount(10, 1)] public List<string> Tags { get; init; } = new();")]
    public void InvertedBounds_IsVM1101(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Equal(
            DiagnosticSeverity.Error,
            Assert.Single(result.Diagnostics, d => d.Id == "VM1101").Severity
        );
    }

    [Theory]
    [InlineData("[StringLength(10, Min = 1)] public string? Name { get; init; }")]
    [InlineData("[StringLength(5, Min = 5)] public string? Name { get; init; }")]
    [InlineData("[StringLength(Max = 500)] public string? Notes { get; init; }")]
    [InlineData("[StringLength(Min = 1)] public string? Name { get; init; }")]
    public void SatisfiableBounds_IsSilent(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1101");
    }

    /// <summary>
    /// [Range] bounds are compared as the member's own type, whether they were written as constants
    /// or as strings, and from either vocabulary.
    /// </summary>
    [Theory]
    [InlineData("[Range(10, 1)] public int Guests { get; init; }")]
    [InlineData("[Range(Min = 10L, Max = 1L)] public long Units { get; init; }")]
    [InlineData("[Range(10.5, 1.5)] public double Ratio { get; init; }")]
    [InlineData("[Range(\"10.50\", \"9.99\")] public decimal Price { get; init; }")]
    [InlineData("[Range(\"2024-12-31\", \"2024-01-01\")] public DateOnly Day { get; init; }")]
    [InlineData("[Range(\"2024-12-31\", \"2024-01-01\")] public DateTime At { get; init; }")]
    [InlineData("[Range(\"12:00:00\", \"08:00:00\")] public TimeOnly Opens { get; init; }")]
    [InlineData("[Range(\"2.00:00:00\", \"1.00:00:00\")] public TimeSpan Window { get; init; }")]
    [InlineData(
        "[Range(\"2024-01-02T00:00:00+00:00\", \"2024-01-01T00:00:00+00:00\")] public DateTimeOffset Stamp { get; init; }"
    )]
    [InlineData(
        "[System.ComponentModel.DataAnnotations.Range(10, 1)] public int Seats { get; init; }"
    )]
    [InlineData(
        "[System.ComponentModel.DataAnnotations.Range(typeof(DateTime), \"2024-12-31\", \"2024-01-01\")] public DateTime Due { get; init; }"
    )]
    [InlineData("[Range(5, 5, ExclusiveMin = true)] public int Exact { get; init; }")]
    [InlineData("[Range(5, 5, ExclusiveMax = true)] public int Exact { get; init; }")]
    public void RangeThatAdmitsNoValue_IsVM1101(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Equal(
            DiagnosticSeverity.Error,
            Assert.Single(result.Diagnostics, d => d.Id == "VM1101").Severity
        );
    }

    /// <summary>
    /// The comparison is the member's, not the text's: "9.99" is below "10.5" as a decimal, and a
    /// DateTimeOffset minimum written later in the day under a larger offset is the earlier instant.
    /// </summary>
    [Theory]
    [InlineData("[Range(1, 10)] public int Guests { get; init; }")]
    [InlineData("[Range(5, 5)] public int Exact { get; init; }")]
    [InlineData("[Range(0, 100, ExclusiveMax = true)] public int Percent { get; init; }")]
    [InlineData("[Range(Min = 18)] public int Age { get; init; }")]
    [InlineData("[Range(\"9.99\", \"10.5\")] public decimal Price { get; init; }")]
    [InlineData("[Range(1, double.PositiveInfinity)] public double Ratio { get; init; }")]
    [InlineData(
        "[Range(\"2024-01-01T00:00:00+05:00\", \"2023-12-31T20:00:00+00:00\")] public DateTimeOffset Stamp { get; init; }"
    )]
    public void RangeThatAdmitsAValue_IsSilent(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1101");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void VM1101_NamesTheBoundsAndSaysToSwapThem()
    {
        var result = GeneratorHarness.Run(Model("[Range(10, 1)] public int Guests { get; init; }"));

        Assert.Equal(
            "The bounds on 'Guests' are inverted, so the constraint can never be satisfied. The "
                + "minimum 10 exceeds the maximum 1. Swap the two bounds",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1101").GetMessage()
        );
    }

    [Fact]
    public void VM1101_OnEqualExclusiveBounds_SaysWhichBoundExcludesItself()
    {
        var result = GeneratorHarness.Run(
            Model("[Range(5, 5, ExclusiveMin = true)] public int Exact { get; init; }")
        );

        Assert.Equal(
            "The bounds on 'Exact' admit no value, so the constraint can never be satisfied. The "
                + "minimum and the maximum are both 5, and ExclusiveMin is set. Make both bounds "
                + "inclusive, or widen the range",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1101").GetMessage()
        );
    }

    // VM1007 — a constrained property the validator cannot read.

    [Fact]
    public void SetOnlyProperty_IsVM1007()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                public string? Name { set { } }
                """
            )
        );

        Assert.Equal(
            DiagnosticSeverity.Error,
            Assert.Single(result.Diagnostics, d => d.Id == "VM1007").Severity
        );
    }

    [Fact]
    public void PrivateGetter_IsVM1007()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                public string? Name { private get; set; }
                """
            )
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1007");
    }

    [Fact]
    public void InaccessibleProperty_IsSkippedWhileTheRestOfTheTypeIsStillEmitted()
    {
        // The unreadable property is dropped rather than emitted anyway, so the build fails on
        // VM1007 alone and not also on generated code that will not compile.
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                public string? Hidden { set { } }

                [Required]
                public string? Name { get; init; }
                """
            )
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1007");

        var emitted = result.Sources["Sample.PetValidator.g.cs"];
        Assert.Contains("\"name\"", emitted);
        Assert.DoesNotContain("Hidden", emitted);
    }

    [Fact]
    public void InternalGetter_IsReadableAndSilent()
    {
        // Internal is visible to the generated validator, which lands in the same assembly.
        var result = GeneratorHarness.Run(
            Model(
                """
                [Required]
                public string? Name { internal get; set; }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1007");
    }

    // VM1302 — RegexOptions.Compiled asked for where it means nothing.

    [Fact]
    public void CompiledRegexOption_IsVM1302()
    {
        // Carrying this over is the habit §2 of the plan exists to remove, because
        // RegexOptions.Compiled emits IL through Reflection.Emit. The inline form is a Regex the
        // validator constructs, so the flag is removed rather than passed on, and the diagnostic
        // says so.
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

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1302");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains(
            "point at it: [Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]",
            diagnostic.GetMessage()
        );

        // Removed, as the message says, and with nothing else set the single-argument
        // constructor applies.
        Assert.Contains(
            "new global::System.Text.RegularExpressions.Regex(\"^[A-Z]{3}$\");",
            result.Sources["Sample.PetValidator.g.cs"]
        );
    }

    [Fact]
    public void CompiledRegexOption_IsRemovedAndTheOtherOptionsKept()
    {
        var source = """
            using System.Text.RegularExpressions;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Pattern("^[a-z]{3}$", Options = RegexOptions.Compiled | RegexOptions.IgnoreCase)]
                public string? Sku { get; init; }
            }
            """;

        var emitted = GeneratorHarness.Run(source).Sources["Sample.PetValidator.g.cs"];

        // IgnoreCase is 1; with Compiled it would have been 9.
        Assert.Contains("(global::System.Text.RegularExpressions.RegexOptions)1)", emitted);
    }

    [Fact]
    public void OtherRegexOptions_AreSilent()
    {
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

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1302");
    }

    // VM1011 — a constraint on a field or a static property, which the walk never reads.

    [Fact]
    public void ConstraintOnAFieldOrAStaticProperty_IsVM1011()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class Form {
                [Required]
                public string? Field;

                [Required]
                public static string? Shared { get; set; }

                [Required]
                public string? Property { get; init; } = "set";
            }
            """
        );

        var reported = result
            .Diagnostics.Where(d => d.Id == "VM1011")
            .Select(d => d.GetMessage())
            .OrderBy(message => message, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "'Required' on 'Field' is never evaluated, because 'Field' is a field. Constraints "
                    + "apply to instance properties. Declare it as one: "
                    + "public string? Field { get; set; }",
                "'Required' on 'Shared' is never evaluated, because 'Shared' is a static property. "
                    + "Constraints apply to instance properties. Declare it as one: "
                    + "public string? Shared { get; set; }",
            ],
            reported
        );
        Assert.All(
            result.Diagnostics.Where(d => d.Id == "VM1011"),
            d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity)
        );

        // The instance property beside them is still validated.
        Assert.Contains(
            "ReportRequired(ctx, \"property\", value: value.Property)",
            result.Sources["Sample.FormValidator.g.cs"]
        );
    }

    [Fact]
    public void ConstraintOnAField_ReportsOncePerAttribute()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class Form {
                [Required, StringLength(10)]
                public readonly string? Code;
            }
            """
        );

        Assert.Equal(2, result.Diagnostics.Count(d => d.Id == "VM1011"));
        Assert.Contains(
            "public string? Code { get; }",
            result.Diagnostics.First(d => d.Id == "VM1011").GetMessage()
        );
    }

    /// <summary>
    /// A type whose only constraints sit on fields gets no validator. The warning is what tells the
    /// author, because otherwise the type looks unconstrained and nothing is registered for it.
    /// </summary>
    [Fact]
    public void ConstraintsOnlyOnFields_StillReportVM1011()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class Form {
                [Required]
                public string? Field;
            }
            """
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1011");
        Assert.DoesNotContain(result.Sources.Keys, key => key.Contains("FormValidator"));
        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// Every shape the generator would compile on a property counts: a DataAnnotations constraint,
    /// while that front end is on, and a custom constraint attribute. An attribute that is not a
    /// constraint does not.
    /// </summary>
    [Fact]
    public void EveryCompiledShapeOnAField_IsVM1011_AndOtherAttributesAreNot()
    {
        var source = """
            using System.Text.Json.Serialization;
            using ValidationModules.Constraints;
            using DA = System.ComponentModel.DataAnnotations;

            namespace Sample;

            public sealed class StartsWithAAttribute : CustomConstraintAttribute {
                public static bool IsValid(string value) => value.StartsWith("A");
            }

            public sealed class Form {
                [DA.Required]
                public string? Annotated;

                [StartsWithA]
                public string? Custom;

                [JsonPropertyName("plain"), DA.Display(Name = "Plain")]
                public string? Plain;
            }
            """;

        var compiled = GeneratorHarness.Run(source);
        var ignored = GeneratorHarness.Run(source, ("ValidationModules_DataAnnotations", "Ignore"));

        Assert.Equal(
            ["'Required' on 'Annotated'", "'StartsWithA' on 'Custom'"],
            compiled
                .Diagnostics.Where(d => d.Id == "VM1011")
                .Select(d => d.GetMessage().Substring(0, d.GetMessage().IndexOf(" is never")))
                .OrderBy(prefix => prefix, StringComparer.Ordinal)
        );
        Assert.Single(ignored.Diagnostics, d => d.Id == "VM1011");
    }

    /// <summary>
    /// A field declared on a base type is reported once, where it is declared, rather than once
    /// per type that inherits it.
    /// </summary>
    [Fact]
    public void ConstraintOnAnInheritedField_IsReportedOnceWhereItIsDeclared()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public class Base {
                [Required]
                public string? Field;
            }

            public sealed class Derived : Base {
                [Required]
                public string? Property { get; init; }
            }
            """
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1011");
    }

    // VM1008 — a constraint on a record parameter, which binds to the parameter and is never read.

    [Fact]
    public void ConstraintOnARecordParameter_IsVM1008()
    {
        // Without this the type looks entirely unconstrained: no validator is emitted, nothing is
        // registered, IValidatorFor<Pet> does not resolve, and a runner merging zero validators
        // calls every value valid. Silent in every direction, which is why it is reported before
        // any property is read rather than as part of reading one.
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([Required] string Name);
            """
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1008");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void VM1008_SuggestsTheFixAsItWouldBeTyped()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([Required] string Name);
            """
        );

        // "[property: Required]", not "[property: RequiredAttribute]".
        Assert.Contains(
            "[property: Required]",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1008").GetMessage()
        );
    }

    [Fact]
    public void ConstraintOnARecordParameter_ReportsOncePerAttribute()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([Required][StringLength(10, Min = 1)] string Name, [Range(0, 30)] int Age);
            """
        );

        Assert.Equal(3, result.Diagnostics.Count(d => d.Id == "VM1008"));
    }

    [Fact]
    public void ConstraintOnARecordParameterWithThePropertyTarget_IsReadNormallyAndIsSilent()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([property: Required] string Name);
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1008");
        Assert.Contains(
            "global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"name\", value: value.Name)",
            result.Sources["Sample.PetValidator.g.cs"]
        );
    }

    [Fact]
    public void MixedTargets_ReportOnlyTheUntargetedOne()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([property: Required] string Name, [StringLength(10, Min = 1)] string Tag);
            """
        );

        Assert.Contains(
            "StringLength",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1008").GetMessage()
        );
    }

    [Fact]
    public void DataAnnotationsConstraintOnARecordParameter_IsAlsoVM1008()
    {
        var result = GeneratorHarness.Run(
            """
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public record Customer([Required] string Name);
            """
        );

        Assert.Single(result.Diagnostics, d => d.Id == "VM1008");
    }

    [Fact]
    public void DataAnnotationsConstraintOnARecordParameter_IsSilentWhenTheFrontEndIsOff()
    {
        // With the vocabulary switched off the attribute is inert wherever it sits, and VM2001 is
        // the diagnostic with that news. Reporting both would be two answers to one question.
        var result = GeneratorHarness.Run(
            """
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public record Customer([Required] string Name);
            """,
            ("ValidationModules_DataAnnotations", "Ignore")
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1008");
    }

    [Fact]
    public void NonConstraintAttributeOnARecordParameter_IsSilent()
    {
        var result = GeneratorHarness.Run(
            """
            using System;
            using ValidationModules.Constraints;

            namespace Sample;

            [AttributeUsage(AttributeTargets.Parameter)]
            public sealed class NoteAttribute : Attribute { }

            public record Pet([Note] string Name) {
                [Required] public string? Tag { get; init; }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1008");
    }

    [Fact]
    public void ConstraintOnAnOrdinaryConstructorParameter_IsNotVM1008()
    {
        // Equally inert, but [property:] is not legal there, so this diagnostic's advice would be
        // wrong. Scoped to the primary constructor for exactly that reason.
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                public Pet([Required] string name) => Name = name;

                [Required] public string? Name { get; init; }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1008");
    }

    [Fact]
    public void ConstraintOnARecordParameter_DoesNotAlsoEmitAValidatorWithNoRules()
    {
        // The diagnostic is the whole output. Emitting an empty validator as well would register
        // something that validates nothing, which is the state this exists to make visible.
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet([Required] string Name);
            """
        );

        Assert.DoesNotContain("Sample.PetValidator.g.cs", result.Sources.Keys);
    }

    // A model with no mistakes in it produces no diagnostics at all, which is the assertion that
    // keeps the ones above from passing for the wrong reason.

    [Fact]
    public void WellFormedModel_ProducesNoDiagnosticsAndCompiles()
    {
        var result = GeneratorHarness.Run(
            """
            using System;
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Sample;

            // Sealed, which is what "well formed" now means for a nested target: an unsealed one
            // leaves open what should happen to a value of a more derived type, and VM1503 asks.
            public sealed record Address {
                [Required]
                [StringLength(100, Min = 1)]
                public string? Street { get; init; }
            }

            public record Pet {
                [Required]
                [StringLength(100, Min = 1)]
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
            """
        );

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    // VM1004 — [MultipleOf] on a member with no numeric type.

    [Theory]
    [InlineData("[MultipleOf(5)] public string? Name { get; init; }")]
    [InlineData("[MultipleOf(5)] public bool Flag { get; init; }")]
    [InlineData("[MultipleOf(5)] public DateTime Starts { get; init; }")]
    [InlineData("[MultipleOf(5)] public List<int> Sizes { get; init; } = new();")]
    public void MultipleOf_OnNonNumeric_IsVM1004(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1004");
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
    public void MultipleOf_OnNumeric_IsSilentAndCompiles(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    // VM1104 — a divisor that would divide by zero, or invert the question.

    [Theory]
    [InlineData("[MultipleOf(0)] public int Quantity { get; init; }")]
    [InlineData("[MultipleOf(-5)] public int Negative { get; init; }")]
    [InlineData("[MultipleOf(0.0)] public double Ratio { get; init; }")]
    [InlineData("[MultipleOf(\"0\")] public decimal Price { get; init; }")]
    public void MultipleOf_WithANonPositiveDivisor_IsVM1104(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1104");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

        // The point of the diagnostic: `value % 0` is CS0020 for an integral member, so the
        // constraint has to be dropped rather than emitted.
        Assert.Empty(result.CompilationErrors);
    }

    // VM1105 — a divisor with no form the member's type can be checked against.

    [Theory]
    [InlineData("[MultipleOf(\"not a number\")] public decimal Price { get; init; }")]
    [InlineData("[MultipleOf(\"2.5\")] public int Quantity { get; init; }")]
    [InlineData("[MultipleOf(2.5)] public int Whole { get; init; }")]
    public void MultipleOf_WithAnUnparseableDivisor_IsVM1105(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1105");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Empty(result.CompilationErrors);
    }

    // VM1005 — [UniqueItems] on something with no elements.

    [Theory]
    [InlineData("[UniqueItems] public int Age { get; init; }")]
    [InlineData("[UniqueItems] public string? Name { get; init; }")]
    public void UniqueItems_OnNonCollection_IsVM1005(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1005");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("[UniqueItems] public List<string> Tags { get; init; } = new();")]
    [InlineData("[UniqueItems] public int[] Sizes { get; init; } = Array.Empty<int>();")]
    [InlineData("[UniqueItems] public IEnumerable<string>? Codes { get; init; }")]
    public void UniqueItems_OnACollection_IsSilentAndCompiles(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    // VM1202 — elements with no equality of their own, which compare by reference.

    [Fact]
    public void UniqueItems_OverAClassWithNoEquality_IsVM1202()
    {
        var result = GeneratorHarness.Run(
            """
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
            """
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1202");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Sample.Tag", diagnostic.GetMessage());
    }

    /// <summary>
    /// The four ways an element type earns value equality. None of them should warn.
    /// </summary>
    [Fact]
    public void UniqueItems_OverElementsWithEquality_IsSilent()
    {
        var result = GeneratorHarness.Run(
            """
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
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1202");
    }

    // VM1102 — a [Range] that declares neither bound.

    [Fact]
    public void Range_WithNoBounds_IsVM1102()
    {
        var result = GeneratorHarness.Run(Model("[Range] public int Age { get; init; }"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1102");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("[Range(Min = 1)] public int Age { get; init; }")]
    [InlineData("[Range(Max = 99)] public int Count { get; init; }")]
    [InlineData("[Range(1, 99)] public int Both { get; init; }")]
    public void Range_WithOneBoundOrTwo_IsSilent(string member)
    {
        var result = GeneratorHarness.Run(Model(member));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// The regression VM1103 did not cover: a fractional bound written as a numeric literal against
    /// a <c>decimal</c> member. C# has no implicit double-to-decimal conversion, so the emitted
    /// comparison was CS0019 - an error inside generated code, which plan §7.5 rules out.
    /// </summary>
    [Fact]
    public void Range_WithAFractionalLiteralOnADecimal_Compiles()
    {
        var result = GeneratorHarness.Run(
            Model("[Range(0.5, 9.99)] public decimal Price { get; init; }")
        );

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
    [InlineData("[StringLength(10, Min = 1)] public int Quantity { get; init; }", "VM1001")]
    [InlineData("[Pattern(\"^a$\")] public int Quantity { get; init; }", "VM1001")]
    [InlineData("[ItemCount(1, 5)] public int Quantity { get; init; }", "VM1002")]
    [InlineData("[Range(1, 10)] public object? Thing { get; init; }", "VM1003")]
    [InlineData("[Required] public int Quantity { get; init; }", "VM1201")]
    [InlineData("[Pattern(\"([unclosed\")] public string? Name { get; init; }", "VM1106")]
    [InlineData("[MultipleOf(5)] public string? Name { get; init; }", "VM1004")]
    [InlineData("[UniqueItems] public int Quantity { get; init; }", "VM1005")]
    public void DiagnosedConstraint_DoesNotAlsoEmitUncompilableCode(
        string member,
        string diagnostic
    )
    {
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
    public void DiagnosedConstraint_LeavesTheRestOfTheModelIntact()
    {
        var result = GeneratorHarness.Run(
            Model(
                """
                [StringLength(10, Min = 1)] public int Quantity { get; init; }

                [Required] public string? Name { get; init; }
                """
            )
        );

        Assert.Contains(result.Diagnostics, d => d.Id == "VM1001");
        Assert.Empty(result.CompilationErrors);

        var emitted = Assert
            .Single(result.Sources, pair => pair.Key.EndsWith("Validator.g.cs"))
            .Value;

        Assert.Contains("ReportRequired(ctx, \"name\", value: value.Name)", emitted);
        Assert.DoesNotContain("Quantity", emitted);
    }

    [Theory]
    [InlineData("[EnumDefined] public int Quantity { get; init; }", "int")]
    [InlineData("[EnumDefined] public string? Name { get; init; }", "string")]
    public void EnumDefined_OnANonEnum_IsVM1006(string member, string mentioned)
    {
        var result = GeneratorHarness.Run(Model(member));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1006");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(mentioned, diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }
}
