using System.Text.Json.Serialization;
using ValidationModules;
using ValidationModules.Constraints;

namespace ApiDemo;

/// <summary>
/// The request body every probe posts. Shaped to exercise the three field-path forms a client has
/// to parse - a flat name, a nested path, and an indexed one.
/// </summary>
public sealed record CreateOrder {
    [Required, StringLength(min: 3, max: 40)]
    public string? Reference { get; init; }

    [Range(1, 500)]
    public int Quantity { get; init; }

    [ValidateNested]
    public Address? ShipTo { get; init; }

    [ValidateNested]
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];
}

public sealed record Address {
    [Required]
    public string? Postcode { get; init; }
}

public sealed record OrderLine {
    [Required]
    public string? Sku { get; init; }
}

/// <summary>
/// Reports at the object level rather than against a field, which is what puts the empty-string key
/// on the wire. Registered by hand, because no attribute produces a type-level error.
/// </summary>
public sealed class OrderTotalsValidator : IValidatorFor<CreateOrder> {
    public ValidationFlow Validate(ref ValidationContext context, CreateOrder value) =>
        value.Quantity > 100 && value.Lines.Count == 0
            ? context.ReportHere("bulk_needs_lines", "a bulk order must list its lines.")
            : ValidationFlow.Continue;
}

/// <summary>
/// The app's own serialiser metadata. An AOT-published minimal API resolves request and response
/// types through the application's context, which knows nothing about this package's problem body -
/// so the two are chained rather than replaced. See Program.cs.
/// </summary>
[JsonSerializable(typeof(CreateOrder))]
[JsonSerializable(typeof(Address))]
[JsonSerializable(typeof(OrderLine))]
[JsonSerializable(typeof(AcceptedOrder))]
internal sealed partial class ApiDemoJsonContext : JsonSerializerContext;

public sealed record AcceptedOrder(string Reference, int Quantity);
