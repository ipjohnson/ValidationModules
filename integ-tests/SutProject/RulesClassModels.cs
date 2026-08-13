using System.Text.RegularExpressions;
using ValidationModules;

namespace SutProject.Declared;

/// <summary>
/// Stands in for a type from a package nobody here owns: no constraint attributes, no reference to
/// this library, nothing that could be edited to add a rule.
/// </summary>
public sealed record Reservation {
    public string? Guest { get; init; }
    public string? Reference { get; init; }
    public int Nights { get; init; }
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }
    public IReadOnlyList<string>? Notes { get; init; }
}

public static partial class ReservationPatterns {
    [GeneratedRegex("^[A-Z]{2}-[0-9]{6}$")]
    public static partial Regex Reference();
}

public static class ReservationChecks {

    /// <summary>Applied by method group, emitted as a direct call. See API-SURFACE.md §19.6.</summary>
    public static void GuestInitialMatchesReference(ref ValidationContext context, Reservation value) {
        if (value.Guest is { Length: > 0 } guest && value.Reference is { Length: > 0 } reference &&
            guest[0] != reference[0]) {
            context.Add("reference", "guest_initial", "reference must start with the guest's initial.");
        }
    }
}

/// <summary>
/// The declaration form from API-SURFACE.md §19, compiled by the generator into
/// <c>ReservationValidator</c> rather than run.
/// </summary>
public sealed class ReservationRules : IValidationRulesFor<Reservation> {

    public void Describe(ValidationRules<Reservation> rules) {
        rules.Required(x => x.Guest).Length(2, 40);
        rules.Pattern(x => x.Reference, ReservationPatterns.Reference);
        rules.Range(x => x.Nights, 1, 30);
        rules.Count(x => x.Notes, 0, 3);

        rules.Ensure(x => x.Start < x.End);
        rules.Ensure(x => x.Nights <= 7 || x.Notes != null, code: "long_stay_needs_notes");

        rules.Apply(ReservationChecks.GuestInitialMatchesReference);
    }
}
