using ValidationModules;
using ValidationModules.Constraints;
using DA = System.ComponentModel.DataAnnotations;

namespace SutProject;

/// <summary>
/// <c>[AllowedValues]</c> with a <c>Comparison</c>, which the generator used to ignore.
/// </summary>
public sealed record Subscription
{
    [AllowedValues("active", "pending", Comparison = StringComparison.OrdinalIgnoreCase)]
    public string? Status { get; init; }
}

/// <summary>
/// The DataAnnotations pair over enum values, whose messages used to list the qualified C#
/// expressions instead of the member names.
/// </summary>
public sealed record PlanChange
{
    [DA.AllowedValues(Tier.Pro, Tier.Enterprise)]
    public Tier Plan { get; init; } = Tier.Pro;

    [DA.DeniedValues(Tier.Free)]
    public Tier Downgrade { get; init; } = Tier.Pro;
}

public static class ConsignmentModes
{
    public const string Road = "road";
}

public sealed record Consignment
{
    public string? Kind { get; init; } = "parcel";

    public string? Mode { get; init; } = "road";

    public Tier Tier { get; init; } = Tier.Pro;
}

/// <summary>
/// The rules-class forms that used to drop values: the chained params form, a <c>const</c> in the
/// set, and enum values.
/// </summary>
public sealed class ConsignmentRules : IValidationRulesFor<Consignment>
{
    public static void Describe(ValidationRules<Consignment> rules, Consignment x)
    {
        rules.For(x.Kind).AllowedValues("parcel", "pallet");
        rules.AllowedValues(x.Mode, [ConsignmentModes.Road, "rail"]);
        rules.AllowedValues(x.Tier, [Tier.Pro, Tier.Enterprise]);
    }
}
