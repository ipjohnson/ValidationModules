using Annotated = ValidationModules.Benchmarks.Comparative.Models.Annotated;

namespace ValidationModules.Benchmarks.Comparative.Models;

/// <summary>
/// Builds the payloads the engines are given, in both model sets.
/// </summary>
/// <remarks>
/// The two sets are kept field-for-field identical on purpose. If the shared model's customer is
/// 36 years old and the annotated one is 37, some future edit will make one of them fail a rule the
/// other passes, and the comparison will quietly become a comparison of two different payloads.
/// </remarks>
public static class SampleData {

    public static Customer ValidCustomer() => new() {
        Name = "Ada Lovelace",
        Email = "ada@example.com",
        Age = 36,
        Tier = "gold",
        Notes = "Long-standing account.",
    };

    /// <summary>Every rule violated - the worst case for all three engines equally.</summary>
    public static Customer InvalidCustomer() => new() {
        Name = null,
        Email = "not-an-email",
        Age = 500,
        Tier = "platinum",
        Notes = new string('x', 501),
    };

    public static Annotated.Customer ValidAnnotatedCustomer() => new() {
        Name = "Ada Lovelace",
        Email = "ada@example.com",
        Age = 36,
        Tier = "gold",
        Notes = "Long-standing account.",
    };

    public static Annotated.Customer InvalidAnnotatedCustomer() => new() {
        Name = null,
        Email = "not-an-email",
        Age = 500,
        Tier = "platinum",
        Notes = new string('x', 501),
    };

    public static Order ValidOrder() => new() {
        Reference = "ORD-0001",
        Buyer = ValidCustomer(),
        ShipTo = ValidAddress(),
        Lines = [ValidLine(1), ValidLine(2), ValidLine(3)],
    };

    /// <summary>One failure at each level, so every level has to build an error path.</summary>
    public static Order InvalidOrder() => new() {
        Reference = "nope",
        Buyer = ValidCustomer() with { Age = 500 },
        ShipTo = ValidAddress() with { PostalCode = "not-a-postcode" },
        Lines = [ValidLine(1), ValidLine(2) with { Quantity = 0 }, ValidLine(3)],
    };

    public static Annotated.Order ValidAnnotatedOrder() => new() {
        Reference = "ORD-0001",
        Buyer = ValidAnnotatedCustomer(),
        ShipTo = ValidAnnotatedAddress(),
        Lines = [ValidAnnotatedLine(1), ValidAnnotatedLine(2), ValidAnnotatedLine(3)],
    };

    public static Annotated.Order InvalidAnnotatedOrder() => new() {
        Reference = "nope",
        Buyer = ValidAnnotatedCustomer() with { Age = 500 },
        ShipTo = ValidAnnotatedAddress() with { PostalCode = "not-a-postcode" },
        Lines = [ValidAnnotatedLine(1), ValidAnnotatedLine(2) with { Quantity = 0 }, ValidAnnotatedLine(3)],
    };

    public static Address ValidAddress() => new() {
        Line1 = "1 Riverside Way",
        City = "Portland",
        PostalCode = "97201",
    };

    public static Annotated.Address ValidAnnotatedAddress() => new() {
        Line1 = "1 Riverside Way",
        City = "Portland",
        PostalCode = "97201",
    };

    public static OrderLine ValidLine(int seed) => new() {
        Sku = Skus[seed % Skus.Length],
        Quantity = 1 + seed % 9,
    };

    public static Annotated.OrderLine ValidAnnotatedLine(int seed) => new() {
        Sku = Skus[seed % Skus.Length],
        Quantity = 1 + seed % 9,
    };

    /// <summary>A basket of clean elements, for the collection sweep.</summary>
    public static Basket BasketOf(int lineCount) {
        var lines = new List<OrderLine>(lineCount);

        for (var i = 0; i < lineCount; i++) {
            lines.Add(ValidLine(i));
        }

        return new Basket { Id = "basket-1", Lines = lines };
    }

    private static readonly string[] Skus = ["ABC-1234", "DEF-5678", "GHI-9012", "JKL-3456"];
}
