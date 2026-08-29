using System.Globalization;
using MessagesDemo;
using Microsoft.Extensions.DependencyInjection;
using ValidationModules;
using Xunit;

namespace MessagesDemo.Filtered.Tests;

/// <summary>
/// The csproj opt-out, proven from inside the binary it produced: this assembly compiled the same
/// sources and had the same five pack files on disk as MessagesDemo, with
/// <c>&lt;ValidationModulesLanguages&gt;fr&lt;/ValidationModulesLanguages&gt;</c> set - so French
/// exists, and German is not disabled but absent.
/// </summary>
public class FilteredLanguageTests {

    private static ServiceProvider Services() =>
        new ServiceCollection().AddMessagesDemoFilteredTestsValidators().BuildServiceProvider();

    [Fact]
    public void OnlyTheOptedInLanguage_WasCompiledIn() {
        using var services = Services();

        var pack = Assert.Single(services.GetServices<IValidationLanguagePack>());

        Assert.Equal("fr", pack.Culture);
    }

    [Fact]
    public void TheOptedInLanguage_Renders_AndTheExcludedOneFallsBackToDefaults() {
        using var services = Services();

        var validator = services.GetRequiredService<IValidatorFor<Reservation>>();
        var formatter = services.GetRequiredService<ValidationMessageFormatter>();
        var error = validator.Validate(new Reservation { Name = "x", PartySize = 12, Guests = ["a"] })
            .Errors.Single(e => e.Field == "partySize");

        var previous = CultureInfo.CurrentUICulture;

        try {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
            Assert.Equal("partySize doit être compris entre 1 et 8.", error.ToMessage(formatter));

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
            Assert.Equal("partySize must be between 1 and 8.", error.ToMessage(formatter));
        }
        finally {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
