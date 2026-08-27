using SutProject.Polymorphic;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Polymorphic descent against really-compiled generated code.
/// </summary>
public class PolymorphicDescentTests {

    private static readonly CheckoutValidator Dispatching = new();
    private static readonly DeclaredOnlyCheckoutValidator Declared = new();

    /// <summary>
    /// The probe that found the defect: a card with a short PAN, reached through a property typed
    /// as the base. This used to report nothing at all.
    /// </summary>
    [Fact]
    public void SubtypeRules_RunUnderCompileTimeDispatch() {
        var checkout = new Checkout { Payment = new Card { Currency = "GBP", Pan = "123" } };

        var error = Assert.Single(Dispatching.Validate(checkout).Errors);

        Assert.Equal("payment.pan", error.Field);
        Assert.Equal(ValidationCodes.StringLength, error.Code);
    }

    /// <summary>
    /// The same value under <c>DeclaredOnly</c>, which is today's behaviour chosen deliberately
    /// rather than fallen into.
    /// </summary>
    [Fact]
    public void SubtypeRules_DoNotRunUnderDeclaredOnly() {
        var checkout = new DeclaredOnlyCheckout { Payment = new Card { Currency = "GBP", Pan = "123" } };

        Assert.True(Declared.Validate(checkout).IsValid);
    }

    /// <summary>
    /// The base's own constraints still apply to a subtype, because the subtype's validator
    /// inherits them - which is also why the declared-type validator must not run as well.
    /// </summary>
    [Fact]
    public void BaseConstraints_ApplyToASubtypeAndAreReportedOnce() {
        var checkout = new Checkout { Payment = new Card { Currency = null, Pan = "1234567890123456" } };

        Assert.Equal(
            ValidationCodes.Required,
            Assert.Single(Dispatching.Validate(checkout).Errors).Code);
    }

    /// <summary>
    /// Two levels down, which is the arm that a wrongly ordered switch would never reach.
    /// </summary>
    [Fact]
    public void TheMostDerivedArmWins() {
        var checkout = new Checkout {
            Payment = new Premium { Currency = "GBP", Pan = "1234567890123456", Concierge = null },
        };

        Assert.Equal(
            "payment.concierge",
            Assert.Single(Dispatching.Validate(checkout).Errors).Field);
    }

    [Fact]
    public void ASiblingSubtypeDispatchesToItsOwnValidator() {
        var checkout = new Checkout { Payment = new Bank { Currency = "GBP", Iban = null } };

        Assert.Equal("payment.iban", Assert.Single(Dispatching.Validate(checkout).Errors).Field);
    }

    [Fact]
    public void AValidSubtypeValueReportsNothing() {
        var checkout = new Checkout {
            Payment = new Premium { Currency = "GBP", Pan = "1234567890123456", Concierge = "Ada" },
        };

        Assert.True(Dispatching.Validate(checkout).IsValid);
        Assert.True(Dispatching.IsValid(checkout));
    }

    [Fact]
    public void IsValid_AgreesWithValidateOnADispatchedDescent() {
        var checkout = new Checkout { Payment = new Card { Currency = "GBP", Pan = "123" } };

        Assert.False(Dispatching.IsValid(checkout));
    }

    [Fact]
    public void CollectionElements_DispatchIndividually() {
        var basket = new Basketful {
            Payments = {
                new Card { Currency = "GBP", Pan = "123" },
                new Bank { Currency = "GBP", Iban = null },
            },
        };

        Assert.Equal(
            ["payments[0].pan", "payments[1].iban"],
            new BasketfulValidator().Validate(basket).Errors.Select(error => error.Field));
    }
}
