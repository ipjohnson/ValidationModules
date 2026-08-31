using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ValidationModules.Constraints;

namespace SutProject;

/// <summary>Native constraints, nesting and collections.</summary>
public sealed record Pet {
    [Required]
    [StringLength(min: 1, max: 10)]
    public string? Name { get; init; }

    [Pattern("^[A-Z]{3}$")]
    public string? Sku { get; init; }

    /// <summary>
    /// The reference form. The [GeneratedRegex] has to live in consumer source, because source
    /// generators cannot see each other's output - so ours can call it, but could never write it.
    /// </summary>
    [Pattern(typeof(PetPatterns), nameof(PetPatterns.Slug))]
    public string? Slug { get; init; }

    [Range(0, 30)]
    public int Age { get; init; }

    [AllowedValues("available", "pending", "sold")]
    public string? Status { get; init; }

    [ValidateNested]
    public Address? Home { get; init; }

    [ItemCount(min: 1, max: 3)]
    [ValidateNested]
    public IReadOnlyList<Toy> Toys { get; init; } = new List<Toy>();
}

public sealed record Address {
    [Required]
    [JsonPropertyName("postal_code")]
    public string? PostalCode { get; init; }
}

public sealed record Toy {
    [Required]
    public string? Name { get; init; }
}

/// <summary>An explicit message, which must emit a literal rather than a composed one.</summary>
public sealed record Owner {
    [Required(Message = "an owner must be named")]
    public string? Name { get; init; }
}

/// <summary>A nullable value type, and an exclusive bound.</summary>
public sealed record Reading {
    [Required]
    [Range(0.0, 1.0, ExclusiveMax = true)]
    public double? Ratio { get; init; }
}

/// <summary>Consumer-declared patterns, implemented by the regex source generator.</summary>
public static partial class PetPatterns {
    [GeneratedRegex("^[a-z0-9-]+$")]
    public static partial Regex Slug();
}

/// <summary>
/// The string-bounds form of <c>[Range]</c>, for the types with no constant form in metadata.
/// </summary>
/// <remarks>
/// Here rather than only in the generator tests because those prove the emitted file compiles, and
/// what matters is that the comparison it compiles to is the right one. A bound parsed into the
/// wrong month would still compile.
/// </remarks>
public sealed record Booking {
    [Range("2000-01-01", "2100-12-31")]
    public DateOnly Starts { get; init; }

    [Range("0.00", "9.99")]
    public decimal Price { get; init; }

    [Range("00:00:00", "23:59:59")]
    public TimeSpan Window { get; init; }

    [Range("2000-01-01", "2100-01-01", ExclusiveMax = true)]
    public DateTime Effective { get; init; }
}

/// <summary>
/// <c>[MultipleOf]</c> across every numeric shape it accepts, and <c>[UniqueItems]</c>.
/// </summary>
/// <remarks>
/// The floating-point members are the interesting ones. They are checked in the decimal domain
/// rather than with <c>%</c>, because <c>0.3 % 0.01</c> is 0.00999999999999998 in binary floating
/// point - so a naive check rejects almost every price a specification would call valid. Running
/// the emitted comparison is the only way to see that, which is why these are here rather than
/// only in the generator tests.
/// </remarks>
public sealed record Order {
    [MultipleOf(5)]
    public int Quantity { get; init; }

    [MultipleOf(100)]
    public long Cents { get; init; }

    [MultipleOf("0.05")]
    public decimal Price { get; init; }

    [MultipleOf(0.01)]
    public double Ratio { get; init; }

    [MultipleOf(25)]
    public int? Optional { get; init; }

    [UniqueItems]
    public List<string> Codes { get; init; } = new();

    [UniqueItems]
    public int[] Sizes { get; init; } = Array.Empty<int>();
}

/// <summary>
/// A <c>[Range]</c> with one bound, and a fractional bound written as a numeric literal against a
/// <c>decimal</c>.
/// </summary>
/// <remarks>
/// Both are regressions rather than features. An absent bound used to be emitted as the type's
/// extreme, which reached the caller as "must be between 1 and 7.9228162514264338E+28"; and
/// <c>[Range(0.5, 9.99)]</c> on a decimal emitted <c>price &lt; 0.5</c>, which is CS0019 - an error
/// inside generated code.
/// </remarks>
public sealed record Allocation {
    [Range(Min = 1)]
    public int AtLeastOne { get; init; }

    [Range(Max = 99)]
    public int AtMostNinetyNine { get; init; }

    [Range(0.5, 9.99)]
    public decimal Fractional { get; init; }
}

/// <summary>
/// The single-bound constructor forms. [StringLength(min: 12)] was CS7036 in two consecutive
/// trials - the two-argument constructor had no defaults, so the property-setter form was the
/// only one-bound spelling.
/// </summary>
public sealed record Passphrase {
    [StringLength(min: 12)]
    public string? Value { get; init; }

    [ItemCount(max: 2)]
    public List<string> Hints { get; init; } = [];
}
