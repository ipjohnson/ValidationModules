using System.Text.RegularExpressions;
using ValidationModules.Constraints;

namespace ValidationModules.Benchmarks.Models;

/// <summary>
/// A flat API payload covering every constraint kind exactly once. The shape most request bodies
/// actually have, and the baseline the nested and collection models are read against.
/// </summary>
public sealed record Customer {
    [Required]
    [StringLength(min: 1, max: 100)]
    public string? Name { get; init; }

    [Required]
    [Pattern(typeof(BenchmarkPatterns), nameof(BenchmarkPatterns.Email))]
    public string? Email { get; init; }

    [Pattern(typeof(BenchmarkPatterns), nameof(BenchmarkPatterns.Sku))]
    public string? ReferralCode { get; init; }

    [Range(0, 120)]
    public int Age { get; init; }

    [AllowedValues("gold", "silver", "bronze")]
    public string? Tier { get; init; }

    [StringLength(Max = 500)]
    public string? Notes { get; init; }

    [ItemCount(min: 0, max: 10)]
    public IReadOnlyList<string> Labels { get; init; } = [];

    [Range(0.0, 1.0)]
    public double DiscountRate { get; init; }
}

/// <summary>
/// Nesting plus a bounded collection: one object two levels down and a list of children, which is
/// where the path-building machinery starts doing real work.
/// </summary>
public sealed record Order {
    [Required]
    [Pattern(typeof(BenchmarkPatterns), nameof(BenchmarkPatterns.Sku))]
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

    [StringLength(Max = 120)]
    public string? Line2 { get; init; }

    [Required]
    [StringLength(min: 1, max: 60)]
    public string? City { get; init; }

    [Required]
    [Pattern(typeof(BenchmarkPatterns), nameof(BenchmarkPatterns.PostalCode))]
    public string? PostalCode { get; init; }
}

public sealed record OrderLine {
    [Required]
    [Pattern(typeof(BenchmarkPatterns), nameof(BenchmarkPatterns.Sku))]
    public string? Sku { get; init; }

    [Range(1, 999)]
    public int Quantity { get; init; }

    [Range(0.0, 100000.0)]
    public decimal UnitPrice { get; init; }
}

/// <summary>
/// An unbounded collection of validated elements, so element count can be a benchmark parameter
/// without <c>[ItemCount]</c> failing at the top of every pass and changing what is measured.
/// </summary>
public sealed record Basket {
    [Required]
    public string? Id { get; init; }

    [ValidateNested]
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];
}

/// <summary>
/// Self-referential, so nesting depth can be a benchmark parameter. Terminates on a null child;
/// the cycle guard at <see cref="ValidationErrorCollector.MaxDepth"/> - enforced by
/// <c>ValidationContext</c> against the depth it carries - is what stops a genuine cycle, and is
/// deliberately not exercised here.
/// </summary>
public sealed record Node {
    [Required]
    public string? Label { get; init; }

    [ValidateNested]
    public Node? Child { get; init; }
}

// The single-constraint types below exist so that each constraint can be priced on its own through
// real generated code. Measuring one of them against Customer is how a surprising per-constraint
// number gets attributed to the constraint rather than to the pass around it.

public sealed record RequiredOnly {
    [Required]
    public string? Value { get; init; }
}

public sealed record StringLengthOnly {
    [StringLength(min: 1, max: 100)]
    public string? Value { get; init; }
}

public sealed record RangeOnly {
    [Range(0, 120)]
    public int Value { get; init; }
}

public sealed record PatternOnly {
    [Pattern(typeof(BenchmarkPatterns), nameof(BenchmarkPatterns.Sku))]
    public string? Value { get; init; }
}

public sealed record AllowedValuesOnly {
    [AllowedValues("gold", "silver", "bronze")]
    public string? Value { get; init; }
}

public sealed record ItemCountOnly {
    [ItemCount(min: 1, max: 10)]
    public IReadOnlyList<string> Value { get; init; } = [];
}

/// <summary>
/// The patterns these models reference.
/// </summary>
/// <remarks>
/// The reference form throughout, rather than <c>[Pattern("...")]</c>. Partly because this project
/// publishes AOT and the inline form is a VM0017 there, but mostly because it is the form an
/// AOT-facing consumer writes - benchmarking the other one would price a shape the library steers
/// people away from.
/// </remarks>
public static partial class BenchmarkPatterns {

    [GeneratedRegex("^[A-Z]{3}-[0-9]{4}$")]
    public static partial Regex Sku();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[a-zA-Z]{2,}$")]
    public static partial Regex Email();

    [GeneratedRegex("^[0-9]{5}(-[0-9]{4})?$")]
    public static partial Regex PostalCode();
}
