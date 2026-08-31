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
    public void ConstraintInterfaceAttributes_HoistAndInvokeTheInstance() {
        // The instance shape of a custom constraint, every binding it can need: both members
        // public (direct calls), only IsValid (Validate casts to the interface default), the
        // constraint base's knobs riding into the construction with When woven outside the call,
        // a nullable member guarded and unwrapped, and [PerValidationInstance] trading the
        // hoisted field for a construction at the check.
        Snapshot.Match(Emit("""
            using System;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class ChannelAttribute : Attribute, IConstraintFor<string> {
                private readonly string[] _allowed;

                public ChannelAttribute(params string[] allowed) { _allowed = allowed; }

                public bool IsValid(string value) => Array.IndexOf(_allowed, value) >= 0;

                public ValidationFlow Validate(ref ValidationContext context, string value, string field) =>
                    IsValid(value)
                        ? ValidationFlow.Continue
                        : context.Report(field, "channel", $"{field} must be one of: {string.Join(", ", _allowed)}.");
            }

            public sealed class EvenAttribute : ValidationConstraintAttribute, IConstraintFor<int> {
                public bool IsValid(int value) => value % 2 == 0;
            }

            [PerValidationInstance]
            public sealed class StampAttribute : Attribute, IConstraintFor<int> {
                public bool IsValid(int value) => value >= 0;

                public ValidationFlow Validate(ref ValidationContext context, int value, string field) =>
                    IsValid(value) ? ValidationFlow.Continue : context.ReportCustom(field);
            }

            public record Broadcast {
                public bool IsScheduled { get; init; }

                [Required]
                [Channel("email", "sms")]
                public string? Channel { get; init; }

                [Even(Code = "pair", Message = "{field} must come in pairs", When = nameof(IsScheduled))]
                public int? Batch { get; init; }

                [Stamp]
                public int Sequence { get; init; }
            }
            """));
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
    public void DataAnnotationsCustomSurfaces_InvokeThroughTheBridge() {
        // The three DataAnnotations surfaces that carry user code: a custom attribute constructed
        // once into a static field, a [CustomValidation] method resolved to a direct static call
        // in both arities, and IValidatableObject sequenced last behind a clean-pass gate - which
        // also costs the type its boolean fast path.
        Snapshot.Match(Emit("""
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public sealed class DivisibleAttribute : ValidationAttribute {
                public DivisibleAttribute(int divisor) => Divisor = divisor;
                public int Divisor { get; }
                public override bool IsValid(object? value) => value is int n && n % Divisor == 0;
            }

            public class Order : IValidatableObject {
                [Divisible(3, ErrorMessage = "must divide by three")]
                public int Count { get; set; }

                [CustomValidation(typeof(Order), "CheckName")]
                public string? Name { get; set; }

                [CustomValidation(typeof(Order), "CheckSku")]
                public string? Sku { get; set; }

                public static ValidationResult? CheckName(string? value) => ValidationResult.Success;

                public static ValidationResult? CheckSku(string? value, ValidationContext context) =>
                    ValidationResult.Success;

                public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) {
                    yield break;
                }
            }
            """));
    }

    [Fact]
    public void CustomConstraintAttributes_CompileLikeBuiltIns() {
        // The native extensibility shape: the author's attribute, the author's static check,
        // compiled to the same straight-line form as a built-in - constructor constants in the
        // call, base knobs riding along, zero cost the model did not write.
        Snapshot.Match(Emit("""
            using System;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class SkuAttribute : CustomConstraintAttribute {
                public static bool IsValid(string value) => value.StartsWith("SKU-", StringComparison.Ordinal);
            }

            public sealed class DivisibleAttribute : CustomConstraintAttribute {
                public DivisibleAttribute(int divisor) { }

                public static bool IsValid(int value, int divisor) => value % divisor == 0;
            }

            public record Product {
                [Required]
                [Sku(Message = "sku must start with SKU-")]
                public string? Sku { get; init; }

                [Divisible(4)]
                public int Count { get; init; }

                [Divisible(2, Code = "pair")]
                public int? Spare { get; init; }
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
    public void RulesClassDescents_PassTheInjectedArraysIntoTheRegion() {
        // Nested and Each declared in a body: the validator still grows the injected-validator
        // machinery - fields, constructors, accessors - and hands the arrays to the region, so a
        // separately registered validator composes exactly as on an attribute descent. The walk
        // itself is the region's, in body order.
        Snapshot.Match(Emit("""
            using System.Collections.Generic;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Line {
                [Required] public string? Sku { get; init; }
            }

            public sealed record Address {
                [Required] public string? PostalCode { get; init; }
            }

            public sealed record Order {
                public Address? ShipTo { get; init; }
                public IReadOnlyList<Line>? Lines { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.Nested(x.ShipTo);
                    rules.Count(x.Lines, 1, 50).Each();
                }
            }
            """));
    }

    [Fact]
    public void RulesClassComputation_TranscribesReporterNameofAndAutoWrap() {
        // The free half of the surface in one region: a local feeding an Ensure whose message
        // names it, nameof through the subject rewritten to the wire path (interpolation
        // included), and a user helper returning ValidationFlow wrapped by its type alone.
        Snapshot.Match(Emit("""
            using System.Linq;
            using System.Collections.Generic;
            using ValidationModules;

            namespace Sample;

            public sealed record Order {
                public string? AccountNumber { get; init; }
                public decimal CreditLimit { get; init; }
                public IReadOnlyList<decimal>? Amounts { get; init; }
            }

            public static class Luhn {
                public static bool Validates(string? value) => value?.Length > 4;

                public static ValidationFlow Audit(IValidationContextReporter reporter, string? value) =>
                    reporter.ReportHere("audit", "audited");
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    var total = x.Amounts?.Sum() ?? 0m;
                    rules.Ensure(total <= x.CreditLimit);

                    if (!Luhn.Validates(x.AccountNumber)) {
                        rules.Context.Report(nameof(x.AccountNumber), "checksum",
                            $"{nameof(x.AccountNumber)} failed its checksum");
                    }

                    Luhn.Audit(rules.Context, x.AccountNumber);
                }
            }
            """));
    }

    [Fact]
    public void RulesClass_TranscribesIntoARegionTheValidatorCalls() {
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
                public static void Describe(ValidationRules<Reservation> rules, Reservation x) {
                    rules.Require(x.Guest).Length(2, 40);
                    rules.Range(x.Nights, 1, 30);
                    rules.Ensure(x.Start < x.End);
                }
            }
            """));
    }

    [Fact]
    public void RulesClassNullableValue_GuardsTheMemberAndKeepsItsPath() {
        // A nullable-valued rule in both spellings: written against the member, and written with
        // the .Value unwrap VM3104 corrects. The two must produce the same guarded shape and the
        // same "batteryKwh" wire path - the unwrap's .value hop must appear nowhere.
        Snapshot.Match(Emit("""
            using ValidationModules;

            namespace Sample;

            public sealed record Vehicle {
                public decimal? BatteryKwh { get; init; }
                public decimal? ReserveKwh { get; init; }
            }

            public sealed class VehicleRules : IValidationRulesFor<Vehicle> {
                public static void Describe(ValidationRules<Vehicle> rules, Vehicle x) {
                    rules.Range(x.BatteryKwh, 10m, 300m);
                    rules.Range(x.ReserveKwh.Value, 1m, 50m);
                }
            }
            """));
    }
}
