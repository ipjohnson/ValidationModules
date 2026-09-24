using ValidationModules;
using ValidationModules.Constraints;

namespace SutProject.Declared;

/// <summary>
/// A facet: an interface whose rules are declared beside it in a rules class, applied from any
/// implementer with <c>rules.As&lt;IAudited&gt;(x)</c>.
/// </summary>
/// <remarks>
/// Declared through a rules class, the rules constraint inheritance cannot see. A facet declared
/// with constraint attributes is <see cref="ITracked"/>.
/// </remarks>
public interface IAudited
{
    string? CreatedBy { get; }

    int Version { get; }
}

public sealed class AuditRules : IValidationRulesFor<IAudited>
{
    public static void Describe(ValidationRules<IAudited> rules, IAudited x)
    {
        rules.Require(x.CreatedBy);
        rules.RangeAtLeast(x.Version, 1);
    }
}

public sealed record Shipment : IAudited
{
    public string? CreatedBy { get; init; }

    public int Version { get; init; }

    public string? Carrier { get; init; }
}

public sealed class ShipmentRules : IValidationRulesFor<Shipment>
{
    public static void Describe(ValidationRules<Shipment> rules, Shipment x)
    {
        rules.Require(x.Carrier);

        // Same-compilation binding: the generator sees IAuditedValidator here and binds
        // statically - no DI involved, and the path does not push.
        rules.As<IAudited>(x);
    }
}

/// <summary>
/// A facet declared with constraint attributes. They reach every implementer through constraint
/// inheritance, and an implementer whose rules class validates it through <c>As</c> leaves them to
/// the facet's validator instead, so each is checked once, where the <c>As</c> was written.
/// </summary>
public interface ITracked
{
    [Required]
    string? TrackingNumber { get; }
}

public sealed record Parcel : ITracked
{
    public string? TrackingNumber { get; init; }

    [Required]
    public string? Recipient { get; init; }

    public bool Draft { get; init; }
}

public sealed class ParcelRules : IValidationRulesFor<Parcel>
{
    public static void Describe(ValidationRules<Parcel> rules, Parcel x)
    {
        // Under an if, the facet's attributes run only when it holds.
        if (!x.Draft)
        {
            rules.As<ITracked>(x);
        }
    }
}

/// <summary>The same facet with no rules class: its attributes merge into this type's validator.</summary>
public sealed record Letter : ITracked
{
    public string? TrackingNumber { get; init; }
}
