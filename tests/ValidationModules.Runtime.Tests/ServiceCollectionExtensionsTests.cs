using Microsoft.Extensions.DependencyInjection;
using ValidationModules.Naming;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The non-DependencyModules registration path from plan §7.3.
/// </summary>
/// <remarks>
/// Everything here is about what does <i>not</i> happen: no open generics, no
/// <c>ActivatorUtilities</c>, no constructor reflection, and one instance per process rather than
/// one per request. Each of those is a §2 non-negotiable that MS.DI would happily violate if the
/// registration were written the obvious way, and none of them is visible from reading a passing
/// functional test — so they are asserted directly.
/// </remarks>
public class ServiceCollectionExtensionsTests {

    private sealed record Widget {
        public string? Name { get; init; }
    }

    private sealed class SampleValidator : IValidatorFor<Widget> {
        public static readonly SampleValidator Instance = new();

        public void Validate(ref ValidationContext context, Widget value) {
            if (string.IsNullOrWhiteSpace(value.Name)) {
                context.AddRequired("name");
            }
        }
    }

    private static ValidatorRegistration[] Table() => [
        new ValidatorRegistration(typeof(IValidatorFor<Widget>), static _ => SampleValidator.Instance),
    ];

    [Fact]
    public void AddValidationModules_RegistersEachEntryUnderItsServiceType() {
        var provider = new ServiceCollection().AddValidationModules(Table()).BuildServiceProvider();

        Assert.Same(SampleValidator.Instance, provider.GetRequiredService<IValidatorFor<Widget>>());
    }

    [Fact]
    public void AddValidationModules_RegistersValidatorsAsSingletons() {
        // Rule graphs are built once, never per validation call. A scoped registration is how
        // FluentValidation ends up rebuilding its graph every request — measured at ~2,163 ns.
        var provider = new ServiceCollection().AddValidationModules(Table()).BuildServiceProvider();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IValidatorFor<Widget>>(),
            second.ServiceProvider.GetRequiredService<IValidatorFor<Widget>>());
    }

    [Fact]
    public void AddValidationModules_UsesTheFactoryRatherThanConstructingTheType() {
        // The factory is what keeps ActivatorUtilities out of the path. Observed by registering a
        // factory that could not be reproduced by construction.
        var sentinel = new SampleValidator();
        var provider = new ServiceCollection()
            .AddValidationModules([
                new ValidatorRegistration(typeof(IValidatorFor<Widget>), _ => sentinel),
            ])
            .BuildServiceProvider();

        Assert.Same(sentinel, provider.GetRequiredService<IValidatorFor<Widget>>());
    }

    [Fact]
    public void AddValidationModules_PassesTheProviderToTheFactory() {
        IServiceProvider? seen = null;

        var provider = new ServiceCollection()
            .AddValidationModules([
                new ValidatorRegistration(typeof(IValidatorFor<Widget>), resolved => {
                    seen = resolved;
                    return SampleValidator.Instance;
                }),
            ])
            .BuildServiceProvider();

        _ = provider.GetRequiredService<IValidatorFor<Widget>>();

        Assert.NotNull(seen);
    }

    [Fact]
    public void AddValidationModules_RegistersTheDefaultFieldNamer() {
        var provider = new ServiceCollection().AddValidationModules(Table()).BuildServiceProvider();

        Assert.IsType<CamelCaseFieldNamer>(provider.GetRequiredService<IValidationFieldNamer>());
    }

    [Fact]
    public void AddValidationModules_KeepsAFieldNamerTheConsumerRegisteredFirst() {
        // TryAdd, so a consumer's own naming policy survives. Registering ours over theirs would
        // put the generated literals and the adapter's renaming out of step, which is the one thing
        // a single namer exists to prevent.
        var mine = PascalCaseFieldNamer.Instance;

        var provider = new ServiceCollection()
            .AddSingleton<IValidationFieldNamer>(mine)
            .AddValidationModules(Table())
            .BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IValidationFieldNamer>());
    }

    [Fact]
    public void AddValidationModules_WithAnEmptyTable_IsANoOpThatStillRegistersTheNamer() {
        var provider = new ServiceCollection().AddValidationModules([]).BuildServiceProvider();

        Assert.NotNull(provider.GetService<IValidationFieldNamer>());
        Assert.Null(provider.GetService<IValidatorFor<Widget>>());
    }

    [Fact]
    public void AddValidationModules_RegistersEveryEntryWhenTwoShareAServiceType() {
        // ValidationRunner<T> merges every registered IValidatorFor<T>, so Add rather than TryAdd
        // is deliberate: a structural validator and a hand-written one must both run.
        var provider = new ServiceCollection()
            .AddValidationModules([
                new ValidatorRegistration(typeof(IValidatorFor<Widget>), static _ => SampleValidator.Instance),
                new ValidatorRegistration(typeof(IValidatorFor<Widget>), static _ => new SampleValidator()),
            ])
            .BuildServiceProvider();

        Assert.Equal(2, provider.GetServices<IValidatorFor<Widget>>().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddValidationModules_NullArgument_Throws(bool nullServices) {
        if (nullServices) {
            Assert.Throws<ArgumentNullException>(() =>
                ValidationModulesServiceCollectionExtensions.AddValidationModules(null!, Table()));
        } else {
            Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddValidationModules(null!));
        }
    }

    [Fact]
    public void AddValidationRunner_RegistersTheRunnerScoped() {
        var provider = new ServiceCollection()
            .AddValidationModules(Table())
            .AddValidationRunner<Widget>()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ValidationRunner<Widget>>());
    }

    [Fact]
    public void AddValidationRunner_IsClosedSoNothingResolvesThroughAnOpenGeneric() {
        // AddScoped(typeof(ValidationRunner<>)) would have MS.DI construct it reflectively, which
        // is exactly what a Native AOT publish cannot do. The generator emits one call per type.
        var provider = new ServiceCollection()
            .AddValidationModules(Table())
            .AddValidationRunner<Widget>()
            .BuildServiceProvider();

        Assert.Null(provider.GetService<ValidationRunner<string>>());
    }

    [Fact]
    public void AddValidationRunner_CalledTwice_RegistersOne() {
        var services = new ServiceCollection()
            .AddValidationModules(Table())
            .AddValidationRunner<Widget>()
            .AddValidationRunner<Widget>();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ValidationRunner<Widget>));
    }

    [Fact]
    public void AddValidationRunner_ResolvesARunnerThatActuallyValidates() {
        var provider = new ServiceCollection()
            .AddValidationModules(Table())
            .AddValidationRunner<Widget>()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<Widget>>();

        Assert.False(runner.Validate(new Widget { Name = null }).IsValid);
        Assert.True(runner.Validate(new Widget { Name = "Rex" }).IsValid);
    }

    [Fact]
    public void AddDescribedValidator_RegistersARunnableValidatorWithoutTheGenerator() {
        // The generator-less path: a rules class hand-written or emitted by somebody else's
        // generator, run by DescribedValidator<T> with none of this package's build-time machinery.
        var provider = new ServiceCollection()
            .AddDescribedValidator<Widget, SampleRules>()
            .BuildServiceProvider();

        var validator = provider.GetRequiredService<IValidatorFor<Widget>>();

        Assert.IsType<DescribedValidator<Widget>>(validator);
        Assert.False(validator.IsValid(new Widget { Name = null }));
        Assert.True(validator.IsValid(new Widget { Name = "Rex" }));
    }

    [Fact]
    public void AddDescribedValidator_IsSingletonSoDescribeRunsOncePerProcess() {
        var provider = new ServiceCollection()
            .AddDescribedValidator<Widget, SampleRules>()
            .BuildServiceProvider();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IValidatorFor<Widget>>(),
            second.ServiceProvider.GetRequiredService<IValidatorFor<Widget>>());
    }

    [Fact]
    public void AddDescribedValidator_ResolvesWithoutAnIValidatorProviderRegistered() {
        // Resolved optionally, because only a declaration descending into a nested type needs one.
        var provider = new ServiceCollection()
            .AddDescribedValidator<Widget, SampleRules>()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IValidatorFor<Widget>>());
    }

    [Fact]
    public void AddDescribedValidator_AlsoRegistersTheDefaultFieldNamer() {
        var provider = new ServiceCollection()
            .AddDescribedValidator<Widget, SampleRules>()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<IValidationFieldNamer>());
    }

    private sealed class SampleRules : IValidationRulesFor<Widget> {
        public void Describe(ValidationRules<Widget> rules) {
            rules.Required(x => x.Name);
        }
    }
}
