using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins the composition policy in IMPLEMENTATION-PLAN.md §8: everything registered runs, results
/// merge rather than one replacing another, and business rules are gated on structural validation
/// passing.
/// </summary>
public class ValidationRunnerTests {

    [Fact]
    public void Validate_RunsEveryStructuralValidatorAndMergesResults() {
        var runner = new ValidationRunner<Pet>([PetValidator.Instance, PetValidatorV2.Instance], []);

        var result = runner.Validate(new Pet { Name = "Rex", Toys = [new Toy { Name = "ball" }] });

        // toys passes; the two validators disagree only about Tag, and both verdicts survive.
        Assert.Equal(["tag"], result.Errors.Select(error => error.Field));
    }

    [Fact]
    public async Task ValidateAsync_StructuralPasses_RunsBusinessRules() {
        var runner = new ValidationRunner<Pet>(
            [PetValidator.Instance],
            [new PetNameUniquenessValidator("Rex")]);

        var result = await runner.ValidateAsync(ValidPet(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("duplicate", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task ValidateAsync_StructuralFails_SkipsBusinessRules() {
        // The point of the gate: a uniqueness check must not reach the database for a null field.
        var business = new RecordingAsyncValidator();
        var runner = new ValidationRunner<Pet>([PetValidator.Instance], [business]);

        var result = await runner.ValidateAsync(
            new Pet { Toys = [new Toy { Name = "ball" }] },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(business.WasCalled);
        Assert.Equal("required", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task ValidateAsync_BusinessRuleErrors_ShareThePathVocabulary() {
        var runner = new ValidationRunner<Pet>([], [new NestedAsyncValidator()]);

        var result = await runner.ValidateAsync(ValidPet(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("home.postalCode", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public async Task ValidateAsync_MultipleBusinessRules_RunInRegistrationOrder() {
        var runner = new ValidationRunner<Pet>(
            [],
            [new TaggingAsyncValidator("first"), new TaggingAsyncValidator("second")]);

        var result = await runner.ValidateAsync(ValidPet(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], result.Errors.Select(error => error.Code));
    }

    [Fact]
    public void Validate_CleanValue_ReturnsTheSharedValidInstance() {
        var runner = new ValidationRunner<Pet>([PetValidator.Instance], []);

        Assert.Same(ValidationResult.Valid, runner.Validate(ValidPet()));
    }

    private static Pet ValidPet() =>
        new() {
            Name = "Rex",
            Tag = "tag",
            Sku = "ABC",
            Toys = [new Toy { Name = "ball" }],
        };

    private sealed class RecordingAsyncValidator : IAsyncValidatorFor<Pet> {
        public bool WasCalled { get; private set; }

        public ValueTask ValidateAsync(ValidationContext context, Pet value, CancellationToken cancellationToken) {
            WasCalled = true;

            return default;
        }
    }

    private sealed class NestedAsyncValidator : IAsyncValidatorFor<Pet> {
        public async ValueTask ValidateAsync(ValidationContext context, Pet value, CancellationToken cancellationToken) {
            var home = context.Push("home");

            // The context is used after an await, which is the case the ref struct design could not
            // express at all.
            await Task.Yield();

            home.Add("postalCode", "unknown", "postal code not recognised.");
        }
    }

    private sealed class TaggingAsyncValidator(string code) : IAsyncValidatorFor<Pet> {
        public async ValueTask ValidateAsync(ValidationContext context, Pet value, CancellationToken cancellationToken) {
            await Task.Yield();

            context.Add("name", code, "x");
        }
    }
}
