using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// One rules class describing two targets, run through the real emitted code: two validators, one
/// shared companion of <c>Describe</c> overloads, one shared constant.
/// </summary>
public class MultiTargetRulesTests {

    [Fact]
    public void EachTarget_GetsItsOwnValidator() {
        Assert.True(new InvoiceValidator().Validate(new Invoice { Number = "1234567890" }).IsValid);
        Assert.True(new CreditNoteValidator().Validate(
            new CreditNote { Number = "1234567890", Amount = 5m }).IsValid);
    }

    [Fact]
    public void EachRegion_CarriesItsOwnRules() {
        var invoice = new InvoiceValidator().Validate(new Invoice { Number = "short" });

        Assert.Equal(
            [("number", ValidationCodes.StringLength)],
            invoice.Errors.Select(error => (error.Field, error.Code)));

        var creditNote = new CreditNoteValidator().Validate(
            new CreditNote { Number = "short", Amount = 0m });

        Assert.Equal(
            [
                ("number", ValidationCodes.StringLength),
                ("amount", "positive"),
            ],
            creditNote.Errors.Select(error => (error.Field, error.Code)));
    }

    /// <summary>
    /// The shared constant is one declaration serving both regions - baked by value into each,
    /// exactly as in a single-target class.
    /// </summary>
    [Fact]
    public void TheSharedConstant_GovernsBothTargets() {
        Assert.False(new InvoiceValidator().IsValid(new Invoice { Number = "123456789" }));
        Assert.False(new CreditNoteValidator().IsValid(
            new CreditNote { Number = "123456789", Amount = 5m }));
    }
}
