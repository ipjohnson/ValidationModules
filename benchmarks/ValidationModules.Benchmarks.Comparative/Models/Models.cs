using System.Text.RegularExpressions;
using ValidationModules.Constraints;

namespace ValidationModules.Benchmarks.Comparative.Models;

/// <summary>
/// The models both ValidationModules and FluentValidation validate.
/// </summary>
/// <remarks>
/// <para>
/// One model set, not two. The constraint attributes drive the generated validator; the
/// FluentValidation validators in <c>Engines/FluentEngine.cs</c> are hand-written against the same
/// types and ignore the attributes entirely. So both engines walk identical object layouts and read
/// identical properties, and the measured difference is the engine rather than the model.
/// </para>
/// <para>
/// DataAnnotations cannot join in here: its attributes on these types would also be picked up by
/// this library's DataAnnotations front-end, which would emit a second copy of every rule into the
/// generated validator and make ValidationModules do twice the work. It gets a structurally
/// identical model set of its own in <c>AnnotatedModels.cs</c>.
/// </para>
/// </remarks>
public sealed record Customer {
    [Required]
    [StringLength(min: 1, max: 100)]
    public string? Name { get; init; }

    [Required]
    [Pattern(typeof(Patterns), nameof(Patterns.Email))]
    public string? Email { get; init; }

    [Range(0, 120)]
    public int Age { get; init; }

    [AllowedValues("gold", "silver", "bronze")]
    public string? Tier { get; init; }

    [StringLength(Max = 500)]
    public string? Notes { get; init; }
}

public sealed record Order {
    [Required]
    [Pattern(typeof(Patterns), nameof(Patterns.Reference))]
    public string? Reference { get; init; }

    [ValidateNested]
    public Customer? Buyer { get; init; }

    [ValidateNested]
    public Address? ShipTo { get; init; }

    [ItemCount(min: 1, max: 100)]
    [ValidateNested]
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];
}

public sealed record Address {
    [Required]
    [StringLength(min: 1, max: 120)]
    public string? Line1 { get; init; }

    [Required]
    [StringLength(min: 1, max: 60)]
    public string? City { get; init; }

    [Required]
    [Pattern(typeof(Patterns), nameof(Patterns.PostalCode))]
    public string? PostalCode { get; init; }
}

public sealed record OrderLine {
    [Required]
    [Pattern(typeof(Patterns), nameof(Patterns.Sku))]
    public string? Sku { get; init; }

    [Range(1, 999)]
    public int Quantity { get; init; }
}

/// <summary>
/// An unbounded collection, so element count can be a benchmark parameter without an
/// <c>[ItemCount]</c> ceiling failing every large payload and changing what is compared.
/// </summary>
public sealed record Basket {
    [Required]
    public string? Id { get; init; }

    [ValidateNested]
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];
}

/// <summary>
/// The patterns, declared once and handed to both engines.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shared. FluentValidation's <c>Matches</c> accepts a <see cref="Regex"/> instance, so
/// both engines can be given the same <c>[GeneratedRegex]</c> object - which takes the regex engine
/// out of the comparison and leaves it measuring rule dispatch, which is the thing that actually
/// differs.
/// </para>
/// <para>
/// It also means these numbers understate the gap for a typical FluentValidation codebase, where
/// <c>Matches("^[A-Z]{3}$")</c> builds an interpreted <see cref="Regex"/> from a string. That is the
/// honest direction to err in.
/// </para>
/// </remarks>
public static partial class Patterns {

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[a-zA-Z]{2,}$")]
    public static partial Regex Email();

    [GeneratedRegex("^[A-Z]{3}-[0-9]{4}$")]
    public static partial Regex Sku();

    [GeneratedRegex("^ORD-[0-9]{4}$")]
    public static partial Regex Reference();

    [GeneratedRegex("^[0-9]{5}(-[0-9]{4})?$")]
    public static partial Regex PostalCode();
}
