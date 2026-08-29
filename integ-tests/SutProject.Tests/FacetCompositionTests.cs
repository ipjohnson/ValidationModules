using SutProject.Declared;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// <c>rules.As&lt;IAudited&gt;(x)</c> with the facet generated in this compilation: the static
/// binding, run through real emitted code.
/// </summary>
public class FacetCompositionTests {

    private static readonly IValidatorFor<Shipment> Validator = new ShipmentValidator();

    private static Shipment Valid() => new() { CreatedBy = "ada", Version = 1, Carrier = "DHL" };

    [Fact]
    public void AValueSatisfyingTheFacet_IsValid() {
        Assert.True(Validator.Validate(Valid()).IsValid);
    }

    /// <summary>
    /// The path does not push: facet fields report at the current level - <c>createdBy</c>, not
    /// <c>audited.createdBy</c> - because the facet validates the subject itself.
    /// </summary>
    [Fact]
    public void FacetErrors_ReportAtTheCurrentLevel() {
        var result = Validator.Validate(Valid() with { CreatedBy = null, Version = 0 });

        Assert.Equal(
            [
                ("createdBy", ValidationCodes.Required),
                ("version", ValidationCodes.Range),
            ],
            result.Errors.Select(error => (error.Field, error.Code)));
    }

    /// <summary>Body order holds: the rules class's own checks first, then the facet's.</summary>
    [Fact]
    public void FacetErrors_LandWhereTheAsWasWritten() {
        var result = Validator.Validate(new Shipment());

        Assert.Equal(
            ["carrier", "createdBy", "version"],
            result.Errors.Select(error => error.Field));
    }

    /// <summary>
    /// The facet's validator is its own - registering it separately composes on a facet descent
    /// exactly as any registered validator composes anywhere else.
    /// </summary>
    [Fact]
    public void TheFacetValidator_StandsAlone() {
        var audited = new IAuditedValidator();

        Assert.False(audited.IsValid(new Shipment { Version = 1 }));
        Assert.True(audited.IsValid(Valid()));
    }
}
