using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OptionsDemo;
using Xunit;

namespace OptionsDemo.Tests;

/// <summary>
/// <c>AddValidatedOptions&lt;T&gt;()</c> end to end: the generated validator judges the bound
/// configuration, and a bad <c>appsettings.json</c> refuses the host at startup rather than
/// surfacing on first use.
/// </summary>
/// <remarks>
/// The lesson of the ASP.NET Core wave applied to the options wave: the root cause of that gap
/// was that no web app existed in the repository, so this host exists in it.
/// </remarks>
public class OptionsStartupTests {

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IHost Host(string settingsFile, bool registerValidators = true) {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings {
            // No environment probing, no ambient appsettings - the named file is the whole config.
            DisableDefaults = true,
        });

        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, settingsFile));

        if (registerValidators) {
            builder.Services.AddOptionsDemoValidators();
        }

        builder.Services.AddValidatedOptions<HubOptions>().BindConfiguration("Hub");

        return builder.Build();
    }

    [Fact]
    public async Task BadConfiguration_RefusesTheHost_NamingFieldCodeAndMessage() {
        using var host = Host("appsettings.bad.json");

        var error = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Ct));

        Assert.Contains("hubName [string_length]", error.Message);
        Assert.Contains("maxBatchSize [range]", error.Message);
    }

    [Fact]
    public async Task GoodConfiguration_StartsAndBinds() {
        using var host = Host("appsettings.good.json");

        await host.StartAsync(Ct);

        var options = host.Services.GetRequiredService<IOptions<HubOptions>>().Value;

        Assert.Equal("main-hub", options.HubName);
        Assert.Equal(100, options.MaxBatchSize);

        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task NoRegisteredValidator_FailsRatherThanValidatingNothing() {
        // Asking for validated options and consulting nothing would be a silent no-op - the
        // class of failure this whole release removes.
        using var host = Host("appsettings.good.json", registerValidators: false);

        var error = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Ct));

        Assert.Contains("No IValidatorFor<HubOptions>", error.Message);
    }
}
