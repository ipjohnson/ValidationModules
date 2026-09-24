using Microsoft.Extensions.DependencyInjection;
using SutProject.FieldNaming;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// The field is the wire name on every path, and <c>[Display(Name)]</c> only labels the message.
/// </summary>
/// <remarks>
/// A runner carries services, so its pass has the registered field namer. These run through one
/// and through the validator alone, because the two used to disagree: the namer respelled names
/// the generator had already resolved.
/// </remarks>
public class FieldNamingTests
{
    private static ValidationResult ThroughTheRunner<T>(T value)
    {
        var services = new ServiceCollection();

        services.AddSutProjectValidators();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<ValidationRunner<T>>().Validate(value);
    }

    [Fact]
    public void ADisplayName_LabelsTheMessage_AndTheFieldIsTheWireName()
    {
        var error = Assert.Single(
            new ContactValidator().Validate(Contact() with { PostalCode = null }).Errors
        );

        Assert.Equal("postalCode", error.Field);
        Assert.Equal("Postal code is required.", error.Message);
    }

    [Fact]
    public void AnAuthoredMessage_NamesTheFieldByItsLabel()
    {
        var error = Assert.Single(
            new ContactValidator().Validate(Contact() with { Nickname = null }).Errors
        );

        Assert.Equal("nickname", error.Field);
        Assert.Equal("Nick name needs a value.", error.Message);
    }

    [Fact]
    public void ResolvedNames_ReachTheErrorUnchanged_WithAndWithoutARunner()
    {
        var empty = new Contact();

        var direct = new ContactValidator().Validate(empty).Errors.Select(e => e.Field);
        var runner = ThroughTheRunner(empty).Errors.Select(e => e.Field);

        Assert.Equal(["postalCode", "GivenName", "family_name", "nickname"], direct);
        Assert.Equal(direct, runner);
    }

    [Fact]
    public void AValidatableObjectsMemberName_TakesTheResolvedFieldName()
    {
        var error = Assert.Single(ThroughTheRunner(new Registrant()).Errors);

        Assert.Equal("given_name", error.Field);
    }

    [Fact]
    public void ACustomValidationResultsMemberName_TakesTheResolvedFieldName()
    {
        var error = Assert.Single(ThroughTheRunner(new Applicant { FamilyName = "x" }).Errors);

        Assert.Equal("family_name", error.Field);
    }

    [Fact]
    public void AResourceMessage_FillsZeroWithTheDisplayName()
    {
        var messages = new SignatoryValidator()
            .Validate(new Signatory())
            .Errors.Select(e => (e.Field, e.Message));

        Assert.Equal(
            [("lastName", "LastName is missing"), ("firstName", "First name is missing")],
            messages
        );
    }

    [Fact]
    public void AnEnsureMessage_NamesMembersByTheirFieldNames()
    {
        var stay = new Stay
        {
            StartDate = new DateOnly(2024, 5, 2),
            EndDate = new DateOnly(2024, 5, 1),
        };

        var error = Assert.Single(new StayValidator().Validate(stay).Errors);

        Assert.Equal("start_date", error.Field);
        Assert.Equal("start_date_less_than_end_date", error.Code);
        Assert.Equal("start_date < end_date.", error.Message);
    }

    [Fact]
    public void ARulesClassRule_LabelsItsMessageToo()
    {
        var missing = Assert.Single(new ParcelValidator().Validate(new Parcel()).Errors);
        var tooShort = Assert.Single(
            new ParcelValidator().Validate(new Parcel { TrackingNumber = "AB" }).Errors
        );

        Assert.Equal("trackingNumber", missing.Field);
        Assert.Equal("Tracking number is required.", missing.Message);
        Assert.Equal("Tracking number must be between 5 and 20 characters.", tooShort.Message);
    }

    private static Contact Contact() =>
        new()
        {
            PostalCode = "12345",
            GivenName = "Ada",
            FamilyName = "Lovelace",
            Nickname = "ada",
        };
}
