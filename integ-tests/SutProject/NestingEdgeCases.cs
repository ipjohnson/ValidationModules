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
