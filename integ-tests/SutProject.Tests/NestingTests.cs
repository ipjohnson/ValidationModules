using SutProject.Nesting;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Sub-object validation: how a nested failure is pathed, and the two shapes that used not to work.
/// </summary>
public class NestingTests {

    [Fact]
    public void Dictionary_IsPathedByKeyRatherThanPosition() {
        // This used to emit a call to a KeyValuePairValidator that does not exist, because every
        // dictionary is also an IEnumerable<KeyValuePair<K,V>> and that reading was taken first.
        // The consumer's build broke inside generated code.
        var catalog = new Catalog {
            Items = new Dictionary<string, Item> {
                ["sku-1"] = new Item { Sku = "ok" },
                ["sku-2"] = new Item(),
            },
        };

        var result = new CatalogValidator().Validate(catalog);

        Assert.Equal("items[sku-2].sku", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void SelfReferentialType_ValidatesDownTheWholeTree() {
        var node = new Node { Label = "a", Child = new Node { Label = "b", Child = new Node() } };

        var result = new NodeValidator().Validate(node);

        Assert.Equal("child.child.label", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void TwoLevelsDeep_ReportsEverySegment() {
        var basket = TwoLineBasket(secondLineSku: null);

        var result = new BasketValidator().Validate(basket);

        Assert.Equal("order.lines[1].sku", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void ThreeLevelsDeep_ElidesTheMiddleAndTakesItsIndexWithIt() {
        // The documented cost of compact paths, pinned in executable form so it is a deliberate
        // change rather than an accident if it ever moves. `lines[1]` is neither the outermost
        // segment nor the immediate parent, so it goes and its index goes with it - the caller can
        // see a postal code failed on some line, but not which. HANDOFF.md §3.1, API-SURFACE.md §3.2.
        var basket = TwoLineBasket(secondLinePostalCode: null);

        var result = new BasketValidator().Validate(basket);

        Assert.Equal("order...shipTo.postalCode", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void CyclicGraph_ThrowsRatherThanOverflowingTheStack() {
        // A StackOverflowException cannot be caught and takes the process down with it, so the depth
        // guard turns a caller's data bug into something diagnosable.
        // A record cannot hold a cycle - `a with { Child = b }` copies - so this needs a mutable type.
        var head = new MutableNode { Label = "head" };
        head.Child = head;

        var exception = Assert.Throws<InvalidOperationException>(
            () => new MutableNodeValidator().Validate(head));

        Assert.Contains("cycle", exception.Message);
    }

    [Fact]
    public void NestedObject_DoesNotRecurseIntoAMissingValue() {
        var result = new NodeValidator().Validate(new Node { Label = "a" });

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// Two lines, the first always clean, so whichever argument is left null is the only failure in
    /// the pass and <c>Assert.Single</c> is pinning the path rather than picking one of several.
    /// </summary>
    private static Basket TwoLineBasket(string? secondLineSku = "ok", string? secondLinePostalCode = "SW1") =>
        new() {
            Order = new Purchase {
                Lines = [
                    new Line { Sku = "ok", ShipTo = new Destination { PostalCode = "EC1" } },
                    new Line { Sku = secondLineSku, ShipTo = new Destination { PostalCode = secondLinePostalCode } },
                ],
            },
        };
}
