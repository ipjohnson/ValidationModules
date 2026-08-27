using ValidationModules.Constraints;

namespace SutProject.Polymorphic;

/// <summary>
/// The hierarchy from the probe that found the defect: a <c>Checkout</c> whose <c>Payment</c> is
/// declared as the base, holding a value of a subtype whose own rules used never to run.
/// </summary>
public abstract record Payment {
    [Required]
    public string? Currency { get; init; }
}

public record Card : Payment {
    [StringLength(16, 16)]
    public string? Pan { get; init; }
}

/// <summary>
/// Two levels down. Its arm has to precede <c>Card</c>'s in the emitted switch, because a type
/// pattern matches derived types and the <c>Card</c> arm would otherwise swallow it.
/// </summary>
public sealed record Premium : Card {
    [Required]
    public string? Concierge { get; init; }
}

public sealed record Bank : Payment {
    [Required]
    public string? Iban { get; init; }
}

public sealed record Checkout {
    [ValidateNested(Polymorphism.CompileTime)]
    public Payment? Payment { get; init; }
}

/// <summary>The same hierarchy, descended without dispatch, so the two modes can be compared.</summary>
public sealed record DeclaredOnlyCheckout {
    [ValidateNested(Polymorphism.DeclaredOnly)]
    public Payment? Payment { get; init; }
}

/// <summary>
/// The same hierarchy dispatched through the container rather than through a compile-time switch.
/// Unlike CompileTime, this composes: a separately registered validator for the runtime type runs
/// alongside the generated one.
/// </summary>
public sealed record DynamicCheckout {
    [ValidateNested(Polymorphism.Runtime)]
    public Payment? Payment { get; init; }
}

public sealed record Basketful {
    [ValidateNested(Polymorphism.CompileTime)]
    public List<Payment> Payments { get; init; } = new();
}
