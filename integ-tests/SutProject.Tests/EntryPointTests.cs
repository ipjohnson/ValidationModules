using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// The four ways to run a validator, against generated code rather than a hand-written stand-in.
/// </summary>
/// <remarks>
/// Each entry point exists because it makes a different trade — the full error list, a verdict, an
/// exception, or a caller-owned collector — and each is documented with a promise about what it
/// costs. What matters is that they never disagree about *whether* a value is valid, because a
/// caller who switches from one to another to save an allocation should not change behaviour.
/// </remarks>
public class EntryPointTests {

    private static Pet Valid() => new() {
        Name = "Rex",
        Sku = "ABC",
        Slug = "rex-the-dog",
        Age = 3,
        Status = "available",
        Home = new Address { PostalCode = "SW1" },
        Toys = [new Toy { Name = "ball" }],
    };

    public static TheoryData<Pet, bool> Cases() => new() {
        { Valid(), true },
        { Valid() with { Name = null }, false },
        { Valid() with { Name = "   " }, false },
        { Valid() with { Name = "a name that is far too long" }, false },
        { Valid() with { Age = 99 }, false },
        { Valid() with { Sku = "abc" }, false },
        { Valid() with { Status = "unknown" }, false },
        { Valid() with { Home = new Address { PostalCode = null } }, false },
        { Valid() with { Toys = [] }, false },
        { Valid() with { Toys = [new Toy { Name = null }] }, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsValid_AgreesWithValidate(Pet pet, bool expected) {
        // IsValid stops as soon as it knows, so it is the one entry point that does not build the
        // whole list. That is only safe if it reaches the same verdict.
        Assert.Equal(expected, PetValidator.Instance.IsValid(pet));
        Assert.Equal(expected, PetValidator.Instance.Validate(pet).IsValid);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ValidateAndThrow_ThrowsExactlyWhenValidateSaysInvalid(Pet pet, bool expected) {
        var exception = Record.Exception(() => PetValidator.Instance.ValidateAndThrow(pet));

        if (expected) {
            Assert.Null(exception);
        } else {
            Assert.IsType<ValidationException>(exception);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ValidateInto_ProducesTheSameErrorsAsValidate(Pet pet, bool expected) {
        var collector = new ValidationErrorCollector();

        PetValidator.Instance.ValidateInto(collector, pet);

        Assert.Equal(expected, collector.ToResult().IsValid);
        Assert.Equal(
            PetValidator.Instance.Validate(pet).Errors,
            collector.ToResult().Errors);
    }

    [Fact]
    public void ValidateAndThrow_CarriesTheFullResultOnTheException() {
        var pet = Valid() with { Name = null, Age = 99 };

        var exception = Assert.Throws<ValidationException>(() => PetValidator.Instance.ValidateAndThrow(pet));

        Assert.Equal(
            PetValidator.Instance.Validate(pet).Errors,
            exception.Result.Errors);
    }

    [Fact]
    public void ValidateInto_AccumulatesAcrossValuesWhenTheCollectorIsReused() {
        // The pooling shape: one collector, several values. Errors accumulate rather than the
        // second pass clearing the first.
        var collector = new ValidationErrorCollector();

        PetValidator.Instance.ValidateInto(collector, Valid() with { Name = null });
        PetValidator.Instance.ValidateInto(collector, Valid() with { Age = 99 });

        Assert.Equal(2, collector.Count);
    }

    [Fact]
    public void ValidateInto_ResetClearsForTheNextValue() {
        var collector = new ValidationErrorCollector();

        PetValidator.Instance.ValidateInto(collector, Valid() with { Name = null });
        collector.Reset();
        PetValidator.Instance.ValidateInto(collector, Valid());

        Assert.True(collector.ToResult().IsValid);
        Assert.Equal(0, collector.Count);
    }

    [Fact]
    public void Reset_ClearsTheRequiredSuppressionStateToo() {
        // Suppression is per-pass state living in the collector, so a reused collector that does
        // not clear it would silently drop a second value's non-required errors on the same field.
        var collector = new ValidationErrorCollector();

        PetValidator.Instance.ValidateInto(collector, Valid() with { Name = null });
        collector.Reset();
        PetValidator.Instance.ValidateInto(collector, Valid() with { Name = "a name that is far too long" });

        Assert.Equal(ValidationCodes.StringLength, Assert.Single(collector.ToResult().Errors).Code);
    }

    [Fact]
    public void Validate_ReturnsAnIndependentResultEachCall() {
        var pet = Valid() with { Name = null };

        var first = PetValidator.Instance.Validate(pet);
        var second = PetValidator.Instance.Validate(pet);

        Assert.NotSame(first, second);
        Assert.Equal(first.Errors, second.Errors);
    }

    [Fact]
    public void Validate_OnACleanValue_ReturnsTheSharedValidResult() {
        // Safe only because ValidationResult is immutable — there is no AddError to poison it with.
        Assert.Same(ValidationResult.Valid, PetValidator.Instance.Validate(Valid()));
    }

    [Fact]
    public void Merge_CombinesTwoResultsWithoutMutatingEither() {
        var first = PetValidator.Instance.Validate(Valid() with { Name = null });
        var second = PetValidator.Instance.Validate(Valid() with { Age = 99 });

        var merged = first.Merge(second);

        Assert.Equal(2, merged.Errors.Count);
        Assert.Single(first.Errors);
        Assert.Single(second.Errors);
    }

    [Fact]
    public void Merge_WithAValidResult_KeepsTheFailures() {
        var failing = PetValidator.Instance.Validate(Valid() with { Name = null });

        Assert.Single(failing.Merge(ValidationResult.Valid).Errors);
        Assert.Single(ValidationResult.Valid.Merge(failing).Errors);
    }
}
