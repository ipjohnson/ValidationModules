using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// <c>[Range]</c> with string bounds, run rather than inspected.
/// </summary>
/// <remarks>
/// The generator tests prove the emitted file compiles. That is necessary and not sufficient: a
/// bound parsed into the wrong month, or with the day and month transposed, compiles perfectly.
/// These assert the comparison it compiles to accepts and rejects the right values, including at
/// the bound itself, which is where an off-by-one lands.
/// </remarks>
public class RangeBoundsTests {

    private static Booking Valid() => new() {
        Starts = new DateOnly(2020, 6, 15),
        Price = 4.99m,
        Window = TimeSpan.FromHours(12),
        Effective = new DateTime(2020, 6, 15),
    };

    [Fact]
    public void CleanValue_IsValid() {
        Assert.True(new BookingValidator().IsValid(Valid()));
    }

    /// <summary>
    /// A bound the author did not declare is not compared against and is not named. It used to be
    /// emitted as the type's extreme, which reached a caller as "must be between 1 and
    /// 7.9228162514264338E+28" for a specification that set only <c>minimum</c>.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(int.MaxValue, true)]
    public void RangeWithOnlyAMinimum_HasNoUpperBound(int value, bool expected) {
        var allocation = new Allocation { AtLeastOne = value, AtMostNinetyNine = 1, Fractional = 1m };

        Assert.Equal(expected, new AllocationValidator().IsValid(allocation));
    }

    [Theory]
    [InlineData(int.MinValue, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public void RangeWithOnlyAMaximum_HasNoLowerBound(int value, bool expected) {
        var allocation = new Allocation { AtLeastOne = 1, AtMostNinetyNine = value, Fractional = 1m };

        Assert.Equal(expected, new AllocationValidator().IsValid(allocation));
    }

    [Fact]
    public void OneSidedRange_NamesOnlyTheBoundThatWasDeclared() {
        var result = new AllocationValidator().Validate(
            new Allocation { AtLeastOne = 0, AtMostNinetyNine = 1, Fractional = 1m });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.Range, error.Code);
        Assert.Equal("atLeastOne must be at least 1.", error.Message);
    }

    /// <summary>
    /// A fractional bound written as a numeric literal against a <c>decimal</c>. C# has no implicit
    /// double-to-decimal conversion, so this used to emit <c>fractional &lt; 0.5</c> and fail to
    /// compile - which is why the assertion that matters is that the comparison runs at all.
    /// </summary>
    [Theory]
    [InlineData("0.49", false)]
    [InlineData("0.5", true)]
    [InlineData("9.99", true)]
    [InlineData("10.00", false)]
    public void RangeWithAFractionalNumericBound_ComparesAgainstADecimal(string value, bool expected) {
        var allocation = new Allocation {
            AtLeastOne = 1,
            AtMostNinetyNine = 1,
            Fractional = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        };

        Assert.Equal(expected, new AllocationValidator().IsValid(allocation));
    }

    [Theory]
    [InlineData(1999, 12, 31, false)]
    [InlineData(2000, 1, 1, true)]      // the lower bound itself — inclusive
    [InlineData(2100, 12, 31, true)]    // the upper bound itself — inclusive
    [InlineData(2101, 1, 1, false)]
    public void DateOnlyBounds_AreInclusiveAndParsedInTheRightOrder(int year, int month, int day, bool expected) {
        // 2100-12-31 rather than 2100-01-31, so a day/month transposition fails here rather than
        // passing by coincidence.
        var booking = Valid() with { Starts = new DateOnly(year, month, day) };

        Assert.Equal(expected, new BookingValidator().IsValid(booking));
    }

    [Theory]
    [InlineData("-0.01", false)]
    [InlineData("0.00", true)]
    [InlineData("9.99", true)]
    [InlineData("10.00", false)]
    public void DecimalBounds_KeepTheirPrecision(string price, bool expected) {
        // The suffix matters: without it the bound is a double, and 9.99 as a double is not 9.99.
        var booking = Valid() with { Price = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture) };

        Assert.Equal(expected, new BookingValidator().IsValid(booking));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(86_399, true)]
    [InlineData(86_400, false)]
    public void TimeSpanBounds_AreParsedAsAnElapsedDuration(int seconds, bool expected) {
        var booking = Valid() with { Window = TimeSpan.FromSeconds(seconds) };

        Assert.Equal(expected, new BookingValidator().IsValid(booking));
    }

    [Fact]
    public void ExclusiveUpperBound_RejectsTheBoundItself() {
        Assert.False(new BookingValidator().IsValid(Valid() with { Effective = new DateTime(2100, 1, 1) }));
        Assert.True(new BookingValidator().IsValid(Valid() with { Effective = new DateTime(2099, 12, 31) }));
    }

    [Fact]
    public void OutOfRange_ReportsTheRangeCode() {
        var result = new BookingValidator().Validate(Valid() with { Starts = new DateOnly(1999, 1, 1) });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.Range, error.Code);
        Assert.Equal("starts", error.Field);
    }

    [Fact]
    public void OutOfRange_RendersTheBoundsInTheMessage() {
        // The message and the comparison take the same expression, so this also pins that they
        // cannot disagree about what the bound is.
        var result = new BookingValidator().Validate(Valid() with { Starts = new DateOnly(1999, 1, 1) });

        var message = Assert.Single(result.Errors).Message;

        Assert.Contains("2000", message);
        Assert.Contains("2100", message);
    }

    [Fact]
    public void EveryBoundedPropertyFailsIndependently() {
        var booking = new Booking {
            Starts = new DateOnly(1999, 1, 1),
            Price = 99.99m,
            Window = TimeSpan.FromDays(2),
            Effective = new DateTime(1999, 1, 1),
        };

        var result = new BookingValidator().Validate(booking);

        Assert.Equal(4, result.Errors.Count);
        Assert.All(result.Errors, error => Assert.Equal(ValidationCodes.Range, error.Code));
    }
}
