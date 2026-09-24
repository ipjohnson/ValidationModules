using System.Text.Json.Serialization;
using ValidationModules;
using ValidationModules.Constraints;
using DA = System.ComponentModel.DataAnnotations;

namespace SutProject.FieldNaming;

/// <summary>
/// Explicit names beside the policy's: a <c>[Display(Name)]</c> label, a PascalCase
/// <c>[JsonPropertyName]</c> the camelCase namer would respell, and a snake_case one it would not.
/// </summary>
public sealed record Contact
{
    [Required, DA.Display(Name = "Postal code")]
    public string? PostalCode { get; init; }

    [Required, JsonPropertyName("GivenName")]
    public string? GivenName { get; init; }

    [Required, JsonPropertyName("family_name")]
    public string? FamilyName { get; init; }

    [Required(Message = "{field} needs a value."), DA.Display(Name = "Nick name")]
    public string? Nickname { get; init; }
}

/// <summary>An object-level rule that names a member by its CLR name.</summary>
[GenerateValidator]
public sealed class Registrant : DA.IValidatableObject
{
    [JsonPropertyName("given_name")]
    public string? GivenName { get; init; }

    public IEnumerable<DA.ValidationResult> Validate(DA.ValidationContext validationContext)
    {
        if (GivenName is null)
        {
            yield return new DA.ValidationResult("a given name is needed", [nameof(GivenName)]);
        }
    }
}

/// <summary>A <c>[CustomValidation]</c> result naming its member through the context.</summary>
public sealed class Applicant
{
    [DA.CustomValidation(typeof(Applicant), nameof(Check)), JsonPropertyName("family_name")]
    public string? FamilyName { get; init; }

    public static DA.ValidationResult? Check(string? value, DA.ValidationContext context) =>
        value is null ? null : new DA.ValidationResult("not accepted", [context.MemberName!]);
}

/// <summary>Resource messages, whose <c>{0}</c> is the DataAnnotations display name.</summary>
public static class FieldNamingMessages
{
    public static string Missing => "{0} is missing";
}

public sealed class Signatory
{
    [DA.Required(
        ErrorMessageResourceType = typeof(FieldNamingMessages),
        ErrorMessageResourceName = nameof(FieldNamingMessages.Missing)
    )]
    public string? LastName { get; init; }

    [DA.Required(
        ErrorMessageResourceType = typeof(FieldNamingMessages),
        ErrorMessageResourceName = nameof(FieldNamingMessages.Missing)
    )]
    [DA.Display(Name = "First name")]
    public string? FirstName { get; init; }
}

/// <summary>An <c>Ensure</c> over members the wire knows by other names.</summary>
public sealed record Stay
{
    [JsonPropertyName("start_date")]
    public DateOnly StartDate { get; init; }

    [JsonPropertyName("end_date")]
    public DateOnly EndDate { get; init; }
}

public sealed class StayRules : IValidationRulesFor<Stay>
{
    public static void Describe(ValidationRules<Stay> rules, Stay x) =>
        rules.Ensure(x.StartDate < x.EndDate);
}

/// <summary>A labelled property constrained by a rules class.</summary>
public sealed record Parcel
{
    [DA.Display(Name = "Tracking number")]
    public string? TrackingNumber { get; init; }
}

public sealed class ParcelRules : IValidationRulesFor<Parcel>
{
    public static void Describe(ValidationRules<Parcel> rules, Parcel x) =>
        rules.Require(x.TrackingNumber).Length(5, 20);
}
