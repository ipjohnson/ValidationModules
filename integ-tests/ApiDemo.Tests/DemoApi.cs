using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace ApiDemo.Tests;

/// <summary>
/// Drives the real ApiDemo pipeline, per environment.
/// </summary>
/// <remarks>
/// The environment is a parameter rather than a default because it changes behaviour that matters:
/// <c>RouteHandlerOptions.ThrowOnBadRequest</c> defaults to <c>IsDevelopment()</c>, so an unparseable
/// body throws in Development and is a silent 400 outside it. Both paths have to end somewhere a
/// client can read, and only running both proves it.
/// </remarks>
internal sealed class DemoApi(string environment) : WebApplicationFactory<Program> {
    public static DemoApi Development() => new("Development");

    public static DemoApi Production() => new("Production");

    protected override IHost CreateHost(IHostBuilder builder) {
        builder.UseEnvironment(environment);
        return base.CreateHost(builder);
    }
}
