using ValidationModules;
using ValidationModules.Constraints;

namespace SutProject;

/// <summary>
/// The instance shape of a custom constraint: state built once in the constructor, the check
/// reading it on every pass. The construction counter is what the tests observe - one instance
/// per declaration site is the promise.
/// </summary>
public sealed class ChannelAttribute : Attribute, IConstraintFor<string> {
    public static int Constructions;

    private readonly string[] _allowed;

    public ChannelAttribute(params string[] allowed) {
        Constructions++;
        _allowed = allowed;
    }

    public bool IsValid(string value) => Array.IndexOf(_allowed, value) >= 0;

    public ValidationFlow Validate(ref ValidationContext context, string value, string field) =>
        IsValid(value)
            ? ValidationFlow.Continue
            : context.Report(field, "channel", $"{field} must be one of: {string.Join(", ", _allowed)}.");
}

/// <summary>The opt-out: a fresh instance at every check, counted the same way.</summary>
[PerValidationInstance]
public sealed class StampedAttribute : Attribute, IConstraintFor<int> {
    public static int Constructions;

    public StampedAttribute() {
        Constructions++;
    }

    public bool IsValid(int value) => value >= 0;

    public ValidationFlow Validate(ref ValidationContext context, int value, string field) =>
        IsValid(value) ? ValidationFlow.Continue : context.ReportCustom(field);
}

/// <summary>
/// Only <c>IsValid</c>: the interface's default <c>Validate</c> answers, honouring the base's
/// <c>Code</c> and <c>Message</c> set at the declaration site.
/// </summary>
public sealed class PairedAttribute : ValidationConstraintAttribute, IConstraintFor<int> {
    public bool IsValid(int value) => value % 2 == 0;
}

public record Bulletin {
    [Required]
    [Channel("email", "sms")]
    public string? Channel { get; init; }

    [Stamped]
    public int Sequence { get; init; }

    [Paired(Code = "pair", Message = "{field} must come in pairs")]
    public int? Batch { get; init; }
}
