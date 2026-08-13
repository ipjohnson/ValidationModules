using System.ComponentModel.DataAnnotations;

namespace ValidationModules.Benchmarks.Comparative.Models.Annotated;

/// <summary>
/// The same models again, declared with DataAnnotations, for
/// <see cref="System.ComponentModel.DataAnnotations.Validator"/> to walk.
/// </summary>
/// <remarks>
/// <para>
/// A separate type set rather than extra attributes on the shared models: this library's
/// DataAnnotations front-end reads these attributes too, so putting them alongside the native
/// constraints would emit every rule twice into the generated validator and charge
/// ValidationModules for work no consumer would ask it to do.
/// </para>
/// <para>
/// The rules are the same ones, spelled the way DataAnnotations spells them. Two differences are
/// unavoidable and both are recorded in <c>benchmarks/README.md</c>: <c>[RegularExpression]</c>
/// builds its own interpreted <see cref="System.Text.RegularExpressions.Regex"/> rather than
/// accepting the shared <c>[GeneratedRegex]</c>, and <c>Validator.TryValidateObject</c> does not
/// descend into nested objects at all.
/// </para>
/// </remarks>
public sealed record Customer {
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; init; }

    [Required]
    [RegularExpression(@"^[^@\s]+@[^@\s]+\.[a-zA-Z]{2,}$")]
    public string? Email { get; init; }

    [Range(0, 120)]
    public int Age { get; init; }

    [AllowedValues("gold", "silver", "bronze")]
    public string? Tier { get; init; }

    [StringLength(500)]
    public string? Notes { get; init; }
}

public sealed record Order {
    [Required]
    [RegularExpression("^ORD-[0-9]{4}$")]
    public string? Reference { get; init; }

    public Customer? Buyer { get; init; }

    public Address? ShipTo { get; init; }

    [MinLength(1)]
    [MaxLength(100)]
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];
}

public sealed record Address {
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string? Line1 { get; init; }

    [Required]
    [StringLength(60, MinimumLength = 1)]
    public string? City { get; init; }

    [Required]
    [RegularExpression("^[0-9]{5}(-[0-9]{4})?$")]
    public string? PostalCode { get; init; }
}

public sealed record OrderLine {
    [Required]
    [RegularExpression("^[A-Z]{3}-[0-9]{4}$")]
    public string? Sku { get; init; }

    [Range(1, 999)]
    public int Quantity { get; init; }
}
