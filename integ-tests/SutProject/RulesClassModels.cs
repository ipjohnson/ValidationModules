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
    public int Guests { get; init; }
    public decimal Deposit { get; init; }
    public IReadOnlyList<string>? Rooms { get; init; }
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

        // The same four constraints the attribute front end reads, declared here instead. Both
        // flatten into one validator, so a rule declared either way has to mean the same thing.
        rules.RangeAtLeast(x => x.Guests, 1);
        rules.MultipleOf(x => x.Deposit, 0.05m);
        rules.Unique(x => x.Rooms);

        rules.Ensure(x => x.Start < x.End);
        rules.Ensure(x => x.Nights <= 7 || x.Notes != null, code: "long_stay_needs_notes");

        rules.Apply(ReservationChecks.GuestInitialMatchesReference);
    }
}

/// <summary>
/// Two <c>Ensure</c> rules anchored to the same property, one renaming its field and one reporting
/// as a warning. Both are what the generator used to drop.
/// </summary>
public sealed record Filing {
    public string? Reference { get; init; }

    public string? Attachment { get; init; }

    public int DaysLate { get; init; }
}

public sealed class FilingRules : IValidationRulesFor<Filing> {
    public void Describe(ValidationRules<Filing> rules) {
        rules.Required(x => x.Reference);

        // Anchored to Reference, reported under attachment. The property already carries a rule, so
        // a per-property field name would have kept the first one and lost this.
        rules.Ensure(
            x => x.Reference == null || x.Attachment != null,
            field: "attachment",
            code: "attachment_required",
            message: "an attachment is required once a reference is set.");

        // Advisory: surfaced, but the filing is still valid.
        rules.Ensure(
            x => x.DaysLate <= 30,
            field: "daysLate",
            code: "late_notice",
            message: "filed more than 30 days after the period end.",
            severity: ValidationSeverity.Warning);
    }
}


/// <summary>
/// Three fields, each carrying two constraints that fail together. The shape that used to expose
/// the <c>else if</c> chain in the emitter: the generated engine reported the first failure per
/// field and stopped, where the described engine reported both.
/// </summary>
/// <remarks>
/// A <c>[Required]</c> is not needed to trigger it - <see cref="Note"/> carries none - so the three
/// cases here are the three the divergence was reproduced with: guarded by a Required, unguarded,
/// and on a value type where neither constraint can be skipped for nullness.
/// </remarks>
public sealed record Ticket {
    public string? Code { get; init; }

    public string? Note { get; init; }

    public decimal Amount { get; init; }
}

public static partial class TicketPatterns {
    [GeneratedRegex("^[0-9]+$")]
    public static partial Regex Digits();
}

public sealed class TicketRules : IValidationRulesFor<Ticket> {

    public void Describe(ValidationRules<Ticket> rules) {
        // Required passes on "AB"; the two constraints behind it both fail.
        rules.Required(x => x.Code).Length(3, 10);
        rules.Pattern(x => x.Code, TicketPatterns.Digits);

        // The same pair with nothing in front of them.
        rules.Length(x => x.Note, 3, 10);
        rules.Pattern(x => x.Note, TicketPatterns.Digits);

        rules.Range(x => x.Amount, 10m, 20m);
        rules.MultipleOf(x => x.Amount, 4m);
    }
}

/// <summary>
/// An expression-bodied <c>Describe</c>, compiled here rather than only in the generator's own
/// tests. The arrow form used to throw inside the generator, which produced no output for the whole
/// compilation - a failure this project catches by existing.
/// </summary>
public sealed record Badge {
    public string? Holder { get; init; }
}

public sealed class BadgeRules : IValidationRulesFor<Badge> {
    public void Describe(ValidationRules<Badge> rules) => rules.Required(x => x.Holder).Length(2, 20);
}
