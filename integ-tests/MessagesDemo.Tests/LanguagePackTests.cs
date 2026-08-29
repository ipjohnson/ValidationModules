using System.Globalization;
using MessagesDemo;
using Microsoft.Extensions.DependencyInjection;
using ValidationModules;
using Xunit;

namespace MessagesDemo.Tests;

/// <summary>
/// The language-pack pipeline end to end: five JSON files compiled by the generator, registered
/// by <c>AddMessagesDemoValidators()</c>, selected by ambient culture through the formatter the
/// same registration TryAdds - and the app-local override file winning per key because it landed
/// later in the additional-files order.
/// </summary>
public class LanguagePackTests {

    private static readonly Reservation Invalid = new() {
        Name = null,
        PartySize = 12,
        Code = "nope",
        Guests = [],
        Start = new DateOnly(2026, 9, 10),
        End = new DateOnly(2026, 9, 1),
    };

    private static ServiceProvider Services() =>
        new ServiceCollection().AddMessagesDemoValidators().BuildServiceProvider();

    private static Dictionary<string, string> MessagesUnder(string culture) {
        using var services = Services();

        var validator = services.GetRequiredService<IValidatorFor<Reservation>>();
        var formatter = services.GetRequiredService<ValidationMessageFormatter>();
        var result = validator.Validate(Invalid);

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
    public void French_TranslatesEveryShape_AndTheOverrideWinsItsOneKey() {
        var messages = MessagesUnder("fr");

        // "required" comes from the override file, which registered later and wins per key…
        Assert.Equal("Merci de renseigner name.", messages["name"]);
        // …while every other key inherits the full pack beneath it.
        Assert.Equal("partySize doit être compris entre 1 et 8.", messages["partySize"]);
        Assert.Equal("code n'a pas le format requis.", messages["code"]);
        Assert.Equal("guests doit compter entre 1 et 4 éléments.", messages["guests"]);
        Assert.Equal("la date de fin doit suivre la date de début.", messages["end"]);
    }

    [Fact]
    public void RegionalCulture_FallsBackToItsParentPack() {
        var messages = MessagesUnder("fr-CA");

        Assert.Equal("partySize doit être compris entre 1 et 8.", messages["partySize"]);
    }

    [Fact]
    public void UnpackedCulture_KeepsTheDefaultRender() {
        var messages = MessagesUnder("it");

        Assert.Equal("name is required.", messages["name"]);
        Assert.Equal("partySize must be between 1 and 8.", messages["partySize"]);
    }

    [Theory]
    [InlineData("es", "partySize", "partySize debe estar entre 1 y 8.")]
    [InlineData("de", "partySize", "partySize muss zwischen 1 und 8 liegen.")]
    [InlineData("zh", "partySize", "partySize必须介于1和8之间。")]
    [InlineData("ja", "partySize", "partySizeは1から8の範囲で入力してください。")]
    [InlineData("zh-CN", "code", "code的格式不正确。")]
    [InlineData("ja", "end", "終了日は開始日より後にしてください。")]
    public void EveryShippedLanguage_RendersFromItsPack(string culture, string field, string expected) {
        Assert.Equal(expected, MessagesUnder(culture)[field]);
    }

    [Fact]
    public void TheFormatter_IsTryAdded_SoAnAppsOwnStillWins() {
        var services = new ServiceCollection();

        services.AddSingleton<ValidationMessageFormatter>(new ValidationMessageMap()
            .Map(ValidationCodes.Required, static (in ValidationError e) => $"{e.Field}?!"));
        services.AddMessagesDemoValidators();

        using var provider = services.BuildServiceProvider();
        var error = new ValidationError("name", ValidationCodes.Required, null, ValidationMessageInfo.Required);

        Assert.Equal("name?!", error.ToMessage(provider.GetRequiredService<ValidationMessageFormatter>()));
    }

    [Fact]
    public void ValidationCodes_AreUntouchedByAnyOfThis() {
        using var services = Services();

        var result = services.GetRequiredService<IValidatorFor<Reservation>>().Validate(Invalid);

        Assert.Equal(
            ["required", "range", "pattern", "array_bounds", "date_order"],
            result.Errors.Select(error => error.Code).ToArray());
    }
}
