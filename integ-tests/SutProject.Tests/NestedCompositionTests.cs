using Microsoft.Extensions.DependencyInjection;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Validators registered for a <i>nested</i> type compose with the generated one, the same way
/// validators for the top-level type always have.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this fixes.</b> A generated validator descends by calling the nested type's generated
/// validator through its static <c>Instance</c> field, so a hand-written
/// <c>IValidatorFor&lt;Address&gt;</c> in the container used to be invisible from that path.
/// Validating an <c>Address</c> directly ran it; validating the same <c>Address</c> as a property of
/// a <c>Pet</c> did not, and nothing said so. A blocked postal code passed or failed depending on
/// which entry point the caller happened to use.
/// </para>
/// <para>
/// <b>Why it is AOT-safe.</b> The emitted lookup is
/// <c>GetServices&lt;IValidatorFor&lt;Address&gt;&gt;()</c> - a closed generic, written by the
/// generator, which knows the nested type at build time. The reflective spelling would be
/// <c>MakeGenericType</c>, which is the thing this library exists to avoid.
/// </para>
/// </remarks>
public class NestedCompositionTests {

    /// <remarks>
    /// Note the field name. Address.PostalCode carries [JsonPropertyName("postal_code")], so the
    /// generated validator reports "postal_code" - and a hand-written validator that composes with
    /// it has to say the same thing, or one field arrives under two names depending on which rule
    /// failed. The runtime cannot check this: reading the attribute at run time is reflection.
    /// </remarks>
    private sealed class AddressBlocklistValidator : IValidatorFor<Address> {
        public void Validate(ref ValidationContext context, Address value) {
            if (value.PostalCode == "BLOCKED") {
                context.Add("postal_code", "blocked", "postal code is blocked.");
            }
        }
    }

    private sealed class ToyRecallValidator : IValidatorFor<Toy> {
        public void Validate(ref ValidationContext context, Toy value) {
            if (value.Name == "recalled") {
                context.Add("name", "recalled", "toy is recalled.");
            }
        }
    }

    private static ServiceProvider Provider() {
        var services = new ServiceCollection();

        services.AddValidationModules(GeneratedValidators.All);
        services.AddSingleton<IValidatorFor<Address>, AddressBlocklistValidator>();
        services.AddSingleton<IValidatorFor<Toy>, ToyRecallValidator>();
        services.AddValidationRunner<Pet>();

        return services.BuildServiceProvider();
    }

    private static ValidationRunner<Pet> Runner(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<ValidationRunner<Pet>>();

    private static Pet Valid() => new() {
        Name = "Rex", Sku = "ABC", Slug = "rex", Age = 3, Status = "available",
        Home = new Address { PostalCode = "SW1" },
        Toys = [new Toy { Name = "ball" }],
    };

    [Fact]
    public void RegisteredValidatorForANestedObject_Runs() {
        using var provider = Provider();

        var result = Runner(provider).Validate(Valid() with { Home = new Address { PostalCode = "BLOCKED" } });

        var error = Assert.Single(result.Errors);
        Assert.Equal("home.postal_code", error.Field);
        Assert.Equal("blocked", error.Code);
    }

    [Fact]
    public void RegisteredValidatorForACollectionElement_RunsAndIsIndexed() {
        using var provider = Provider();

        var result = Runner(provider).Validate(Valid() with {
            Toys = [new Toy { Name = "ball" }, new Toy { Name = "recalled" }],
        });

        var error = Assert.Single(result.Errors);
        Assert.Equal("toys[1].name", error.Field);
        Assert.Equal("recalled", error.Code);
    }

    [Fact]
    public void GeneratedNestedValidatorDoesNotRunTwice() {
        // The generated validator is registered in the container as well as being reachable
        // statically, so without excluding it by reference every nested error would be duplicated.
        using var provider = Provider();

        var result = Runner(provider).Validate(Valid() with { Home = new Address { PostalCode = null } });

        var error = Assert.Single(result.Errors);
        Assert.Equal("home.postal_code", error.Field);
        Assert.Equal(ValidationCodes.Required, error.Code);
    }

    [Fact]
    public void GeneratedAndRegisteredNestedErrors_BothSurvive() {
        using var provider = Provider();

        var result = Runner(provider).Validate(Valid() with {
            Home = new Address { PostalCode = "BLOCKED" },
            Toys = [new Toy { Name = null }, new Toy { Name = "recalled" }],
        });

        Assert.Equal(
            new[] { "home.postal_code:blocked", "toys[0].name:required", "toys[1].name:recalled" },
            result.Errors.Select(e => $"{e.Field}:{e.Code}").OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void CleanValue_StaysCleanWithCompositionOn() {
        using var provider = Provider();

        Assert.True(Runner(provider).Validate(Valid()).IsValid);
    }

    [Fact]
    public void InstanceCall_IsUnaffectedBecauseItHasNoProvider() {
        // The no-container path must keep working exactly as before: Services is null, the resolve
        // short-circuits, and nothing is allocated for it.
        var pet = Valid() with { Home = new Address { PostalCode = "BLOCKED" } };

        Assert.True(PetValidator.Instance.IsValid(pet));
        Assert.Empty(PetValidator.Instance.Validate(pet).Errors);
    }

    [Fact]
    public void RunnerConstructedByHandWithoutAProvider_RunsGeneratedValidatorsOnly() {
        // What a unit test does. Composition is a property of having been resolved from a scope.
        var runner = new ValidationRunner<Pet>([PetValidator.Instance], []);

        Assert.True(runner.Validate(Valid() with { Home = new Address { PostalCode = "BLOCKED" } }).IsValid);
    }

    [Fact]
    public void ContextStartedWithAProvider_CarriesItThroughEveryDescent() {
        // The mechanism, asserted directly rather than through its effect: Push must not drop it,
        // or composition would work at depth 1 and stop.
        using var provider = Provider();
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector, provider);

        Assert.Same(provider, context.Services);
        Assert.Same(provider, context.Push("home").Services);
        Assert.Same(provider, context.Push("home").PushIndex("toys", 2).Services);
        Assert.Same(provider, context.PushKey("map", "k").Push("a").Push("b").Services);
    }

    [Fact]
    public void ContextStartedWithoutAProvider_HasNoServices() {
        Assert.Null(new ValidationContext(new ValidationErrorCollector()).Services);
        Assert.Null(new ValidationContext(new ValidationErrorCollector()).Push("home").Services);
    }
}
