using Microsoft.Extensions.DependencyInjection;
using SutProject;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Exercises validators the generator actually produced, in a project that compiled them against
/// the runtime a consumer would reference.
/// </summary>
/// <remarks>
/// This is the half of source-generator testing that golden files cannot do. A snapshot proves the
/// emitted text is what was expected; only a real compilation proves it is valid C# that binds, and
/// only running it proves the semantics in API-SURFACE.md §4.2 survived the trip through the
/// emitter.
/// </remarks>
public class GeneratedValidatorTests {

    [Fact]
    public void Generator_ProducedAValidatorPerAnnotatedType() {
        Assert.NotNull(PetValidator.Instance);
        Assert.NotNull(AddressValidator.Instance);
        Assert.NotNull(ToyValidator.Instance);
    }

    [Fact]
    public void Validate_CleanValue_IsValid() {
        Assert.True(PetValidator.Instance.IsValid(ValidPet()));
    }

    [Fact]
    public void Validate_CleanValue_AllocatesNothing() {
        var collector = new ValidationErrorCollector();
        var pet = ValidPet();

        for (var i = 0; i < 200; i++) {
            collector.Reset();
            PetValidator.Instance.ValidateInto(collector, pet);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++) {
            collector.Reset();
            PetValidator.Instance.ValidateInto(collector, pet);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Validate_MissingRequired_UsesTheSharedCodeAndComposedMessage() {
        var result = PetValidator.Instance.Validate(ValidPet() with { Name = null });

        var error = Assert.Single(result.Errors, e => e.Field == "name");
        Assert.Equal(ValidationCodes.Required, error.Code);
        Assert.Equal("name is required.", error.Message);
    }

    [Fact]
    public void Validate_FailedRequired_SuppressesTheLengthConstraintOnTheSameField() {
        // Name carries both [Required] and [StringLength]. One error, not two.
        var result = PetValidator.Instance.Validate(ValidPet() with { Name = null });

        Assert.Single(result.Errors);
    }

    [Fact]
    public void Validate_Errors_EmitInDeclarationOrder() {
        var pet = new Pet { Home = new Address(), Toys = new List<Toy> { new() } };

        var result = PetValidator.Instance.Validate(pet);

        Assert.Equal(
            new[] { "name", "home.postal_code", "toys[0].name" },
            result.Errors.Select(error => error.Field));
    }

    [Fact]
    public void Validate_NestedObject_IsPathedThroughItsProperty() {
        var result = PetValidator.Instance.Validate(ValidPet() with { Home = new Address() });

        // The field name comes from [JsonPropertyName], which outranks the camel-case default.
        Assert.Equal("home.postal_code", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_CollectionElements_AreIndexed() {
        var pet = ValidPet() with { Toys = new List<Toy> { new() { Name = "ball" }, new() } };

        var result = PetValidator.Instance.Validate(pet);

        Assert.Equal("toys[1].name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_ItemCount_ReadsTheCollectionRatherThanItsElements() {
        var result = PetValidator.Instance.Validate(ValidPet() with { Toys = new List<Toy>() });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.ArrayBounds, error.Code);
        Assert.Equal("toys must be between 1 and 3 items.", error.Message);
    }

    [Fact]
    public void Validate_Pattern_IsUnanchoredForTheNativeAttribute() {
        // [Pattern("^[A-Z]{3}$")] - the anchors are the author's, not ours.
        Assert.True(PetValidator.Instance.IsValid(ValidPet() with { Sku = "ABC" }));
        Assert.False(PetValidator.Instance.IsValid(ValidPet() with { Sku = "abc" }));
    }

    [Fact]
    public void Validate_Range_ProducesTheComposedMessage() {
        var result = PetValidator.Instance.Validate(ValidPet() with { Age = 99 });

        Assert.Equal("age must be between 0 and 30.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_AllowedValues_ListsThePermittedSet() {
        var result = PetValidator.Instance.Validate(ValidPet() with { Status = "unknown" });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.Enum, error.Code);
        Assert.Equal("status must be one of: available, pending, sold.", error.Message);
    }

    [Fact]
    public void Validate_ExplicitMessage_IsEmittedVerbatim() {
        var result = OwnerValidator.Instance.Validate(new Owner());

        Assert.Equal("an owner must be named", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_NullableValueType_IsReadThroughItsValue() {
        Assert.True(ReadingValidator.Instance.IsValid(new Reading { Ratio = 0.5 }));

        var missing = ReadingValidator.Instance.Validate(new Reading());
        Assert.Equal(ValidationCodes.Required, Assert.Single(missing.Errors).Code);
    }

    [Fact]
    public void Validate_ExclusiveUpperBound_RejectsTheBoundItself() {
        Assert.False(ReadingValidator.Instance.IsValid(new Reading { Ratio = 1.0 }));
        Assert.True(ReadingValidator.Instance.IsValid(new Reading { Ratio = 0.999 }));
    }

    [Fact]
    public void GeneratedValidators_RegisterThroughAddValidationModules() {
        // DependencyModules is not referenced here, so the static-table branch is what was emitted.
        var services = new ServiceCollection();
        services.AddValidationModules(GeneratedValidators.All);

        var provider = services.BuildServiceProvider();

        Assert.Same(PetValidator.Instance, provider.GetRequiredService<IValidatorFor<Pet>>());
        Assert.Same(AddressValidator.Instance, provider.GetRequiredService<IValidatorFor<Address>>());
    }

    private static Pet ValidPet() =>
        new() {
            Name = "Rex",
            Sku = "ABC",
            Age = 3,
            Status = "available",
            Toys = new List<Toy> { new() { Name = "ball" } },
        };
}
