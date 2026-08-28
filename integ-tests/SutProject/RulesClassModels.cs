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
    public static ValidationFlow GuestInitialMatchesReference(ref ValidationContext context, Reservation value) =>
        value.Guest is { Length: > 0 } guest && value.Reference is { Length: > 0 } reference &&
        guest[0] != reference[0]
            ? context.Report("reference", "guest_initial", "reference must start with the guest's initial.")
            : ValidationFlow.Continue;
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

/// <summary>
/// Every conditional shape the DSL offers, in one declaration, so both engines can be run against
/// the same rules and compared error for error.
/// </summary>
/// <remarks>
/// The substitutability promise of API-SURFACE.md §19.9 is what conditions put most at risk: a
/// condition may read live static state, so an engine evaluating it per rule and one evaluating it
/// per pass produce different answers rather than the same answer twice.
/// </remarks>
public sealed record Claim {
    public bool IsAuto { get; init; }

    public bool IsDraft { get; init; }

    public bool IsExpedited { get; init; }

    public string? Plate { get; init; }

    public string? Reason { get; init; }

    public string? Reference { get; init; }

    public string? Notes { get; init; }
}

public sealed class ClaimRules : IValidationRulesFor<Claim> {

    public void Describe(ValidationRules<Claim> rules) {
        // Chained: guards both constraints of its own statement, and nothing past the semicolon.
        rules.Required(x => x.Reason).Length(2, 20).When(x => x.IsExpedited);

        // Chained negated.
        rules.Required(x => x.Reference).Unless(x => x.IsDraft);

        // Block, with the other half declared through Otherwise rather than a second predicate.
        rules.When(x => x.IsAuto, () => {
            rules.Required(x => x.Plate);
        }).Otherwise(() => {
            rules.Required(x => x.Notes);
        });
    }
}

/// <summary>
/// A condition reading a mutable static counter, so "once per pass" is observed rather than
/// asserted. Three rules name it; the counter must move by one per validation, on either engine.
/// </summary>
public sealed record Metered {
    public static int Evaluations;

    /// <summary>
    /// Public and on the model, not private on the rules class: a condition is lifted into its own
    /// static class carrying the declaring file's usings, so it can only reach what that class can.
    /// </summary>
    public static bool Counted(Metered value) {
        Evaluations++;

        return value.Gate;
    }

    public bool Gate { get; init; }

    public string? First { get; init; }

    public string? Second { get; init; }

    public string? Third { get; init; }
}

public sealed class MeteredRules : IValidationRulesFor<Metered> {

    public void Describe(ValidationRules<Metered> rules) {
        // A lambda rather than the method group: a condition has to be liftable into a static
        // method, and a method group has no body for the generator to copy.
        rules.When(x => Metered.Counted(x), () => {
            rules.Required(x => x.First);
            rules.Required(x => x.Second);
            rules.Required(x => x.Third);
        });
    }
}

/// <summary>
/// Private constants of the types whose literals need care, referenced from a lifted predicate and
/// from a condition. Compiled and run rather than snapshotted, because the failure mode is a literal
/// that reads back as a different value or a different type.
/// </summary>
public sealed record Quote {
    public decimal Amount { get; init; }

    public double Ratio { get; init; }

    public QuoteTier Tier { get; init; }
}

public enum QuoteTier { Standard = 0, Premium = 1 }

public sealed class QuoteRules : IValidationRulesFor<Quote> {

    // A decimal: without the suffix this is a double literal, and the comparison would not compile.
    private const decimal Ceiling = 1000.50m;

    // Seventeen significant digits: the default ToString on a .NET Framework host would drop the
    // last two, and the rule would then accept a value it is meant to reject.
    private const double MaxRatio = 1.2345678901234567;

    // An enum constant is carried as its underlying number, so the cast is what preserves it.
    private const QuoteTier Restricted = QuoteTier.Premium;

    public void Describe(ValidationRules<Quote> rules) {
        rules.Ensure(x => x.Amount <= Ceiling, code: "ceiling");
        rules.Ensure(x => x.Ratio <= MaxRatio, code: "ratio");
        rules.Ensure(x => x.Amount > 0m, code: "positive").When(x => x.Tier == Restricted);
    }
}
