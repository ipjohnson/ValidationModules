using ValidationModules;

namespace SutProject.Declared;

/// <summary>
/// A facet: an interface whose rules are declared beside it in a rules class, applied from any
/// implementer with <c>rules.As&lt;IAudited&gt;(x)</c>.
/// </summary>
/// <remarks>
/// Declared through a rules class rather than constraint attributes, deliberately: attribute
/// constraints on an interface already reach every implementer through constraint inheritance, so
/// pairing them with <c>As</c> would declare the same rules twice. <c>As</c> exists for exactly
/// the rules inheritance cannot see - a rules class targeting the facet.
/// </remarks>
public interface IAudited {
    string? CreatedBy { get; }

    int Version { get; }
}

public sealed class AuditRules : IValidationRulesFor<IAudited> {
    public static void Describe(ValidationRules<IAudited> rules, IAudited x) {
        rules.Require(x.CreatedBy);
        rules.RangeAtLeast(x.Version, 1);
    }
}

public sealed record Shipment : IAudited {
    public string? CreatedBy { get; init; }

    public int Version { get; init; }

    public string? Carrier { get; init; }
}

public sealed class ShipmentRules : IValidationRulesFor<Shipment> {
    public static void Describe(ValidationRules<Shipment> rules, Shipment x) {
        rules.Require(x.Carrier);

        // Same-compilation binding: the generator sees IAuditedValidator here and binds
        // statically - no DI involved, and the path does not push.
        rules.As<IAudited>(x);
    }
}
