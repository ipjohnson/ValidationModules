using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// <c>[MultipleOf]</c> and <c>[UniqueItems]</c>, run rather than inspected.
/// </summary>
/// <remarks>
/// The generator tests prove the emitted file compiles, which for these two proves very little. A
/// <c>[MultipleOf]</c> emitted as a binary <c>%</c> compiles perfectly and then rejects 1.05
/// against a divisor of 0.01; a <c>[UniqueItems]</c> over the wrong comparer compiles and then
/// calls two identical elements distinct. Only running the comparison shows either.
/// </remarks>
public class MultipleOfAndUniqueItemsTests {

    private static Order Valid() => new() {
        Quantity = 10,
        Cents = 500,
        Price = 4.95m,
        Ratio = 1.05,
        Optional = null,
        Codes = ["a", "b"],
        Sizes = [1, 2, 3],
    };

    [Fact]
    public void CleanOrder_IsValid() {
        Assert.True(new OrderValidator().IsValid(Valid()));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(-5, true)]
    [InlineData(3, false)]
    [InlineData(-3, false)]
    public void Integral_AcceptsExactMultiples(int quantity, bool expected) {
        Assert.Equal(expected, new OrderValidator().IsValid(Valid() with { Quantity = quantity }));
    }

    [Theory]
    [InlineData("4.95", true)]
    [InlineData("0.05", true)]
    [InlineData("0.00", true)]
    [InlineData("4.99", false)]
    [InlineData("0.01", false)]
    public void Decimal_AcceptsExactMultiples(string price, bool expected) {
        var order = Valid() with {
            Price = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture),
        };

        Assert.Equal(expected, new OrderValidator().IsValid(order));
    }

    /// <summary>
    /// The evidence for accepting <c>double</c> at all. Every value here fails a binary-domain
    /// <c>value % 0.01</c>, and every one is a value a specification author would call valid.
    /// </summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(1.05)]
    [InlineData(99.99)]
    [InlineData(1234.56)]
    [InlineData(0.07)]
    public void Double_AcceptsWhatTheNaiveModuloWouldReject(double ratio) {
        Assert.NotEqual(0, ratio % 0.01);
        Assert.True(new OrderValidator().IsValid(Valid() with { Ratio = ratio }));
    }

    [Theory]
    [InlineData(0.125)]
    [InlineData(1.0050001)]
    public void Double_StillRejectsWhatIsNotAMultiple(double ratio) {
        Assert.False(new OrderValidator().IsValid(Valid() with { Ratio = ratio }));
    }

    [Fact]
    public void NullableMember_IsSkippedWhenAbsentAndCheckedWhenPresent() {
        Assert.True(new OrderValidator().IsValid(Valid() with { Optional = null }));
        Assert.True(new OrderValidator().IsValid(Valid() with { Optional = 50 }));
        Assert.False(new OrderValidator().IsValid(Valid() with { Optional = 51 }));
    }

    [Fact]
    public void MultipleOf_ReportsItsOwnCodeAndNamesTheDivisor() {
        var result = new OrderValidator().Validate(Valid() with { Quantity = 3 });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.MultipleOf, error.Code);
        Assert.Equal("quantity", error.Field);
        Assert.Equal("quantity must be a multiple of 5.", error.Message);
    }

    [Theory]
    [InlineData(new[] { "a", "b", "c" }, true)]
    [InlineData(new[] { "a", "b", "a" }, false)]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "a" }, true)]
    public void UniqueItems_ChecksAList(string[] codes, bool expected) {
        Assert.Equal(expected, new OrderValidator().IsValid(Valid() with { Codes = [.. codes] }));
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3 }, true)]
    [InlineData(new[] { 1, 2, 1 }, false)]
    public void UniqueItems_ChecksAnArray(int[] sizes, bool expected) {
        Assert.Equal(expected, new OrderValidator().IsValid(Valid() with { Sizes = sizes }));
    }

    /// <summary>
    /// Both sides of the pairwise/set threshold in <c>ConstraintChecks.AllUnique</c>, through the
    /// emitted call rather than directly.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(40)]
    public void UniqueItems_AgreesAcrossTheAllocationThreshold(int count) {
        var distinct = Enumerable.Range(0, count).ToArray();
        Assert.True(new OrderValidator().IsValid(Valid() with { Sizes = distinct }));

        var repeated = distinct.Append(0).ToArray();
        Assert.False(new OrderValidator().IsValid(Valid() with { Sizes = repeated }));
    }

    [Fact]
    public void UniqueItems_ReportsItsOwnCodeAndDoesNotEchoTheDuplicate() {
        var result = new OrderValidator().Validate(Valid() with { Codes = ["a", "a"] });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.UniqueItems, error.Code);
        Assert.Equal("codes", error.Field);
        Assert.Equal("codes must not contain duplicate items.", error.Message);
    }
}
