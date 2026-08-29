using DependencyModules.Runtime;
using Microsoft.Extensions.DependencyInjection;
using SutProject.Dm;
using ValidationModules;
using Xunit;

namespace SutProject.DependencyModules.Tests;

// The SutProject reference (the cross-assembly facet) declares its own Account in namespace
// SutProject, which the enclosing-namespace walk reaches before the compilation unit's usings.
// Inside the namespace, the alias wins, keeping this file on the type it has always meant.
using Account = SutProject.Dm.Account;
using AccountValidator = SutProject.Dm.AccountValidator;

/// <summary>
/// The DependencyModules branch: the generator saw IDependencyModule in the compilation and emitted
/// a module rather than a static table, and hand-written validators registered through DM's own
/// attributes compose with the generated one.
/// </summary>
/// <remarks>
/// This is also the project that proves the two generators coexist. Ours deliberately does not host
/// DependencyModules' attribute stages - if it derived from BaseSourceGenerator, [DependencyModule]
/// would be processed twice here and the module would be emitted twice (plan §7.2).
/// </remarks>
public class CustomValidatorCompositionTests {

    [Fact]
    public void GeneratedModule_RegistersTheGeneratedValidator() {
        using var provider = BuildProvider();

        Assert.Contains(
            provider.GetServices<IValidatorFor<Account>>(),
            validator => validator is AccountValidator);
    }

    [Fact]
    public void CustomStructuralValidator_IsRegisteredAlongsideIt() {
        using var provider = BuildProvider();

        var validators = provider.GetServices<IValidatorFor<Account>>().ToArray();

        Assert.Contains(validators, v => v is AccountValidator);
        Assert.Contains(validators, v => v is AccountReservedHandleValidator);
    }

    [Fact]
    public void Runner_MergesGeneratedAndHandWrittenStructuralErrors() {
        // "admin" satisfies every generated constraint and fails the hand-written one. Both
        // validators run and neither replaces the other.
        using var provider = BuildProvider();
        var runner = provider.CreateScope().ServiceProvider.GetRequiredService<ValidationRunner<Account>>();

        var result = runner.Validate(new Account { Handle = "admin", Age = 30 });

        Assert.Equal("reserved", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Runner_KeepsStructuralErrorsWhenAHandWrittenValidatorAlsoFails() {
        using var provider = BuildProvider();
        var runner = provider.CreateScope().ServiceProvider.GetRequiredService<ValidationRunner<Account>>();

        var result = runner.Validate(new Account { Handle = "ad", Age = 200 });

        // Generated string_length and range both survive; a hand-written validator cannot make a
        // structural constraint disappear.
        Assert.Equal(
            new[] { "string_length", "range" },
            result.Errors.Select(error => error.Code));
    }

    [Fact]
    public async Task Runner_RunsTheAsyncValidatorWithItsInjectedDependency() {
        using var provider = BuildProvider();
        var runner = provider.CreateScope().ServiceProvider.GetRequiredService<ValidationRunner<Account>>();

        var result = await runner.ValidateAsync(
            new Account { Handle = "taken", Age = 30 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("duplicate", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Runner_SkipsTheAsyncValidatorWhenStructuralValidationFailed() {
        // The gate: a uniqueness lookup must not reach its dependency for a handle that is missing.
        using var provider = BuildProvider();
        var runner = provider.CreateScope().ServiceProvider.GetRequiredService<ValidationRunner<Account>>();

        var result = await runner.ValidateAsync(
            new Account { Handle = null, Age = 30 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("required", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Runner_CleanValue_PassesBothSides() {
        using var provider = BuildProvider();
        var runner = provider.CreateScope().ServiceProvider.GetRequiredService<ValidationRunner<Account>>();

        var result = await runner.ValidateAsync(
            new Account { Handle = "ada", Age = 36 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    private static ServiceProvider BuildProvider() {
        var services = new ServiceCollection();

        // The application's own module brings the hand-written validators in through DM's
        // attributes; the generated module brings the generated ones. Composing them is the
        // consumer's call, per plan §7.3.
        services.AddModule<ApplicationModule>();
        services.AddModule<global::SutProject.DependencyModules.ValidationModule>();

        return services.BuildServiceProvider();
    }
}
