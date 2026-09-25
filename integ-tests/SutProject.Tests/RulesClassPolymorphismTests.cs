using Microsoft.Extensions.DependencyInjection;
using SutProject.Polymorphic;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// <c>rules.Nested</c> and <c>rules.Each</c> passing a <c>Polymorphism</c>, against
/// really-compiled generated code. <see cref="PolymorphicDescentTests"/> and
/// <see cref="RuntimePolymorphismTests"/> cover the same modes on <c>[ValidateNested]</c>.
/// </summary>
public class RulesClassPolymorphismTests
{
    private static readonly HouseholdValidator Dispatching = new();

    // -- CompileTime ----------------------------------------------------------------------------

    [Fact]
    public void ADogReachingAnAnimalMember_RunsTheDogRules()
    {
        var household = new Household
        {
            Pet = new Dog { Name = "Rex", Breed = null },
        };

        var error = Assert.Single(Dispatching.Validate(household).Errors);

        Assert.Equal("pet.breed", error.Field);
        Assert.Equal(ValidationCodes.Required, error.Code);
    }

    [Fact]
    public void AnAnimalThatIsNotADog_RunsTheAnimalRules()
    {
        var household = new Household { Pet = new Animal { Name = null } };

        Assert.Equal("pet.name", Assert.Single(Dispatching.Validate(household).Errors).Field);
    }

    /// <summary>
    /// The Dog validator checks what a Dog inherits, so the Animal rules do not also run on it.
    /// </summary>
    [Fact]
    public void TheAnimalRulesReachADogOnce()
    {
        var household = new Household
        {
            Pet = new Dog { Name = null, Breed = "Collie" },
        };

        Assert.Equal("pet.name", Assert.Single(Dispatching.Validate(household).Errors).Field);
    }

    [Fact]
    public void EachElementOfAMixedList_RunsTheRulesForItsOwnType()
    {
        var household = new Household
        {
            Pets =
            {
                new Dog { Name = "Rex", Breed = null },
                new Animal { Name = null },
                new Dog { Name = "Fido", Breed = "Collie" },
            },
        };

        Assert.Equal(
            ["pets[0].breed", "pets[1].name"],
            Dispatching.Validate(household).Errors.Select(error => error.Field)
        );
    }

    [Fact]
    public void AValidHouseholdReportsNothing()
    {
        var household = new Household
        {
            Pet = new Dog { Name = "Rex", Breed = "Collie" },
            Pets =
            {
                new Animal { Name = "Tom" },
                new Dog { Name = "Fido", Breed = "Pug" },
            },
        };

        Assert.True(Dispatching.Validate(household).IsValid);
        Assert.True(Dispatching.IsValid(household));
    }

    // -- Runtime --------------------------------------------------------------------------------

    private static ServiceProvider Container(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();

        services.AddSutProjectValidators();
        extra?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private static ValidationResult ValidateThroughTheContainer(
        DynamicHousehold household,
        Action<IServiceCollection>? extra = null
    )
    {
        using var provider = Container(extra);
        using var scope = provider.CreateScope();

        return scope
            .ServiceProvider.GetRequiredService<ValidationRunner<DynamicHousehold>>()
            .Validate(household);
    }

    [Fact]
    public void Runtime_ADogReachingAnAnimalMember_RunsTheDogRules()
    {
        var result = ValidateThroughTheContainer(
            new DynamicHousehold
            {
                Pet = new Dog { Name = "Rex", Breed = null },
            }
        );

        Assert.Equal("pet.breed", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Runtime_EachElementOfAMixedList_RunsTheRulesForItsOwnType()
    {
        var result = ValidateThroughTheContainer(
            new DynamicHousehold
            {
                Pets =
                {
                    new Dog { Name = "Rex", Breed = null },
                    new Animal { Name = null },
                },
            }
        );

        Assert.Equal(["pets[0].breed", "pets[1].name"], result.Errors.Select(error => error.Field));
    }

    /// <summary>
    /// No fallback without a container, as on the attribute, and the message names the descent.
    /// </summary>
    [Fact]
    public void Runtime_WithoutAProvider_ItThrowsRatherThanFallingBack()
    {
        var validator = new DynamicHouseholdValidator();
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);
        var household = new DynamicHousehold
        {
            Pet = new Dog { Name = "Rex", Breed = null },
        };

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            validator.Validate(ref context, household)
        );

        Assert.Contains(
            "'pet' on DynamicHousehold descends with Polymorphism.Runtime",
            thrown.Message
        );
        Assert.Contains("Polymorphism.CompileTime", thrown.Message);
    }

    /// <summary>
    /// The difference from CompileTime: a validator registered separately for the runtime type
    /// runs alongside the generated one.
    /// </summary>
    [Fact]
    public void Runtime_ASeparatelyRegisteredValidatorForTheRuntimeType_Composes()
    {
        var result = ValidateThroughTheContainer(
            new DynamicHousehold
            {
                Pet = new Dog { Name = "Rex", Breed = "Collie" },
            },
            services => services.AddSingleton<IValidatorFor<Dog>, KnownBreeds>()
        );

        Assert.Equal("pet.breed", Assert.Single(result.Errors).Field);
    }

    private sealed class KnownBreeds : IValidatorFor<Dog>
    {
        public ValidationFlow Validate(ref ValidationContext context, Dog value) =>
            value.Breed is "Pug"
                ? ValidationFlow.Continue
                : context.Report("breed", "breed", "only pugs are registered.");
    }
}
