using ValidationModules.Constraints;

namespace SutProject.Nesting;

/// <summary>A type that nests itself. Generation must terminate; validation is the question.</summary>
public sealed record Node {
    [Required] public string? Label { get; init; }
    [ValidateNested] public Node? Child { get; init; }
}

/// <summary>Documented as pathed `map[key]`. Is it?</summary>
public sealed record Catalog {
    [ValidateNested] public Dictionary<string, Item> Items { get; init; } = new();
}

public sealed record Item {
    [Required] public string? Sku { get; init; }
}

/// <summary>A nested property typed as a base class.</summary>
public abstract record Shape {
    [Required] public string? Kind { get; init; }
}

public sealed record Circle : Shape {
    [Required] public string? Radius { get; init; }
}

public sealed record Drawing {
    [ValidateNested] public Shape? Primary { get; init; }
}

/// <summary>A mutable self-reference, so a genuine cycle can be built in a test.</summary>
public sealed class MutableNode {
    [Required] public string? Label { get; set; }
    [ValidateNested] public MutableNode? Child { get; set; }
}

/// <summary>
/// A request-body shape deep enough that the compact path has to elide, with a distinct name at
/// every level so the result is legible. Reached through generated code rather than by driving the
/// context by hand, because what the emitter produces is what a consumer actually sees.
/// </summary>
public sealed record Basket {
    [ValidateNested] public Purchase? Order { get; init; }
}

public sealed record Purchase {
    [ValidateNested] public IReadOnlyList<Line> Lines { get; init; } = new List<Line>();
}

public sealed record Line {
    [Required] public string? Sku { get; init; }
    [ValidateNested] public Destination? ShipTo { get; init; }
}

public sealed record Destination {
    [Required] public string? PostalCode { get; init; }
}
