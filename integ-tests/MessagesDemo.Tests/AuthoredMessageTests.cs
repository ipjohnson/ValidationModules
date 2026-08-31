using System.Globalization;
using MessagesDemo;
using Microsoft.Extensions.DependencyInjection;
using ValidationModules;
using Xunit;

namespace MessagesDemo.Tests;

/// <summary>
/// A custom <c>Message</c> always wins over a language pack.
/// </summary>
/// <remarks>
/// The rule this replaces was one nobody could see: an authored message left
/// <c>MessageInfo</c> null, which made the pack's shape lookup unreachable, so the override
/// survived or died according to whether the pack happened to carry a bare code-level key. The
/// shipped <c>de</c> pack has a bare <c>required</c> and no bare <c>string_length</c>, so
/// <c>[Required(Message = …)]</c> was replaced and <c>[StringLength(…, Message = …)]</c> kept -
/// same class, same culture. Generated sites now report authored text through
/// <c>ReportAuthored</c>, which the formatter returns before any table lookup.
/// </remarks>
public class AuthoredMessageTests {

    private static Dictionary<string, string> Render<T>(T value, string culture) where T : class {
        using var services = new ServiceCollection().AddMessagesDemoValidators().BuildServiceProvider();

        var validator = services.GetRequiredService<IValidatorFor<T>>();
        var formatter = services.GetRequiredService<ValidationMessageFormatter>();
        var result = validator.Validate(value);

        Assert.False(result.IsValid);

        var previous = CultureInfo.CurrentUICulture;

        try {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

            return result.Errors.ToDictionary(error => error.Field, error => error.ToMessage(formatter));
        }
        finally {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void CustomMessages_SurviveTheGermanPack_WhetherOrNotItCarriesABareKeyForTheCode() {
        var messages = Render(new Signup { Handle = null, Notes = "ab" }, "de-DE");

        // The de pack carries a bare `required` key; the override still wins.
        Assert.Equal("pick a handle", messages["handle"]);

        // It carries only string_length.* shape keys; the override wins here too, now by rule
        // rather than by accident.
        Assert.Equal("keep notes between 3 and 120 characters", messages["notes"]);
    }

    [Fact]
    public void WithoutACustomMessage_TheSameCodesStillTranslate() {
        // The counterpart claim: dropping the Message hands the text back to the packs.
        var messages = Render(new Reservation { Name = null, PartySize = 4 }, "de-DE");

        Assert.Equal("name ist erforderlich.", messages["name"]);
    }
}
