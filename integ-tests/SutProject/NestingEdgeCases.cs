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
    /// <summary>
    /// The declared-type defect, now answered. Without a mode this checks <c>Kind</c> and nothing
    /// else, so a <c>Circle</c> missing its radius validates clean.
    /// </summary>
    [ValidateNested(Polymorphism.CompileTime)] public Shape? Primary { get; init; }
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

/// <summary>An enum is an integer with names on some of it; <c>[EnumDefined]</c> is the check.</summary>
public enum PaymentMethod { Card, Cash, Transfer }

/// <summary>A combination is a legitimate value here, which membership would reject.</summary>
[Flags]
public enum Access { None = 0, Read = 1, Write = 2, Delete = 4 }

public sealed record Payment {
    [EnumDefined] public PaymentMethod Method { get; init; }

    [EnumDefined] public Access Rights { get; init; }

    [EnumDefined] public PaymentMethod? Fallback { get; init; }
}


/// <summary>
/// A cycle through two types rather than one. Invisible to an identity test and identical to the
/// container: <c>AuthorValidator</c> asking for <c>IValidatorFor&lt;Book&gt;</c> and
/// <c>BookValidator</c> asking for <c>IValidatorFor&lt;Author&gt;</c> is still a circular
/// dependency, and still stops the application from starting.
/// </summary>
public sealed class Author {
    [Required] public string? Name { get; set; }
    [ValidateNested] public Book? Latest { get; set; }
}

public sealed class Book {
    [Required] public string? Title { get; set; }
    [ValidateNested] public Author? Writer { get; set; }
}
