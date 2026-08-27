using ValidationModules.Constraints;

namespace SutProject.Inheritance;

/// <summary>
/// The three shapes of inheritance that used to lose their constraints, compiled here so the
/// emitted code is really run rather than only snapshotted. None of them is polymorphic: the value
/// being validated is always of the declared type.
/// </summary>
/// <remarks>
/// A shared request base carrying correlation and tenant ids is the ordinary case, not an exotic
/// one - as is an audited-entity interface, and a plain class hierarchy. All three produced a
/// validator that checked only the most-derived type's own properties.
/// </remarks>
public record BaseRequest {
    [Required]
    public string? CorrelationId { get; init; }

    [Required]
    public string? TenantId { get; init; }
}

public record CreateOrder : BaseRequest {
    [Required]
    public string? Sku { get; init; }
}

/// <summary>
/// A derived type adding nothing of its own. Without inherited constraints counting toward "this
/// type needs a validator", it would produce no validator at all.
/// </summary>
public record Ping : BaseRequest;

public interface IAudited {
    [Required]
    string? ModifiedBy { get; }
}

public record Document : IAudited {
    [Required]
    public string? Title { get; init; }

    public string? ModifiedBy { get; init; }
}

public class BaseDto {
    [Required]
    public string? A { get; set; }
}

public class DerivedDto : BaseDto {
    [Required]
    public string? B { get; set; }
}

/// <summary>
/// An interface constraint and the implementer's own on one property. The interface is a contract
/// the type opted into, so both apply - this is the one place declarations merge.
/// </summary>
public interface IStamped {
    [Required]
    string? Stamp { get; }
}

public record Envelope : IStamped {
    [StringLength(4, 8)]
    public string? Stamp { get; init; }
}
