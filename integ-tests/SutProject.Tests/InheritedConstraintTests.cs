using SutProject.Inheritance;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Constraints declared on a base type or an interface are enforced by the validator for the type
/// that inherits them, against really-compiled generated code.
/// </summary>
/// <remarks>
/// The counts are the point. Each of these used to report exactly one error - whatever the
/// most-derived type declared for itself - and pass everything the base said, on a clean build.
/// </remarks>
public class InheritedConstraintTests {

    [Fact]
    public void BaseClassConstraints_AreEnforcedOnTheDerivedType() {
        var errors = new CreateOrderValidator().Validate(new CreateOrder()).Errors;

        Assert.Equal(
            ["correlationId", "tenantId", "sku"],
            errors.Select(error => error.Field));
    }

    [Fact]
    public void InterfaceConstraints_AreEnforcedOnTheImplementingType() {
        var errors = new DocumentValidator().Validate(new Document()).Errors;

        Assert.Equal(["title", "modifiedBy"], errors.Select(error => error.Field));
    }

    [Fact]
    public void PlainClassHierarchy_EnforcesBothLevels() {
        var errors = new DerivedDtoValidator().Validate(new DerivedDto()).Errors;

        Assert.Equal(["a", "b"], errors.Select(error => error.Field));
    }

    [Fact]
    public void DerivedTypeAddingNothing_StillValidatesWhatItInherited() {
        var errors = new PingValidator().Validate(new Ping()).Errors;

        Assert.Equal(["correlationId", "tenantId"], errors.Select(error => error.Field));
    }

    /// <summary>
    /// An interface's constraint and the implementer's own both apply to the same field.
    /// </summary>
    [Fact]
    public void InterfaceAndImplementerConstraints_BothApply() {
        var validator = new EnvelopeValidator();

        Assert.Equal(
            ValidationCodes.Required,
            Assert.Single(validator.Validate(new Envelope()).Errors).Code);

        Assert.Equal(
            ValidationCodes.StringLength,
            Assert.Single(validator.Validate(new Envelope { Stamp = "ab" }).Errors).Code);

        Assert.True(validator.Validate(new Envelope { Stamp = "abcd" }).IsValid);
    }

    /// <summary>
    /// Inherited fields report before the type's own, which is the order the two declarations read
    /// in and what §4.2 guarantees.
    /// </summary>
    [Fact]
    public void InheritedFields_ReportBeforeTheTypesOwn() {
        var errors = new CreateOrderValidator().Validate(new CreateOrder()).Errors;

        Assert.Equal("correlationId", errors[0].Field);
        Assert.Equal("sku", errors[^1].Field);
    }

    [Fact]
    public void IsValid_AgreesWithValidateOnInheritedConstraints() {
        var validator = new CreateOrderValidator();

        Assert.False(validator.IsValid(new CreateOrder { Sku = "X" }));
        Assert.True(validator.IsValid(new CreateOrder {
            CorrelationId = "c", TenantId = "t", Sku = "X",
        }));
    }
}
