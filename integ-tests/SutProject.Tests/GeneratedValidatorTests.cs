using Microsoft.Extensions.DependencyInjection;
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
/// only running it proves the semantics survived the trip through the
/// emitter.
/// </remarks>
public class GeneratedValidatorTests {

    [Fact]
    public void Generator_ProducedAValidatorPerAnnotatedType() {
        Assert.NotNull(new PetValidator());
        Assert.NotNull(new AddressValidator());
        Assert.NotNull(new ToyValidator());
    }

    [Fact]
    public void Validate_CleanValue_IsValid() {
        Assert.True(new PetValidator().IsValid(ValidPet()));
    }

    [Fact]
    public void Validate_CleanValue_AllocatesNothing() {
        // The validator is held, not constructed per call — which is what a container does with a
        // singleton, and what any caller should do. Constructing one per validation allocates the
        // object and, on first descent, the array of nested validators behind it.
        var validator = new PetValidator();
        var collector = new ValidationErrorCollector();
        var pet = ValidPet();

        for (var i = 0; i < 200; i++) {
            collector.Reset();
            validator.ValidateInto(collector, pet);
        }

        // The best of several windows rather than one, because tiered JIT can rejit inside a window
        // and the rejit itself allocates. That made this flaky under the full suite — it failed at
        // 784 and 1,568 bytes on separate runs and passed every time in isolation, which is the
        // signature of a measurement artefact rather than a leak.
        //
        // Taking the minimum does not weaken the assertion. A validator that genuinely allocated
        // per call would allocate in *every* window; only a one-off can be escaped by looking at
        // more than one. Steady state is what the promise is about, and this is how you observe it.
        Assert.Equal(0, BestOf(windows: 5, () => {
            for (var i = 0; i < 500; i++) {
                collector.Reset();
                validator.ValidateInto(collector, pet);
            }
        }));
    }

    /// <summary>
    /// The smallest number of bytes <paramref name="work"/> allocated across
    /// <paramref name="windows"/> runs.
    /// </summary>
    private static long BestOf(int windows, Action work) {
        var best = long.MaxValue;

        for (var window = 0; window < windows; window++) {
            var before = GC.GetAllocatedBytesForCurrentThread();

            work();

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (allocated < best) {
                best = allocated;
            }

            if (best == 0) {
                return 0;
            }
        }

        return best;
    }

    [Fact]
    public void Validate_MissingRequired_UsesTheSharedCodeAndComposedMessage() {
        var result = new PetValidator().Validate(ValidPet() with { Name = null });

        var error = Assert.Single(result.Errors, e => e.Field == "name");
        Assert.Equal(ValidationCodes.Required, error.Code);
        Assert.Equal("name is required.", error.Message);
    }

    [Fact]
    public void Validate_FailedRequired_SuppressesTheLengthConstraintOnTheSameField() {
        // Name carries both [Required] and [StringLength]. One error, not two.
        var result = new PetValidator().Validate(ValidPet() with { Name = null });

        Assert.Single(result.Errors);
    }

    [Fact]
    public void Validate_Errors_EmitInDeclarationOrder() {
        var pet = new Pet { Home = new Address(), Toys = new List<Toy> { new() } };

        var result = new PetValidator().Validate(pet);

        Assert.Equal(
            new[] { "name", "home.postal_code", "toys[0].name" },
            result.Errors.Select(error => error.Field));
    }

    [Fact]
    public void Validate_NestedObject_IsPathedThroughItsProperty() {
        var result = new PetValidator().Validate(ValidPet() with { Home = new Address() });

        // The field name comes from [JsonPropertyName], which outranks the camel-case default.
        Assert.Equal("home.postal_code", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_CollectionElements_AreIndexed() {
        var pet = ValidPet() with { Toys = new List<Toy> { new() { Name = "ball" }, new() } };

        var result = new PetValidator().Validate(pet);

        Assert.Equal("toys[1].name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_ItemCount_ReadsTheCollectionRatherThanItsElements() {
        var result = new PetValidator().Validate(ValidPet() with { Toys = new List<Toy>() });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.ArrayBounds, error.Code);
        Assert.Equal("toys must be between 1 and 3 items.", error.Message);
    }

    [Fact]
    public void Validate_Pattern_IsUnanchoredForTheNativeAttribute() {
        // [Pattern("^[A-Z]{3}$")] - the anchors are the author's, not ours.
        Assert.True(new PetValidator().IsValid(ValidPet() with { Sku = "ABC" }));
        Assert.False(new PetValidator().IsValid(ValidPet() with { Sku = "abc" }));
    }

    [Fact]
    public void Validate_Range_ProducesTheComposedMessage() {
        var result = new PetValidator().Validate(ValidPet() with { Age = 99 });

        Assert.Equal("age must be between 0 and 30.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_AllowedValues_ListsThePermittedSet() {
        var result = new PetValidator().Validate(ValidPet() with { Status = "unknown" });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.Enum, error.Code);
        Assert.Equal("status must be one of: available, pending, sold.", error.Message);
    }

    [Fact]
    public void Validate_ExplicitMessage_IsEmittedVerbatim() {
        var result = new OwnerValidator().Validate(new Owner());

        Assert.Equal("an owner must be named", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_NullableValueType_IsReadThroughItsValue() {
        Assert.True(new ReadingValidator().IsValid(new Reading { Ratio = 0.5 }));

        var missing = new ReadingValidator().Validate(new Reading());
        Assert.Equal(ValidationCodes.Required, Assert.Single(missing.Errors).Code);
    }

    [Fact]
    public void Validate_ExclusiveUpperBound_RejectsTheBoundItself() {
        Assert.False(new ReadingValidator().IsValid(new Reading { Ratio = 1.0 }));
        Assert.True(new ReadingValidator().IsValid(new Reading { Ratio = 0.999 }));
    }

    [Fact]
    public void GeneratedValidators_RegisterThroughTheGeneratedExtension() {
        // DependencyModules is not referenced here, so the IServiceCollection branch is what was
        // emitted. The container constructs the validator and owns it; there is no shared static to
        // compare against, so what is asserted is that one resolves and that it is a singleton.
        var services = new ServiceCollection();
        services.AddSutProjectValidators();

        using var provider = services.BuildServiceProvider();

        var pet = provider.GetRequiredService<IValidatorFor<Pet>>();

        Assert.IsType<PetValidator>(pet);
        Assert.IsType<AddressValidator>(provider.GetRequiredService<IValidatorFor<Address>>());

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IValidatorFor<Pet>>(),
            second.ServiceProvider.GetRequiredService<IValidatorFor<Pet>>());
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
