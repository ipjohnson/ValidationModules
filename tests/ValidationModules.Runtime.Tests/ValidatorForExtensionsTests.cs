using Microsoft.Extensions.DependencyInjection;
using ValidationModules.Naming;
using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

public class ValidatorForExtensionsTests
{
    [Fact]
    public void IsValid_CleanValue_IsTrue()
    {
        Assert.True(PetValidator.Instance.IsValid(ValidPet()));
    }

    [Fact]
    public void IsValid_FailingValue_IsFalse()
    {
        Assert.False(PetValidator.Instance.IsValid(new Pet()));
    }

    /// <summary>
    /// A null value is rejected where it enters, as <c>Validator.TryValidateObject</c> rejects a
    /// null instance, rather than reaching the validator and failing wherever it first reads a
    /// member. <c>PetValidator</c> leaves <c>IsValid</c> to the interface's default, so the last
    /// call covers that path.
    /// </summary>
    [Fact]
    public void EveryEntryPoint_RejectsANullValue()
    {
        IValidatorFor<Pet> validator = PetValidator.Instance;

        AssertRejectsNull(() => validator.Validate(null!));
        AssertRejectsNull(() => validator.Validate(null!, ValidationPathMode.Full));
        AssertRejectsNull(() => validator.ValidateFirst(null!));
        AssertRejectsNull(() => validator.ValidateAndThrow(null!));
        AssertRejectsNull(() => validator.ValidateInto(new ValidationErrorCollector(), null!));
        AssertRejectsNull(() => ValidatorForExtensions.IsValid(validator, null!));
        AssertRejectsNull(() => validator.IsValid(null!));
    }

    private static void AssertRejectsNull(Action call) =>
        Assert.Equal("value", Assert.Throws<ArgumentNullException>(call).ParamName);

    [Fact]
    public void ValidateAndThrow_CleanValue_DoesNotThrow()
    {
        PetValidator.Instance.ValidateAndThrow(ValidPet());
    }

    [Fact]
    public void ValidateAndThrow_FailingValue_ThrowsCarryingTheResult()
    {
        var exception = Assert.Throws<ValidationException>(() =>
            PetValidator.Instance.ValidateAndThrow(new Pet())
        );

        Assert.Contains(exception.Result.Errors, error => error.Field == "name");
    }

    [Fact]
    public void ValidateInto_UsesTheCallerSCollector()
    {
        var collector = new ValidationErrorCollector();

        PetValidator.Instance.ValidateInto(
            collector,
            new Pet { Toys = [new Toy { Name = "ball" }] }
        );

        Assert.Equal("name", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void ValidateInto_DoesNotResetTheCollector()
    {
        // The caller owns the lifecycle; a filter validating a body and then a query string wants
        // both sets of errors in one result.
        var collector = new ValidationErrorCollector();
        collector.Add(new ValidationError("query.page", "range", "x"));

        PetValidator.Instance.ValidateInto(
            collector,
            new Pet { Toys = [new Toy { Name = "ball" }] }
        );

        Assert.Equal(2, collector.ToResult().Errors.Count);
    }

    [Fact]
    public void Validate_NullValidator_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ((IValidatorFor<Pet>)null!).Validate(new Pet()));
    }

    [Fact]
    public void AddValidationModules_RegistersEachValidatorAndTheDefaultNamer()
    {
        var services = new ServiceCollection();

        services.AddValidationModules([
            new ValidatorRegistration(
                typeof(IValidatorFor<Pet>),
                static _ => PetValidator.Instance
            ),
            new ValidatorRegistration(
                typeof(IValidatorFor<Address>),
                static _ => AddressValidator.Instance
            ),
        ]);

        var provider = services.BuildServiceProvider();

        Assert.Same(PetValidator.Instance, provider.GetRequiredService<IValidatorFor<Pet>>());
        Assert.Same(
            AddressValidator.Instance,
            provider.GetRequiredService<IValidatorFor<Address>>()
        );
        Assert.IsType<CamelCaseFieldNamer>(provider.GetRequiredService<IValidationFieldNamer>());
    }

    [Fact]
    public void AddValidationModules_KeepsANamerTheConsumerAlreadyRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IValidationFieldNamer>(SnakeCaseFieldNamer.Instance);

        services.AddValidationModules([]);

        Assert.IsType<SnakeCaseFieldNamer>(
            services.BuildServiceProvider().GetRequiredService<IValidationFieldNamer>()
        );
    }

    [Fact]
    public void AddValidationRunner_ResolvesWithTheRegisteredValidators()
    {
        var services = new ServiceCollection();
        services.AddValidationModules([
            new ValidatorRegistration(
                typeof(IValidatorFor<Pet>),
                static _ => PetValidator.Instance
            ),
        ]);
        services.AddValidationRunner<Pet>();

        var runner = services
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider.GetRequiredService<ValidationRunner<Pet>>();

        Assert.Equal(
            "name",
            Assert.Single(runner.Validate(new Pet { Toys = [new Toy { Name = "b" }] }).Errors).Field
        );
    }

    private static Pet ValidPet() =>
        new()
        {
            Name = "Rex",
            Tag = "tag",
            Sku = "ABC",
            Toys = [new Toy { Name = "ball" }],
        };
}
