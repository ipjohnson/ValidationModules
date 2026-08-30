using SutProject.DataAnnotations;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Exercises validators generated from <c>System.ComponentModel.DataAnnotations</c> attributes
/// alone, against a model that imports nothing from <c>ValidationModules.Constraints</c>.
/// </summary>
/// <remarks>
/// That is the on-ramp the DataAnnotations front end exists for. It also means no
/// <c>ValidationAttribute</c> is constructed and no <c>IsValid</c> is called anywhere in this
/// path - the arguments were read out of metadata at build time and compiled.
/// </remarks>
public class DataAnnotationsFrontEndTests {

    [Fact]
    public void Generator_ProducedAValidatorFromDataAnnotationsAlone() {
        Assert.NotNull(new CustomerValidator());
    }

    [Fact]
    public void Validate_CleanValue_IsValid() {
        Assert.True(new CustomerValidator().IsValid(ValidCustomer()));
    }

    [Fact]
    public void Required_TreatsWhitespaceAsMissing() {
        // DataAnnotations trims before testing, and the compiled form matches it.
        var result = new CustomerValidator().Validate(ValidCustomer(customer => customer.Name = "   "));

        Assert.Equal(ValidationCodes.Required, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void StringLength_ReadsBothBoundsIncludingMinimumLength() {
        var tooShort = new CustomerValidator().Validate(ValidCustomer(c => c.Name = "a"));

        var error = Assert.Single(tooShort.Errors);
        Assert.Equal(ValidationCodes.StringLength, error.Code);
        Assert.Equal("name must be between 2 and 20 characters.", error.Message);
    }

    [Fact]
    public void RegularExpression_IsAnchored() {
        // The divergence from the native [Pattern], and the reason they are two IR states rather
        // than one. DataAnnotations requires the whole value to match, so an embedded match fails.
        Assert.True(new CustomerValidator().IsValid(ValidCustomer(c => c.Code = "ABC")));
        Assert.False(new CustomerValidator().IsValid(ValidCustomer(c => c.Code = "xABCx")));
    }

    [Fact]
    public void RegularExpression_AnchoringRejectsATrailingNewline() {
        // \z rather than $, which would otherwise admit "ABC\n".
        Assert.False(new CustomerValidator().IsValid(ValidCustomer(c => c.Code = "ABC\n")));
    }

    [Fact]
    public void Range_MapsToTheSameConstraintAsTheNativeAttribute() {
        var result = new CustomerValidator().Validate(ValidCustomer(c => c.Age = 0));

        Assert.Equal("age must be between 1 and 120.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void MaxLength_OnACollection_BecomesAnItemCountConstraint() {
        // [MaxLength] applies to strings and collections in DataAnnotations; the member's type is
        // what decides which constraint it compiles to.
        var result = new CustomerValidator().Validate(
            ValidCustomer(c => c.Tags = new List<string> { "a", "b", "c", "d" }));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.ArrayBounds, error.Code);
        Assert.Equal("tags must be at most 3 items.", error.Message);
    }

    [Fact]
    public void AllowedValues_MapsAcross() {
        var result = new CustomerValidator().Validate(ValidCustomer(c => c.Tier = "bronze"));

        Assert.Equal(ValidationCodes.Enum, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void FieldNames_UseTheSamePolicyAsNativeConstraints() {
        var result = new CustomerValidator().Validate(ValidCustomer(c => c.Name = null));

        Assert.Equal("name", Assert.Single(result.Errors).Field);
    }

    private static Customer ValidCustomer(Action<Customer>? mutate = null) {
        var customer = new Customer {
            Name = "Ada",
            Code = "ABC",
            Age = 40,
            Tags = new List<string> { "a" },
            Tier = "gold",
        };

        mutate?.Invoke(customer);

        return customer;
    }
}
