using Microsoft.Extensions.DependencyInjection;
using SutProject.Polymorphic;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// <c>Polymorphism.Runtime</c>: a descent that resolves a validator for the value's runtime type
/// from the container.
/// </summary>
/// <remarks>
/// The whole path is <c>GetType()</c> and a dictionary lookup - no <c>MakeGenericType</c>, no
/// <c>Activator</c>, no scanning - because the assembly declaring each type registers an adapter it
/// knows statically.
/// </remarks>
public class RuntimePolymorphismTests {

    private static ServiceProvider Container() {
        var services = new ServiceCollection();
        services.AddSutProjectValidators();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void SubtypeRules_RunWhenResolvedThroughTheContainer() {
        using var provider = Container();
        using var scope = provider.CreateScope();

        var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<DynamicCheckout>>();

        var result = runner.Validate(new DynamicCheckout {
            Payment = new Card { Currency = "GBP", Pan = "123" },
        });

        var error = Assert.Single(result.Errors);

        Assert.Equal("payment.pan", error.Field);
        Assert.Equal(ValidationCodes.StringLength, error.Code);
    }

    /// <summary>
    /// Two levels down, reached by runtime type rather than by a switch arm.
    /// </summary>
    [Fact]
    public void TheMostDerivedValidatorIsTheOneResolved() {
        using var provider = Container();
        using var scope = provider.CreateScope();

        var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<DynamicCheckout>>();

        var result = runner.Validate(new DynamicCheckout {
            Payment = new Premium { Currency = "GBP", Pan = "1234567890123456", Concierge = null },
        });

        Assert.Equal("payment.concierge", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void AValidValueReportsNothing() {
        using var provider = Container();
        using var scope = provider.CreateScope();

        var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<DynamicCheckout>>();

        Assert.True(runner.Validate(new DynamicCheckout {
            Payment = new Bank { Currency = "GBP", Iban = "GB00" },
        }).IsValid);
    }

    /// <summary>
    /// No fallback, deliberately - not to the compile-time switch, not to the declared type. A
    /// validator that behaved one way with a container and another way without one would be exactly
    /// the sort of context-dependent silent change this design exists to avoid.
    /// </summary>
    [Fact]
    public void WithoutAProvider_ItThrowsRatherThanFallingBack() {
        var validator = new DynamicCheckoutValidator();
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);
        var checkout = new DynamicCheckout { Payment = new Card { Currency = "GBP", Pan = "123" } };

        var thrown = Assert.Throws<InvalidOperationException>(
            () => validator.Validate(ref context, checkout));

        Assert.Contains("payment", thrown.Message);
        Assert.Contains("Polymorphism.CompileTime", thrown.Message);
    }

    /// <summary>
    /// The registry is what the descent looks the runtime type up in, and every validated type in a
    /// dispatching assembly has an adapter in it - which is what makes a miss mean "that assembly
    /// never registered" rather than "that type had no rules".
    /// </summary>
    [Fact]
    public void EveryValidatedTypeHasAnAdapter() {
        using var provider = Container();

        var registry = provider.GetRequiredService<DynamicValidatorRegistry>();

        Assert.NotNull(registry.Find(typeof(Card)));
        Assert.NotNull(registry.Find(typeof(Premium)));
        Assert.NotNull(registry.Find(typeof(Bank)));
        Assert.Null(registry.Find(typeof(string)));
    }

    /// <summary>
    /// The difference from CompileTime: dispatch goes through the container, so a second registered
    /// validator for the runtime type composes with the generated one.
    /// </summary>
    [Fact]
    public void ASeparatelyRegisteredValidatorForTheRuntimeType_Composes() {
        var services = new ServiceCollection();
        services.AddSutProjectValidators();
        services.AddSingleton<IValidatorFor<Card>, ExtraCardRule>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<DynamicCheckout>>();

        var result = runner.Validate(new DynamicCheckout {
            Payment = new Card { Currency = "GBP", Pan = "1234567890123456" },
        });

        Assert.Equal("payment.pan", Assert.Single(result.Errors).Field);
    }

    private sealed class ExtraCardRule : IValidatorFor<Card> {
        public ValidationFlow Validate(ref ValidationContext context, Card value) =>
            value.Pan?.StartsWith('9') == false
                ? context.Report("pan", "issuer", "cards must be issued in the 9 range.")
                : ValidationFlow.Continue;
    }
}
