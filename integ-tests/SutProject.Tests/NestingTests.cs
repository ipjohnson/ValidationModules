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

        var result = CatalogValidator.Instance.Validate(catalog);

        Assert.Equal("items[sku-2].sku", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void SelfReferentialType_ValidatesDownTheWholeTree() {
        var node = new Node { Label = "a", Child = new Node { Label = "b", Child = new Node() } };

        var result = NodeValidator.Instance.Validate(node);

        Assert.Equal("child.child.label", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void CyclicGraph_ThrowsRatherThanOverflowingTheStack() {
        // A StackOverflowException cannot be caught and takes the process down with it, so the depth
        // guard turns a caller's data bug into something diagnosable.
        // A record cannot hold a cycle - `a with { Child = b }` copies - so this needs a mutable type.
        var head = new MutableNode { Label = "head" };
        head.Child = head;

        var exception = Assert.Throws<InvalidOperationException>(
            () => MutableNodeValidator.Instance.Validate(head));

        Assert.Contains("cycle", exception.Message);
    }

    [Fact]
    public void NestedObject_DoesNotRecurseIntoAMissingValue() {
        var result = NodeValidator.Instance.Validate(new Node { Label = "a" });

        Assert.True(result.IsValid);
    }
}
