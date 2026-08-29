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

    public static void Describe(ValidationRules<Reservation> rules, Reservation x) {
        rules.Require(x.Guest).Length(2, 40);
        rules.Pattern(x.Reference, ReservationPatterns.Reference);
        rules.Range(x.Nights, 1, 30);
        rules.Count(x.Notes, 0, 3);

        // The same four constraints the attribute front end reads, declared here instead. Both
        // flatten into one validator, so a rule declared either way has to mean the same thing.
        rules.RangeAtLeast(x.Guests, 1);
        rules.MultipleOf(x.Deposit, 0.05m);
        rules.Unique(x.Rooms);

        rules.Ensure(x.Start < x.End);
        rules.Ensure(x.Nights <= 7 || x.Notes != null, code: "long_stay_needs_notes");

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
    public static void Describe(ValidationRules<Filing> rules, Filing x) {
        rules.Require(x.Reference);

        // Anchored to Reference, reported under attachment. The property already carries a rule, so
        // a per-property field name would have kept the first one and lost this.
        rules.Ensure(
            x.Reference == null || x.Attachment != null,
            field: "attachment",
            code: "attachment_required",
            message: "an attachment is required once a reference is set.");

        // Advisory: surfaced, but the filing is still valid.
        rules.Ensure(
            x.DaysLate <= 30,
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

    public static void Describe(ValidationRules<Ticket> rules, Ticket x) {
        // Require passes on "AB"; the two constraints behind it both fail.
        rules.Require(x.Code).Length(3, 10);
        rules.Pattern(x.Code, TicketPatterns.Digits);

        // The same pair with nothing in front of them.
        rules.Length(x.Note, 3, 10);
        rules.Pattern(x.Note, TicketPatterns.Digits);

        rules.Range(x.Amount, 10m, 20m);
        rules.MultipleOf(x.Amount, 4m);
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
    public static void Describe(ValidationRules<Badge> rules, Badge x) => rules.Require(x.Holder).Length(2, 20);
}

/// <summary>
/// Every conditional shape the surface offers - which is to say, C#. Conditions are <c>if</c>/
/// <c>else</c>, evaluated where written, at validation time inside the region.
/// </summary>
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

    public static void Describe(ValidationRules<Claim> rules, Claim x) {
        // Guards both constraints of the chain, and nothing past the brace.
        if (x.IsExpedited) {
            rules.Require(x.Reason).Length(2, 20);
        }

        // What used to be Unless is a negation.
        if (!x.IsDraft) {
            rules.Require(x.Reference);
        }

        // What used to be a block with an Otherwise is an else.
        if (x.IsAuto) {
            rules.Require(x.Plate);
        } else {
            rules.Require(x.Notes);
        }
    }
}

/// <summary>
/// A condition reading a mutable static counter, so "evaluated where written" is observed rather
/// than asserted: one <c>if</c> in the body is one evaluation per pass, however many rules the
/// branch declares.
/// </summary>
public sealed record Metered {
    public static int Evaluations;

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

    public static void Describe(ValidationRules<Metered> rules, Metered x) {
        if (Metered.Counted(x)) {
            rules.Require(x.First);
            rules.Require(x.Second);
            rules.Require(x.Third);
        }
    }
}

/// <summary>
/// Private constants of the types whose literals need care, referenced from an <c>Ensure</c> and
/// from an <c>if</c>. Compiled and run rather than snapshotted, because the failure mode is a
/// literal that reads back as a different value or a different type.
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

    public static void Describe(ValidationRules<Quote> rules, Quote x) {
        rules.Ensure(x.Amount <= Ceiling, code: "ceiling");
        rules.Ensure(x.Ratio <= MaxRatio, code: "ratio");

        if (x.Tier == Restricted) {
            rules.Ensure(x.Amount > 0m, code: "positive");
        }
    }
}
