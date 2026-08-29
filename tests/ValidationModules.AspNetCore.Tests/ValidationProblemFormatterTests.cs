using ValidationModules.AspNetCore;
using Xunit;

namespace ValidationModules.AspNetCore.Tests;

/// <summary>
/// The HTTP boundary's read-side render: a formatter on the options rewrites the <c>errors</c>
/// object, and the <c>validationCodes</c> extension stays exactly what it was - the stable
/// vocabulary must not depend on how prose was rendered.
/// </summary>
public class ValidationProblemFormatterTests {

    private static ValidationResult TwoFailures() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.ReportRequired("name", value: null);
        context.ReportStringLength("nickname", 1, 20, value: "much-too-long-for-anyone");

        return collector.ToResult();
    }

    [Fact]
    public void NoFormatter_KeepsTheDefaultRender() {
        var errors = ValidationProblem.ToDictionary(TwoFailures());

        Assert.Equal("name is required.", Assert.Single(errors["name"]));
        Assert.Equal("nickname must be between 1 and 20 characters.", Assert.Single(errors["nickname"]));
    }

    [Fact]
    public void Formatter_RewritesMessages_AndTheCodesStayPut() {
        var options = new ValidationProblemOptions {
            MessageFormatter = new ValidationMessageMap()
                .Map(ValidationCodes.Required, static (in ValidationError error) => $"{error.Field} est obligatoire."),
        };

        var result = TwoFailures();
        var errors = ValidationProblem.ToDictionary(result, options);
        var codes = ValidationProblem.ToCodeDictionary(result, options);

        Assert.Equal("name est obligatoire.", Assert.Single(errors["name"]));
        // Unmapped codes keep the default render, per the map's fallback.
        Assert.Equal("nickname must be between 1 and 20 characters.", Assert.Single(errors["nickname"]));
        Assert.Equal("required", Assert.Single(codes["name"]));
        Assert.Equal("string_length", Assert.Single(codes["nickname"]));
    }

    [Fact]
    public void Formatter_IsWhereAResponseOptsIntoValues() {
        var options = new ValidationProblemOptions {
            MessageFormatter = new ValidationMessageMap()
                .Map(ValidationCodes.StringLength, static (in ValidationError error) =>
                    $"'{error.Value}' does not fit {error.Field}."),
        };

        var errors = ValidationProblem.ToDictionary(TwoFailures(), options);

        Assert.Equal("'much-too-long-for-anyone' does not fit nickname.", Assert.Single(errors["nickname"]));

        // And without that deliberate opt-in, the value appears nowhere in a body.
        var defaults = ValidationProblem.ToDictionary(TwoFailures());
        Assert.All(defaults.Values, messages =>
            Assert.All(messages, message => Assert.DoesNotContain("much-too-long", message)));
    }
}
