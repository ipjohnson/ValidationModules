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
