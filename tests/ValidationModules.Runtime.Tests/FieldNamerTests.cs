using ValidationModules.Naming;
using Xunit;

namespace ValidationModules.Runtime.Tests;

public class FieldNamerTests {

    [Theory]
    [InlineData("PostalCode", "postalCode")]
    [InlineData("Name", "name")]
    [InlineData("URL", "uRL")]
    [InlineData("alreadyCamel", "alreadyCamel")]
    [InlineData("", "")]
    public void CamelCase_ToFieldName(string input, string expected) {
        Assert.Equal(expected, CamelCaseFieldNamer.Instance.ToFieldName(input));
    }

    [Theory]
    [InlineData("PostalCode", "postal_code")]
    [InlineData("Name", "name")]
    [InlineData("HTTPStatus", "http_status")]
    [InlineData("ID", "id")]
    [InlineData("PostalCodeID", "postal_code_id")]
    [InlineData("", "")]
    public void SnakeCase_ToFieldName(string input, string expected) {
        Assert.Equal(expected, SnakeCaseFieldNamer.Instance.ToFieldName(input));
    }

    [Fact]
    public void PascalCase_ToFieldName_IsIdentity() {
        Assert.Equal("PostalCode", PascalCaseFieldNamer.Instance.ToFieldName("PostalCode"));
    }

    [Fact]
    public void Combine_AtTheRoot_HasNoLeadingSeparator() {
        Assert.Equal("home", CamelCaseFieldNamer.Instance.Combine("", "home"));
    }

    [Fact]
    public void Combine_NestedPath_IsDotted() {
        Assert.Equal("home.postalCode", CamelCaseFieldNamer.Instance.Combine("home", "postalCode"));
    }

    [Fact]
    public void CombineIndex_MatchesWhatTheContextProduces() {
        // The adapter and the generated engine have to agree on this exactly, or the same field
        // gets two spellings depending on which engine found the error.
        Assert.Equal("toys[3]", CamelCaseFieldNamer.Instance.CombineIndex("", "toys", 3));
        Assert.Equal("pet.toys[3]", CamelCaseFieldNamer.Instance.CombineIndex("pet", "toys", 3));
    }
}
