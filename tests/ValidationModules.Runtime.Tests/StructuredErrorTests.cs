using System.Globalization;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The structured error shape from docs/structured-errors.md: the message is data until someone
/// reads it, the value is captured but never rendered by any default, and the read side decides
/// the text.
/// </summary>
public class StructuredErrorTests {

    private static ValidationError Single(Action<ValidationContext> report) {
        var collector = new ValidationErrorCollector();

        report(new ValidationContext(collector));

        return Assert.Single(collector.ToResult().Errors);
    }

    // -- Rendering ------------------------------------------------------------------------------

    [Fact]
    public void StructuredError_RendersItsMessageOnRead() {
        var error = new ValidationError(
            "name", ValidationCodes.StringLength, "ab",
            new ValidationMessageInfo(ValidationMessageTemplates.StringLengthBetween, 3, 50));

        Assert.Equal("name must be between 3 and 50 characters.", error.Message);
        Assert.Equal(error.Message, error.Message);
    }

    [Fact]
    public void FinishedStringError_KeepsItsTextAndCannotReRender() {
        var error = new ValidationError("name", "custom", "exactly this text");

        Assert.Equal("exactly this text", error.Message);
        Assert.Null(error.MessageInfo);
    }

    [Fact]
    public void NestedPath_RendersTheLeafFieldOnly() {
        // The composed helpers always built the message from the site's own field name; the path
        // belongs to Field, not to prose.
        var error = new ValidationError(
            "toys[3].name", ValidationCodes.Required, null, ValidationMessageInfo.Required);

        Assert.Equal("name is required.", error.Message);
    }

    [Fact]
    public void Deconstruct_StillAnswersTheOldPositionalShape() {
        var (field, code, message) = new ValidationError(
            "age", ValidationCodes.Range, 99,
            new ValidationMessageInfo(ValidationMessageTemplates.RangeBetween, 0, 30));

        Assert.Equal("age", field);
        Assert.Equal("range", code);
        Assert.Equal("age must be between 0 and 30.", message);
    }

    [Fact]
    public void ArgumentFormatting_IsInvariantByDefault_AndTakesAProviderOnRequest() {
        var error = new ValidationError(
            "price", ValidationCodes.MultipleOf, null,
            new ValidationMessageInfo(ValidationMessageTemplates.MultipleOf, 0.05m));

        Assert.Equal("price must be a multiple of 0.05.", error.Message);
        Assert.Equal(
            "price must be a multiple of 0,05.",
            error.MessageInfo!.Render(in error, CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void Renderer_HonoursEscapes_AndLeavesUnknownHolesVerbatim() {
        var error = new ValidationError(
            "name", "custom", null,
            new ValidationMessageInfo("{{literal}} {field} {9} {nope}", "unused"));

        Assert.Equal("{literal} name {9} {nope}", error.Message);
    }

    // -- The value: captured, never rendered by a default ---------------------------------------

    [Fact]
    public void Helpers_CaptureTheValue_AndNoDefaultSurfaceRendersIt() {
        var error = Single(context => context.ReportPattern("sku", value: "SECRET-123"));

        Assert.Equal("SECRET-123", error.Value);
        Assert.DoesNotContain("SECRET-123", error.Message);
        Assert.DoesNotContain("SECRET-123", error.ToString());
    }

    [Fact]
    public void ToString_IsFieldCodeMessage() {
        var error = new ValidationError(
            "name", ValidationCodes.Required, "who cares", ValidationMessageInfo.Required);

        Assert.Equal("name: required - name is required.", error.ToString());
    }

    [Fact]
    public void ValidationException_NeverRendersTheValue() {
        var result = ValidationResult.FromErrors([
            new ValidationError("name", ValidationCodes.Required, "SECRET", ValidationMessageInfo.Required),
        ]);

        Assert.DoesNotContain("SECRET", new ValidationException(result).Message);
    }

    // -- Equality --------------------------------------------------------------------------------

    [Fact]
    public void SameSiteFailures_CompareEqual() {
        var info = new ValidationMessageInfo(ValidationMessageTemplates.RangeBetween, 0, 30);
        var first = new ValidationError("age", ValidationCodes.Range, 42, info);
        var second = new ValidationError("age", ValidationCodes.Range, 42, info);

        // Same shared info reference; the boxed ints compare by value through
        // EqualityComparer<object>.
        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentSites_AreDifferentErrors_EvenWhenTheTextMatches() {
        var first = new ValidationError(
            "age", ValidationCodes.Range, null,
            new ValidationMessageInfo(ValidationMessageTemplates.RangeBetween, 0, 30));
        var second = new ValidationError(
            "age", ValidationCodes.Range, null,
            new ValidationMessageInfo(ValidationMessageTemplates.RangeBetween, 0, 30));

        Assert.Equal(first.Message, second.Message);
        Assert.NotEqual(first, second);
    }

    // -- The read side ---------------------------------------------------------------------------

    [Fact]
    public void MessageMap_OverridesExactlyTheMappedCodes() {
        var map = new ValidationMessageMap()
            .Map(ValidationCodes.Required, static (in ValidationError error) => $"{error.Field} est obligatoire.");

        var required = new ValidationError("name", ValidationCodes.Required, null, ValidationMessageInfo.Required);
        var pattern = new ValidationError("sku", ValidationCodes.Pattern, null, ValidationMessageInfo.Pattern);

        Assert.Equal("name est obligatoire.", required.ToMessage(map));
        Assert.Equal("sku is not in the required format.", pattern.ToMessage(map));
    }

    [Fact]
    public void MessageMap_DispatchesUserCodes_LikeBuiltIns() {
        var map = new ValidationMessageMap()
            .Map("date_order", static (in ValidationError _) => "la date de fin doit suivre la date de début.");

        var error = new ValidationError("endDate", "date_order", "endDate >= startDate.");

        Assert.Equal("la date de fin doit suivre la date de début.", error.ToMessage(map));
    }

    [Fact]
    public void Formatter_IsTheOneWayAValueReachesText() {
        var diagnostic = new ValidationMessageMap()
            .Map(ValidationCodes.Pattern, static (in ValidationError error) =>
                $"'{error.Value}' is not in the required format.");

        var error = Single(context => context.ReportPattern("sku", value: "SUMMER!!"));

        Assert.Equal("'SUMMER!!' is not in the required format.", error.ToMessage(diagnostic));
        Assert.Equal("sku is not in the required format.", error.Message);
    }

    [Fact]
    public void Provider_IsReadPerRender_AndRendersDataAnnotationsHoles() {
        // The resx shape: the template is read on every render, which is what lets
        // CurrentUICulture and the satellite fallback chain do their work - and its holes are
        // DataAnnotations' own dialect, {0} the field and {1}… this info's arguments.
        var reads = 0;
        var info = new ValidationMessageInfo(ValidationMessageTemplates.StringLengthAtMost, 10) {
            Provider = new DelegateMessageProvider(() => {
                reads++;
                return "The {0} field wants at most {1}.";
            }),
            DataAnnotationsHoles = true,
        };

        var error = new ValidationError("name", ValidationCodes.StringLength, null, info);

        Assert.Equal("The name field wants at most 10.", error.Message);
        Assert.Equal("The name field wants at most 10.", error.Message);
        Assert.Equal(2, reads);
    }

    // -- The new wordings ------------------------------------------------------------------------

    [Fact]
    public void DeniedValues_SayMustNot() {
        var error = Single(context => context.ReportDeniedValues("role", "admin, root"));

        Assert.Equal(ValidationCodes.Enum, error.Code);
        Assert.Equal("role must not be one of: admin, root.", error.Message);
    }

    [Theory]
    [InlineData(false, false, "age must be between 1 and 10.")]
    [InlineData(true, false, "age must be greater than 1 and at most 10.")]
    [InlineData(false, true, "age must be at least 1 and less than 10.")]
    [InlineData(true, true, "age must be greater than 1 and less than 10.")]
    public void ExclusiveRangeBounds_FinallySaySo(bool exclusiveMin, bool exclusiveMax, string expected) {
        var error = Single(context =>
            context.ReportRange("age", 1, 10, exclusiveMin: exclusiveMin, exclusiveMax: exclusiveMax));

        Assert.Equal(expected, error.Message);
    }

    [Theory]
    [InlineData(false, "age must be at least 18.")]
    [InlineData(true, "age must be greater than 18.")]
    public void ExclusiveLowerBoundAlone_SaysGreaterThan(bool exclusive, string expected) {
        var error = Single(context => context.ReportRangeAtLeast("age", 18, exclusive: exclusive));

        Assert.Equal(expected, error.Message);
    }

    [Theory]
    [InlineData(false, "age must be at most 65.")]
    [InlineData(true, "age must be less than 65.")]
    public void ExclusiveUpperBoundAlone_SaysLessThan(bool exclusive, string expected) {
        var error = Single(context => context.ReportRangeAtMost("age", 65, exclusive: exclusive));

        Assert.Equal(expected, error.Message);
    }
}
