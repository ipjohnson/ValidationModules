using SutProject.ObjectLevel;
using ValidationModules;
using Xunit;
using DA = System.ComponentModel.DataAnnotations;

namespace SutProject.Tests;

/// <summary>
/// The object-level DataAnnotations rules - attributes on the class, then
/// <c>IValidatableObject.Validate</c> - run in <c>Validator.TryValidateObject</c>'s order, and
/// only when the object's own validator found nothing blocking.
/// </summary>
/// <remarks>
/// Compiled by the real generator against the real runtime, so the gate these assert is the one
/// the emitted <c>ctx.Mark()</c> and <c>HasBlockingErrorsSince</c> implement.
/// </remarks>
public class ObjectLevelRuleTests
{
    [Fact]
    public void ValidatableObject_RunsWhenItsOwnRulesReportOnlyAWarning()
    {
        var appointment = new Appointment();

        var result = new AppointmentValidator().Validate(appointment);

        Assert.True(appointment.ObjectRuleRan);
        Assert.Collection(
            result.Errors,
            warning =>
            {
                Assert.Equal("note_missing", warning.Code);
                Assert.Equal(ValidationSeverity.Warning, warning.Severity);
            },
            error =>
            {
                Assert.Equal(ValidationCodes.Custom, error.Code);
                Assert.Equal("the appointment is not confirmed", error.Message);
            }
        );
    }

    [Fact]
    public void ValidatableObject_OnANestedObject_RunsDespiteAnErrorOnItsParent()
    {
        var shipment = new Shipment { Destination = new Destination { Street = "1 Main St" } };

        var result = new ShipmentValidator().Validate(shipment);

        Assert.True(shipment.Destination.ObjectRuleRan);
        Assert.Equal(["reference", "destination.street"], result.Errors.Select(e => e.Field));
        Assert.Equal("the destination is not served", result.Errors[1].Message);
    }

    [Fact]
    public void ValidatableObject_IsSkippedWhenItsOwnRulesReportAnError()
    {
        var shipment = new Shipment { Reference = "S-1", Destination = new Destination() };

        var result = new ShipmentValidator().Validate(shipment);

        Assert.False(shipment.Destination.ObjectRuleRan);
        Assert.Equal("destination.street", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void ClassLevelAttribute_RunsWhenThePropertyRulesPass_AndMatchesDataAnnotations()
    {
        var stay = new Stay
        {
            Guest = "Ada",
            Start = new DateOnly(2024, 5, 2),
            End = new DateOnly(2024, 5, 1),
        };

        var error = Assert.Single(new StayValidator().Validate(stay).Errors);

        // Reported against the object itself: the attribute's result names no member.
        Assert.Equal(string.Empty, error.Field);
        Assert.Equal(ValidationCodes.Custom, error.Code);
        Assert.Equal("The stay must end after it starts.", error.Message);

        var results = new List<DA.ValidationResult>();

        Assert.False(
            DA.Validator.TryValidateObject(stay, new DA.ValidationContext(stay), results, true)
        );
        Assert.Equal(error.Message, Assert.Single(results).ErrorMessage);
    }

    [Fact]
    public void ClassLevelAttribute_ThatFails_HoldsBackValidatableObject()
    {
        var stay = new Stay
        {
            Guest = "Ada",
            Start = new DateOnly(2024, 5, 2),
            End = new DateOnly(2024, 5, 1),
        };

        new StayValidator().Validate(stay);

        Assert.False(stay.ObjectRuleRan);
    }

    [Fact]
    public void ClassLevelAttribute_IsSkippedWhenAPropertyRuleFails()
    {
        var stay = new Stay { Start = new DateOnly(2024, 5, 2), End = new DateOnly(2024, 5, 1) };

        var error = Assert.Single(new StayValidator().Validate(stay).Errors);

        Assert.Equal("guest", error.Field);
        Assert.Equal(ValidationCodes.Required, error.Code);
        Assert.False(stay.ObjectRuleRan);
    }

    [Fact]
    public void ClassLevelAttribute_ThatPasses_LetsValidatableObjectRun()
    {
        var stay = new Stay
        {
            Guest = "Ada",
            Start = new DateOnly(2024, 5, 1),
            End = new DateOnly(2024, 5, 2),
        };

        Assert.Empty(new StayValidator().Validate(stay).Errors);
        Assert.True(stay.ObjectRuleRan);
    }

    [Fact]
    public void ClassLevelCustomValidation_CallsTheMethodWithTheObject_AsDataAnnotationsDoes()
    {
        var party = new Party();

        var error = Assert.Single(new PartyValidator().Validate(party).Errors);

        Assert.Equal("guests", error.Field);
        Assert.Equal(ValidationCodes.Custom, error.Code);
        Assert.Equal("Party needs a guest.", error.Message);

        var results = new List<DA.ValidationResult>();

        Assert.False(
            DA.Validator.TryValidateObject(party, new DA.ValidationContext(party), results, true)
        );

        var expected = Assert.Single(results);

        Assert.Equal(error.Message, expected.ErrorMessage);
        Assert.Equal(["Guests"], expected.MemberNames);
    }
}
