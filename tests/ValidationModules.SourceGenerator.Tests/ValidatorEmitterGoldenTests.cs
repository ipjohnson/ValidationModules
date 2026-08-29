using ValidationModules.SourceGenerator.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Golden files over what <c>ValidatorEmitter</c> writes, per plan §13.
/// </summary>
/// <remarks>
/// <para>
/// The assertions elsewhere in this project check that a particular substring appears in the
/// emitted text, which is the right shape for "this constraint produced a call" and the wrong shape
/// for the thing most likely to go wrong: a change that alters everything slightly. Emitted source
/// is the product's real output — it lands in a consumer's <c>obj/</c>, it is what the trimmer sees,
/// and its allocation behaviour is a documented promise — so a whole-file diff is the review
/// surface that matters.
/// </para>
/// <para>
/// Every file here is also compiled by the harness, so a golden file can never record something
/// that does not build. Accept intended changes with <c>UPDATE_SNAPSHOTS=1</c> and read the diff.
/// </para>
/// </remarks>
public class ValidatorEmitterGoldenTests {

    private static string Emit(string source, params (string Key, string Value)[] properties) {
        var result = GeneratorHarness.Run(source, properties);

        Assert.Empty(result.CompilationErrors);

        return string.Join("\n\n", result.Sources
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"// ==== {pair.Key} ====\n{pair.Value}"));
    }

    [Fact]
    public void FlatModel_EveryConstraintKind() {
        Snapshot.Match(Emit("""
            using System;
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Sample;

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

                [MultipleOf(5)]
                public int Quantity { get; init; }

                [MultipleOf("0.05")]
                public decimal Price { get; init; }

                [MultipleOf(0.01)]
                public double Ratio { get; init; }

                [UniqueItems]
                public List<string> Codes { get; init; } = new();

                [Range(Min = 1)]
                public int AtLeastOne { get; init; }

                [Range(Max = 99)]
                public int AtMostNinetyNine { get; init; }
            }
            """));
    }

    [Fact]
    public void NestedObject_PushesAndDelegatesToTheElementValidator() {
        Snapshot.Match(Emit("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Address {
                [Required]
                public string? Street { get; init; }
            }

            public record Pet {
                [Required]
                public string? Name { get; init; }

                [ValidateNested]
                public Address? Home { get; init; }
            }
            """));
    }

    [Fact]
    public void Collection_WalksByIndexRatherThanEnumerator() {
        // The shape TypeFacts.IsIndexable exists to produce: a for loop over an indexer, because
        // foreach over an interface-typed collection boxes List<T>'s struct enumerator and a clean
        // pass is supposed to allocate nothing.
        Snapshot.Match(Emit("""
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Toy {
                [Required]
                public string? Name { get; init; }
            }

            public record Pet {
                [ValidateNested]
                public List<Toy> Toys { get; init; } = new();
            }
            """));
    }

    [Fact]
    public void EnumerableOnlyCollection_FallsBackToForeach() {
        Snapshot.Match(Emit("""
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Toy {
                [Required]
                public string? Name { get; init; }
            }

            public record Pet {
                [ValidateNested]
                public IEnumerable<Toy> Toys { get; init; } = new List<Toy>();
            }
            """));
    }

    [Fact]
    public void Dictionary_ValidatesValuesAndKeysThePath() {
        // Checked before the collection reading, because every dictionary is also an
        // IEnumerable<KeyValuePair<K,V>> — and taking that reading emitted a call to a
        // KeyValuePairValidator that could not exist.
        Snapshot.Match(Emit("""
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Toy {
                [Required]
                public string? Name { get; init; }
            }

            public record Pet {
                [ValidateNested]
                public Dictionary<string, Toy> Toys { get; init; } = new();
            }
            """));
    }

    [Fact]
    public void ReferencedPattern_CallsTheProvidedRegexMember() {
        // Declared as an ordinary static member rather than with [GeneratedRegex]: the harness runs
        // this generator alone, so the regex generator is not present to supply the partial's body.
        // What is under test is the call the emitter writes, which is identical either way — and a
        // consumer declaring the member with [GeneratedRegex] is what PatternPolicyTests covers.
        Snapshot.Match(Emit("""
            using System.Text.RegularExpressions;
            using ValidationModules.Constraints;

            namespace Sample;

            public static class PetPatterns {
                private static readonly Regex SkuValue = new Regex("^[A-Z]{3}$");
                public static Regex Sku() => SkuValue;
            }

            public record Pet {
                [Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
                public string? Sku { get; init; }
            }
            """, ("PublishAot", "true")));
    }

    [Fact]
    public void NullableAndValueTypes_GuardBeforeReading() {
        Snapshot.Match(Emit("""
            using System;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Required]
                public int? Age { get; init; }

                [Range(0, 30)]
                public int? Weight { get; init; }

                [Range(0.0, 1.0, ExclusiveMax = true)]
                public double Ratio { get; init; }
            }
            """));
    }

    [Fact]
    public void DataAnnotationsModel_ProducesTheSameShapeAsNativeConstraints() {
        // Two vocabularies, one IR, one emitter — so a rule's origin stops mattering the moment it
        // is read. This file is the evidence for that claim.
        Snapshot.Match(Emit("""
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public class Customer {
                [Required]
                [StringLength(100, MinimumLength = 1)]
                public string? Name { get; set; }

                [Range(0, 120)]
                public int Age { get; set; }

                [RegularExpression("^[A-Z]{3}$")]
                public string? Sku { get; set; }

                [MaxLength(5)]
                public List<string> Tags { get; set; } = new();
            }
            """));
    }

    [Fact]
    public void DataAnnotationsFormatValidators_CompileToTheBclChecks() {
        // Each format attribute becomes a straight call into ConstraintChecks with the BCL's own
        // semantics - null passing exactly as the attributes read it, [Url] on a Uri member
        // resolving to the Uri overload by nothing more than overload resolution, and the
        // [FileExtensions] set hoisted into a static field the way patterns are.
        Snapshot.Match(Emit("""
            using System;
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public class Contact {
                [EmailAddress]
                public string? Email { get; set; }

                [Phone]
                public string? Mobile { get; set; }

                [Url]
                public string? Homepage { get; set; }

                [Url]
                public Uri? Avatar { get; set; }

                [CreditCard]
                public string? Card { get; set; }

                [Base64String]
                public string? Payload { get; set; }

                [FileExtensions(Extensions = "png,jpg")]
                public string? Attachment { get; set; }
            }
            """));
    }

    [Fact]
    public void FieldNaming_SnakeCase() {
        Snapshot.Match(Emit("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Required]
                public string? PostalCode { get; init; }

                [Required]
                public string? HTTPStatusLine { get; init; }
            }
            """, ("ValidationModules_FieldNaming", "SnakeCase")));
    }

    [Fact]
    public void FieldNaming_JsonPropertyNameWins() {
        Snapshot.Match(Emit("""
            using System.Text.Json.Serialization;
            using ValidationModules.Constraints;

            namespace Sample;

            public record Pet {
                [Required]
                [JsonPropertyName("pet_name")]
                public string? Name { get; init; }
            }
            """));
    }

    [Fact]
    public void GlobalNamespaceType_EmitsWithoutANamespaceDeclaration() {
        // The validator lives where the type lives, global namespace included. Parking it in a
        // namespace of ours made the validator unfindable from the file that declared the type.
        Snapshot.Match(Emit("""
            using ValidationModules.Constraints;

            public record Pet {
                [Required]
                public string? Name { get; init; }
            }
            """));
    }

    [Fact]
    public void RulesClass_FlattensIntoTheSameValidatorAsTheAttributes() {
        Snapshot.Match(Emit("""
            using System;
            using System.Collections.Generic;
            using ValidationModules;

            namespace Sample;

            public sealed record Reservation {
                public string? Guest { get; init; }
                public int Nights { get; init; }
                public DateOnly Start { get; init; }
                public DateOnly End { get; init; }
            }

            public sealed class ReservationRules : IValidationRulesFor<Reservation> {
                public void Describe(ValidationRules<Reservation> rules) {
                    rules.Required(x => x.Guest).Length(2, 40);
                    rules.Range(x => x.Nights, 1, 30);
                    rules.Ensure(x => x.Start < x.End);
                }
            }
            """));
    }
}
