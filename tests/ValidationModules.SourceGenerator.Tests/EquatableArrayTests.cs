using System.Collections;
using System.Collections.Immutable;
using System.Linq;
using ValidationModules.SourceGenerator.Impl;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The value equality every incremental-generator model depends on.
/// </summary>
/// <remarks>
/// <see cref="ImmutableArray{T}"/> compares by reference, so a record holding one is never equal to
/// a structurally identical rebuild and every downstream stage re-runs on each keystroke. Nothing
/// fails when that goes wrong; the generator just gets slow, which is why it is worth asserting
/// directly rather than hoping a pipeline test would notice.
/// </remarks>
public class EquatableArrayTests {

    private static EquatableArray<string> Of(params string[] values) =>
        new(values.ToImmutableArray());

    [Fact]
    public void StructurallyEqualArrays_AreEqual() {
        // The whole reason the type exists: two separately-built arrays with the same contents.
        Assert.Equal(Of("a", "b"), Of("a", "b"));
        Assert.True(Of("a", "b").Equals(Of("a", "b")));
        Assert.Equal(Of("a", "b").GetHashCode(), Of("a", "b").GetHashCode());
    }

    [Fact]
    public void DifferentContents_AreNotEqual() {
        Assert.NotEqual(Of("a", "b"), Of("a", "c"));
    }

    [Fact]
    public void DifferentLengths_AreNotEqual() {
        Assert.NotEqual(Of("a"), Of("a", "b"));
        Assert.NotEqual(Of("a", "b"), Of("a"));
    }

    [Fact]
    public void AnEmptyArray_EqualsAnotherEmptyOne() {
        Assert.Equal(EquatableArray<string>.Empty, Of());
        Assert.Equal(EquatableArray<string>.Empty.GetHashCode(), Of().GetHashCode());
    }

    [Fact]
    public void TwoDefaults_AreEqual() {
        Assert.Equal(default(EquatableArray<string>), default(EquatableArray<string>));
        Assert.Equal(0, default(EquatableArray<string>).GetHashCode());
    }

    [Fact]
    public void ADefault_IsNotEqualToAnEmptyArray() {
        // Pinned rather than endorsed. Both report Count 0 and enumerate to nothing, so they are
        // observationally the same array, and a model reaching one stage as default and another as
        // Empty compares unequal for no reason a caller could see - the cache miss this type
        // exists to prevent. Changing it is a behaviour change, not a test change.
        Assert.NotEqual(default(EquatableArray<string>), EquatableArray<string>.Empty);
    }

    [Fact]
    public void ADefault_ReadsAsAnEmptyArray() {
        var array = default(EquatableArray<string>);

        Assert.Equal(0, array.Count);
        Assert.Empty(array);
    }

    [Fact]
    public void CountAndTheIndexer_ReadTheValues() {
        var array = Of("a", "b", "c");

        Assert.Equal(3, array.Count);
        Assert.Equal("b", array[1]);
    }

    [Fact]
    public void ItEnumeratesInOrder() {
        Assert.Equal(["a", "b", "c"], Of("a", "b", "c").ToArray());
    }

    [Fact]
    public void TheNonGenericEnumerator_WalksTheSameValues() {
        var walked = new List<object?>();

        foreach (var value in (IEnumerable)Of("a", "b")) {
            walked.Add(value);
        }

        Assert.Equal(["a", "b"], walked);
    }

    [Fact]
    public void EqualsAgainstAnObject_ComparesByValueAndRejectsOtherTypes() {
        Assert.True(Of("a").Equals((object)Of("a")));
        Assert.False(Of("a").Equals((object)"a"));
        Assert.False(Of("a").Equals(null));
    }

    [Fact]
    public void ToEquatableArray_CarriesTheSequence() {
        Assert.Equal(Of("a", "b"), new[] { "a", "b" }.AsEnumerable().ToEquatableArray());
    }
}
