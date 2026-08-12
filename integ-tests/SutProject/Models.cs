using System.Text.Json.Serialization;
using ValidationModules.Constraints;

namespace SutProject;

/// <summary>Native constraints, nesting and collections.</summary>
public sealed record Pet {
    [Required]
    [StringLength(min: 1, max: 10)]
    public string? Name { get; init; }

    [Pattern("^[A-Z]{3}$")]
    public string? Sku { get; init; }

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
