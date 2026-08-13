using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The throwing route, which exists so a framework needs one mapper rather than two shapes.
/// </summary>
/// <remarks>
/// Plan §12 Q4 resolved to throw, and Hardened's <c>IExceptionToModelConverter</c> is the consumer.
/// Both routes — a hand-written <c>ValidateAndThrow</c> and the request filter — end at the same
/// <see cref="ValidationResult"/>, so what matters here is that the result survives the throw
/// intact and that the message is usable in a log without unpacking it.
/// </remarks>
public class ValidationExceptionTests {

    private static ValidationResult Failing(params (string Field, string Code)[] errors) {
        var collector = new ValidationErrorCollector();

        foreach (var (field, code) in errors) {
            collector.Add(new ValidationError(field, code, $"{field} is not acceptable."));
        }

        return collector.ToResult();
    }

    [Fact]
    public void Result_IsTheResultItWasGiven() {
        var result = Failing(("name", ValidationCodes.Required));

        Assert.Same(result, new ValidationException(result).Result);
    }

    [Fact]
    public void Message_NamesTheFirstFailure() {
        var exception = new ValidationException(Failing(("name", ValidationCodes.Required)));

        Assert.Contains("name", exception.Message);
        Assert.Contains(ValidationCodes.Required, exception.Message);
    }

    [Fact]
    public void Message_CountsTheRemainderRatherThanListingEveryFailure() {
        // A message is for a log line. The full set is on Result for anything that wants it.
        var exception = new ValidationException(Failing(
            ("name", ValidationCodes.Required),
            ("age", ValidationCodes.Range),
            ("sku", ValidationCodes.Pattern)));

        Assert.Contains("name", exception.Message);
        Assert.Contains("2 more", exception.Message);
        Assert.DoesNotContain("sku", exception.Message);
    }

    [Fact]
    public void Message_ForOneFailure_DoesNotSayAndZeroMore() {
        Assert.DoesNotContain("more", new ValidationException(Failing(("name", ValidationCodes.Required))).Message);
    }

    [Fact]
    public void EmptyResult_StillProducesAMessage() {
        // Not a shape anything produces, but the constructor must not index into an empty list.
        var exception = new ValidationException(new ValidationErrorCollector().ToResult());

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public void NullResult_Throws() {
        Assert.Throws<ArgumentNullException>(() => new ValidationException(null!));
    }

    [Fact]
    public void IsAnException_SoOrdinaryCatchClausesWork() {
        var caught = Record.Exception(void () => {
            throw new ValidationException(Failing(("name", ValidationCodes.Required)));
        });

        Assert.IsType<ValidationException>(caught);
    }
}
