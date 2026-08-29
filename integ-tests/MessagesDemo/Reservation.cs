using ValidationModules;
using ValidationModules.Constraints;

namespace MessagesDemo;

/// <summary>
/// A model chosen to exercise one shape from each key family the packs translate: a required
/// string with length bounds, a two-bounded range, a pattern, an element count, and a cross-field
/// rule carrying the user code the packs also translate.
/// </summary>
public sealed record Reservation {

    [Required]
    [StringLength(min: 1, max: 60)]
    public string? Name { get; init; }

    [Range(1, 8)]
    public int PartySize { get; init; }

    [Pattern("^[A-Z]{2}-[0-9]{4}$")]
    public string? Code { get; init; }

    [ItemCount(1, 4)]
    public List<string> Guests { get; init; } = [];

    public DateOnly Start { get; init; }

    public DateOnly End { get; init; }
}

public sealed class ReservationRules : IValidationRulesFor<Reservation> {

    public static void Describe(ValidationRules<Reservation> rules, Reservation x) {
        // A user code: unknown to the shape inventory on purpose, translated by the packs all the
        // same - the map is keyed by string, and nothing about "date_order" is special.
        rules.Ensure(x.End >= x.Start, code: "date_order", field: "end");
    }
}
