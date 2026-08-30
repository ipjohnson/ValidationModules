using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins the composition policy: everything registered runs, results
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

    [Fact]
    public void Validate_CleanValue_DoesNotBoxAnEnumerator() {
        // The runner held both dependencies as IEnumerable<T> and foreach'd them. Iterating an
        // array through the interface boxes its enumerator - 32 bytes per call, and the async path
        // paid it twice, which is what holding arrays fixes.
        //
        // Asserted through the runner rather than by reading the field, because the promise is
        // about what a caller is charged, not about the storage that delivers it.
        var runner = new ValidationRunner<Pet>([new CleanValidator()], []);
        var pet = ValidPet();

        for (var i = 0; i < 200; i++) {
            runner.Validate(pet);
        }

        var best = long.MaxValue;

        for (var window = 0; window < 5 && best != 0; window++) {
            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 500; i++) {
                runner.Validate(pet);
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // A clean pass still allocates the collector itself, which is 56 bytes and deliberate -
        // see ValidationErrorCollector's remarks on why pooling was dropped. It carries the
        // monotonic path stamp that lets a context detect an overwritten path, which took it from
        // 40 to 48, and the IServiceProvider a Runtime-polymorphic descent resolves through, which
        // took it from 48 to 56. Constructor-only is what makes that field the scope's, so a
        // collector cannot be re-armed for a different one. What must not be here is a per-call
        // enumerator on top of it, or the path buffer - that is rented, not allocated.
        Assert.Equal(56 * 500, best);
    }

    /// <summary>A validator that finds nothing, so the pass stays on its clean path.</summary>
    private sealed class CleanValidator : IValidatorFor<Pet> {
        public ValidationFlow Validate(ref ValidationContext context, Pet value) => ValidationFlow.Continue;
    }

    private sealed class NestedAsyncValidator : IAsyncValidatorFor<Pet> {
        public async ValueTask ValidateAsync(ValidationContext context, Pet value, CancellationToken cancellationToken) {
            var home = context.Push("home");

            // The context is used after an await, which is the case the ref struct design could not
            // express at all.
            await Task.Yield();

            home.Report("postalCode", "unknown", "postal code not recognised.");
        }
    }

    private sealed class TaggingAsyncValidator(string code) : IAsyncValidatorFor<Pet> {
        public async ValueTask ValidateAsync(ValidationContext context, Pet value, CancellationToken cancellationToken) {
            await Task.Yield();

            context.Report("name", code, "x");
        }
    }

    /// <summary>
    /// A structural warning does not make a value invalid, so it must not stop the business rules
    /// from running. Gating on HasErrors - which counts any severity - skipped them silently, and
    /// the response looked clean.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_StructuralWarning_StillRunsAsyncValidators() {
        var runner = new ValidationRunner<Pet>([WarnsOnly.Instance], [RecordsThatItRan.Instance]);

        var result = await runner.ValidateAsync(ValidPet(), TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, error => error.Code == "async_ran");
        Assert.True(result.IsValid is false || result.Errors.Count > 0);
    }

    /// <summary>A blocking error still short-circuits: that gate is the point.</summary>
    [Fact]
    public async Task ValidateAsync_StructuralError_SkipsAsyncValidators() {
        var runner = new ValidationRunner<Pet>([FailsOnly.Instance], [RecordsThatItRan.Instance]);

        var result = await runner.ValidateAsync(ValidPet(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Errors, error => error.Code == "async_ran");
    }

    private sealed class WarnsOnly : IValidatorFor<Pet> {
        public static readonly WarnsOnly Instance = new();

        public ValidationFlow Validate(ref ValidationContext context, Pet value) =>
            context.Report("name", "advisory", "worth a look", ValidationSeverity.Warning);
    }

    private sealed class FailsOnly : IValidatorFor<Pet> {
        public static readonly FailsOnly Instance = new();

        public ValidationFlow Validate(ref ValidationContext context, Pet value) =>
            context.Report("name", "blocked", "no", ValidationSeverity.Error);
    }

    private sealed class RecordsThatItRan : IAsyncValidatorFor<Pet> {
        public static readonly RecordsThatItRan Instance = new();

        public ValueTask ValidateAsync(ValidationContext context, Pet value, CancellationToken cancellationToken) {
            context.Report("policy", "async_ran", "the business rule ran");
            return default;
        }
    }
}
