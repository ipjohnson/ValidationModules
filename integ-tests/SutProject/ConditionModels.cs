using ValidationModules.Constraints;

namespace SutProject.Conditions;

/// <summary>
/// The discriminated-union shape conditional constraints exist for: a discriminator that says which
/// half of the model is meaningful, and constraints on the other half that should not fire.
/// </summary>
public sealed record Claim {
    public bool IsAuto { get; init; }

    public bool IsDraft { get; init; }

    [Required(When = nameof(IsAuto))]
    public string? PlateNumber { get; init; }

    [Required(Unless = nameof(IsDraft))]
    public string? Reference { get; init; }

    /// <summary>
    /// A guarded <c>Required</c> in front of an unguarded constraint. When the condition is false
    /// the Required never runs, so it suppresses nothing and the length check still applies.
    /// </summary>
    [Required(When = nameof(IsAuto))]
    [StringLength(2, 8)]
    public string? PolicyNumber { get; init; }

    [ValidateNested(When = nameof(IsAuto))]
    public AutoDetail? Auto { get; init; }
}

public sealed record AutoDetail {
    [Required]
    public string? Vin { get; init; }
}

/// <summary>
/// A condition reading mutable static state, so that "evaluated once per pass" is observable rather
/// than merely intended. Two constraints name it; the counter must move by one per validation.
/// </summary>
public sealed record Counted {
    public static int Evaluations;

    public static bool Enabled(Counted value) {
        Evaluations++;
        return value.Gate;
    }

    public bool Gate { get; init; }

    [Required(When = nameof(Enabled))]
    public string? First { get; init; }

    [Required(When = nameof(Enabled))]
    public string? Second { get; init; }

    [StringLength(2, 4, When = nameof(Enabled))]
    public string? Third { get; init; }
}
