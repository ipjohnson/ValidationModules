using ValidationModules;
using ValidationModules.Constraints;
using DA = System.ComponentModel.DataAnnotations;

namespace SutProject.ObjectLevel;

/// <summary>
/// An <c>IValidatableObject</c> whose own rules report only a warning. A warning does not make the
/// value invalid, so the object rule still runs.
/// </summary>
public sealed class Appointment : DA.IValidatableObject
{
    public string? Note { get; init; }

    public bool ObjectRuleRan { get; private set; }

    public IEnumerable<DA.ValidationResult> Validate(DA.ValidationContext validationContext)
    {
        ObjectRuleRan = true;
        yield return new DA.ValidationResult("the appointment is not confirmed");
    }
}

public sealed class AppointmentRules : IValidationRulesFor<Appointment>
{
    public static void Describe(ValidationRules<Appointment> rules, Appointment x) =>
        rules.Ensure(x.Note != null, code: "note_missing", severity: ValidationSeverity.Warning);
}

/// <summary>
/// The parent's error belongs to the parent. The nested destination's object rule depends only on
/// the destination's own rules.
/// </summary>
public sealed class Shipment
{
    [Required]
    public string? Reference { get; init; }

    [ValidateNested]
    public Destination? Destination { get; init; }
}

public sealed class Destination : DA.IValidatableObject
{
    [Required]
    public string? Street { get; init; }

    public bool ObjectRuleRan { get; private set; }

    public IEnumerable<DA.ValidationResult> Validate(DA.ValidationContext validationContext)
    {
        ObjectRuleRan = true;
        yield return new DA.ValidationResult("the destination is not served", [nameof(Street)]);
    }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class EndsAfterStartAttribute : DA.ValidationAttribute
{
    public override bool IsValid(object? value) =>
        value is not StayBase stay || stay.End > stay.Start;
}

/// <summary>
/// A class-level rule on a base class, where a date-range rule shared by several kinds of stay would
/// sit. DataAnnotations applies it to every derived type.
/// </summary>
[EndsAfterStart(ErrorMessage = "The stay must end after it starts.")]
public abstract class StayBase
{
    public DateOnly Start { get; init; }

    public DateOnly End { get; init; }
}

public sealed class Stay : StayBase, DA.IValidatableObject
{
    [Required]
    public string? Guest { get; init; }

    public bool ObjectRuleRan { get; private set; }

    public IEnumerable<DA.ValidationResult> Validate(DA.ValidationContext validationContext)
    {
        ObjectRuleRan = true;
        yield break;
    }
}

/// <summary>A class-level <c>[CustomValidation]</c>, called with the object as the value.</summary>
[DA.CustomValidation(typeof(Party), nameof(CheckGuests))]
public sealed class Party
{
    public int Guests { get; init; }

    public static DA.ValidationResult? CheckGuests(Party party, DA.ValidationContext context) =>
        party.Guests > 0
            ? DA.ValidationResult.Success
            : new DA.ValidationResult($"{context.DisplayName} needs a guest.", [nameof(Guests)]);
}
