using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The two checks generated validators call instead of writing a comparison.
/// </summary>
/// <remarks>
/// Both are reached from emitted code rather than from a consumer's own source, so nothing else
/// would catch a change to them. The floating-point cases in particular are the whole argument for
/// <c>[MultipleOf]</c> accepting <c>double</c> at all, and are pinned here as evidence rather than
/// as a description.
/// </remarks>
public class ConstraintChecksTests {

    /// <summary>
    /// Every one of these fails a naive <c>value % 0.01</c> in binary floating point - 0.3 % 0.01 is
    /// 0.00999999999999998 - and every one is a value a specification author would call valid. This
    /// is why the check converts to decimal first.
    /// </summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(1.05)]
    [InlineData(2.10)]
    [InlineData(0.07)]
    [InlineData(99.99)]
    [InlineData(1234.56)]
    [InlineData(0.35)]
    public void IsMultipleOf_AcceptsValuesTheNaiveModuloRejects(double value) {
        Assert.False(value % 0.01 == 0, "the premise: this value fails a binary-domain check");
        Assert.True(ConstraintChecks.IsMultipleOf(value, 0.01m));
    }

    /// <summary>
    /// The double-to-decimal conversion rounds to 15 significant digits, so accumulated
    /// representation error cancels rather than compounding.
    /// </summary>
    [Fact]
    public void IsMultipleOf_CancelsAccumulatedError() {
        Assert.True(ConstraintChecks.IsMultipleOf(0.1 + 0.2, 0.1m));
    }

    [Theory]
    [InlineData(0.125, 0.01)]
    [InlineData(1.005, 0.01)]
    [InlineData(7.0, 5.0)]
    public void IsMultipleOf_RejectsWhatIsNotAMultiple(double value, double divisor) {
        Assert.False(ConstraintChecks.IsMultipleOf(value, (decimal)divisor));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(15.0)]
    public void IsMultipleOf_AcceptsZeroAndNegatives(double value) {
        Assert.True(ConstraintChecks.IsMultipleOf(value, 5m));
    }

    /// <summary>
    /// None of these can be shown to be a multiple of anything. Reported as failures rather than
    /// passes, because passing would claim a check ran that did not - and a conversion that
    /// overflows would throw, which this runtime does not do on a validation path.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e30)]
    [InlineData(-1e30)]
    public void IsMultipleOf_RejectsWhatItCannotEvaluate(double value) {
        Assert.False(ConstraintChecks.IsMultipleOf(value, 0.01m));
    }

    [Fact]
    public void IsMultipleOf_HandlesFloatThroughTheSamePath() {
        Assert.True(ConstraintChecks.IsMultipleOf(0.25f, 0.05m));
        Assert.False(ConstraintChecks.IsMultipleOf(0.26f, 0.05m));
    }

    [Fact]
    public void AllUnique_IsTrueForDistinctElements() {
        Assert.True(ConstraintChecks.AllUnique(new[] { "a", "b", "c" }));
    }

    [Fact]
    public void AllUnique_IsFalseForARepeat() {
        Assert.False(ConstraintChecks.AllUnique(new[] { "a", "b", "a" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AllUnique_IsTrueBelowTwoElements(int count) {
        Assert.True(ConstraintChecks.AllUnique(Enumerable.Range(0, count).ToList()));
    }

    /// <summary>
    /// Both sides of the pairwise/set threshold, since they are separate implementations.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(200)]
    public void AllUnique_AgreesAcrossTheThreshold(int count) {
        var distinct = Enumerable.Range(0, count).ToList();
        Assert.True(ConstraintChecks.AllUnique(distinct));

        var repeated = new List<int>(distinct) { 0 };
        Assert.False(ConstraintChecks.AllUnique(repeated));
    }

    /// <summary>
    /// An enumerable with no count and no indexer still validates - the check is typed on
    /// IEnumerable, so the fallback the count constraints need does not arise here.
    /// </summary>
    [Fact]
    public void AllUnique_WalksAnEnumerableWithNoCount() {
        static IEnumerable<int> Sequence() {
            yield return 1;
            yield return 2;
            yield return 1;
        }

        Assert.False(ConstraintChecks.AllUnique(Sequence()));
    }

    [Fact]
    public void AllUnique_UsesValueEqualityWhereTheElementHasIt() {
        Assert.False(ConstraintChecks.AllUnique(new[] { new Point(1, 2), new Point(1, 2) }));
    }

    /// <summary>
    /// The case VM0025 warns about, pinned so the warning's claim stays true: a class with no
    /// equality of its own compares by reference, and two equal-looking elements both pass.
    /// </summary>
    [Fact]
    public void AllUnique_ComparesByReferenceWhereTheElementHasNoEquality() {
        Assert.True(ConstraintChecks.AllUnique(new[] { new Opaque("x"), new Opaque("x") }));
    }

    [Fact]
    public void AllUnique_StopsAtNullsRatherThanThrowing() {
        Assert.False(ConstraintChecks.AllUnique(new string?[] { null, "a", null }));
        Assert.True(ConstraintChecks.AllUnique(new string?[] { null, "a" }));
    }

    private sealed record Point(int X, int Y);

    private sealed class Opaque(string value) {
        public string Value { get; } = value;
    }
}
