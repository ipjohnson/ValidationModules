using DependencyModules.Runtime;
using Microsoft.Extensions.DependencyInjection;
using SutProject.Hm;
using ValidationModules;
using Xunit;

namespace SutProject.HardenedModule.Tests;

/// <summary>
/// The Hardened shape: an entry point marked <c>[HardenedModule]</c> and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this assembly registers a validator. No <c>AddSutProjectHardenedModuleValidators()</c>
/// call, no module composed, no attribute pointing at anything - the entry point is composed the
/// way a Hardened host composes it and the validators are simply there.
/// </para>
/// <para>
/// That is the whole of what the fix changed. Before it, the generator emitted a sibling
/// <c>ValidationModule</c> class beside the entry point, nothing could compose it, and every
/// assertion below resolved nothing.
/// </para>
/// </remarks>
public class HardenedEntryPointTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddModule<Application>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheEntryPointRegistersTheGeneratedValidator()
    {
        using var provider = BuildProvider();

        Assert.IsType<ShipmentValidator>(
            provider.GetRequiredService<IValidatorFor<Shipment>>(),
            exactMatch: false
        );
    }

    [Fact]
    public void TheRegisteredValidatorReportsTheConstraintsThatFailed()
    {
        using var provider = BuildProvider();
        var validator = provider.GetRequiredService<IValidatorFor<Shipment>>();

        var result = validator.Validate(new Shipment { Tracking = "ab", Weight = 900 });

        Assert.False(result.IsValid);
        Assert.Equal(
            [("tracking", ValidationCodes.StringLength), ("weight", ValidationCodes.Range)],
            result.Errors.Select(error => (error.Field, error.Code))
        );
    }

    [Fact]
    public void TheRunnerIsRegisteredToo()
    {
        // The extension registers more than the validators - a runner and the collection
        // validators for each type - and the entry-point partial is a call into that one body, so
        // the branch cannot register a subset of it.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var result = scope
            .ServiceProvider.GetRequiredService<ValidationRunner<Shipment>>()
            .Validate(new Shipment { Tracking = "abc", Weight = 5 });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void NoSiblingValidationModuleIsEmitted()
    {
        // With an entry point to register into, the sibling module is not emitted: both would
        // register the same validators and the extension is deliberately not idempotent, so
        // composing the two would report every error twice.
        Assert.Null(
            typeof(Application).Assembly.GetType("SutProject.HardenedModule.ValidationModule")
        );
    }
}
