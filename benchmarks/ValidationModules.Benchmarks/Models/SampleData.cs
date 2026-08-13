namespace ValidationModules.Benchmarks.Models;

/// <summary>
/// Builds the instances the benchmarks validate.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is deterministic. A randomized payload makes the failure count vary between
/// iterations, and since the failure count is what decides whether the pass allocates, that turns
/// the allocation column into noise.
/// </para>
/// <para>
/// <b>Read the valid instances first.</b> Production traffic mostly validates cleanly, so the
/// clean-pass number is the one a consumer feels; the invalid ones price the failure path and the
/// path-materialization it drags in.
/// </para>
/// </remarks>
public static class SampleData {

    /// <summary>Passes every constraint.</summary>
    public static Customer ValidCustomer() => new() {
        Name = "Ada Lovelace",
        Email = "ada@example.com",
        ReferralCode = "ABC-1234",
        Age = 36,
        Tier = "gold",
        Notes = "Long-standing account.",
        Labels = ["vip", "newsletter"],
        DiscountRate = 0.15,
    };

    /// <summary>
    /// Fails exactly one constraint - the realistic bad-request shape, where a client got one field
    /// wrong rather than sending nothing.
    /// </summary>
    public static Customer CustomerWithOneFailure() => ValidCustomer() with { Age = 500 };

    /// <summary>
    /// Fails every constraint, which is also the worst case for the collector: eight errors, eight
    /// composed messages, eight path materializations.
    /// </summary>
    public static Customer InvalidCustomer() => new() {
        Name = null,
        Email = "not-an-email",
        ReferralCode = "nope",
        Age = 500,
        Tier = "platinum",
        Notes = new string('x', 501),
        Labels = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11"],
        DiscountRate = 4.0,
    };

    /// <summary>Two levels of nesting and three elements, all passing.</summary>
    public static Order ValidOrder() => new() {
        Reference = "ORD-0001",
        Buyer = ValidCustomer(),
        ShipTo = ValidAddress(),
        Lines = [ValidLine(1), ValidLine(2), ValidLine(3)],
    };

    /// <summary>
    /// One failure per nesting level, so every level has to materialize a path -
    /// <c>buyer.age</c>, <c>shipTo.postalCode</c>, <c>lines[1].quantity</c>.
    /// </summary>
    public static Order InvalidOrder() => new() {
        Reference = "nope",
        Buyer = CustomerWithOneFailure(),
        ShipTo = ValidAddress() with { PostalCode = "not-a-postcode" },
        Lines = [ValidLine(1), ValidLine(2) with { Quantity = 0 }, ValidLine(3)],
    };

    public static Address ValidAddress() => new() {
        Line1 = "1 Riverside Way",
        Line2 = null,
        City = "Portland",
        PostalCode = "97201",
    };

    public static OrderLine ValidLine(int seed) => new() {
        Sku = Skus[seed % Skus.Length],
        Quantity = 1 + seed % 9,
        UnitPrice = 10m + seed % 50,
    };

    /// <summary>
    /// A basket of <paramref name="lineCount"/> elements, of which every tenth fails when
    /// <paramref name="withFailures"/> is set.
    /// </summary>
    /// <remarks>
    /// A tenth rather than all of them: a collection where every element fails prices the failure
    /// path repeated, which the flat model already covers. The interesting collection question is
    /// what the per-element machinery costs when most elements are clean, which is the mix here.
    /// </remarks>
    public static Basket BasketOf(int lineCount, bool withFailures) {
        var lines = new List<OrderLine>(lineCount);

        for (var i = 0; i < lineCount; i++) {
            var line = ValidLine(i);

            lines.Add(withFailures && i % 10 == 0 ? line with { Quantity = 0 } : line);
        }

        return new Basket { Id = "basket-1", Lines = lines };
    }

    /// <summary>
    /// A chain <paramref name="depth"/> levels deep. The innermost node fails when
    /// <paramref name="failAtLeaf"/> is set, which is the case that forces the longest path walk.
    /// </summary>
    public static Node ChainOf(int depth, bool failAtLeaf) {
        var node = new Node { Label = failAtLeaf ? null : "leaf" };

        for (var i = 1; i < depth; i++) {
            node = new Node { Label = "node", Child = node };
        }

        return node;
    }

    /// <summary>Pre-built so the SKU is not composed inside a benchmark's setup.</summary>
    private static readonly string[] Skus = ["ABC-1234", "DEF-5678", "GHI-9012", "JKL-3456"];
}
