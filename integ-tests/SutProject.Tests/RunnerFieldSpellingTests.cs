using Microsoft.Extensions.DependencyInjection;
using SutProject;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// One property, one runner, one spelling - whichever side of the pass reported it.
/// </summary>
/// <remarks>
/// The rc1015 finding this pins: a generated constraint reported <c>name</c> while a hand-written
/// <see cref="IAsyncValidatorFor{T}"/> on the same property reported the verbatim
/// <c>nameof</c> spelling <c>Name</c>, in the same result list. A client keying off
/// <c>errors["name"]</c> silently missed the async failure.
/// </remarks>
public class RunnerFieldSpellingTests {

    private sealed class NameIsTaken : IAsyncValidatorFor<Pet> {
        public async ValueTask ValidateAsync(
            ValidationContext context, Pet value, CancellationToken cancellationToken = default) {
            await Task.Yield();

            // Verbatim nameof, exactly what a hand-written business rule writes. The generator
            // never sees this code, so the runtime namer is what has to agree with the literals.
            context.Report(nameof(Pet.Name), "name_taken", "name is already taken.");
        }
    }

    private sealed class StepZeroIsWrong : IAsyncValidatorFor<Pet> {
        public async ValueTask ValidateAsync(
            ValidationContext context, Pet value, CancellationToken cancellationToken = default) {
            await Task.Yield();

            context.Report("steps[0]", "step_wrong", "the first step is wrong.");
        }
    }

    private static ServiceProvider Provider<TRule>() where TRule : class, IAsyncValidatorFor<Pet> {
        var services = new ServiceCollection();

        services.AddSutProjectValidators();
        services.AddScoped<IAsyncValidatorFor<Pet>, TRule>();

        return services.BuildServiceProvider();
    }

    private static Pet CleanPet() => new() {
        Name = "Rex",
        Sku = "ABC",
        Slug = "rex-1",
        Age = 3,
        Status = "available",
        Toys = new List<Toy> { new() { Name = "ball" } },
    };

    [Fact]
    public async Task GeneratedAndHandWrittenFailures_OnOneProperty_ShareOneWireName() {
        await using var provider = Provider<NameIsTaken>();
        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<Pet>>();

        // The structural failure on Name, from the generated [Required] literal.
        var structural = await runner.ValidateAsync(CleanPet() with { Name = null });

        // The async failure on the same property, from the hand-written nameof.
        var business = await runner.ValidateAsync(CleanPet());

        var structuralField = Assert.Single(structural.Errors, e => e.Code == "required").Field;
        var businessField = Assert.Single(business.Errors, e => e.Code == "name_taken").Field;

        Assert.Equal("name", structuralField);
        Assert.Equal(structuralField, businessField);
    }

    [Fact]
    public async Task ADeliberatelyShapedField_SurvivesUntouched() {
        await using var provider = Provider<StepZeroIsWrong>();
        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<Pet>>();

        var result = await runner.ValidateAsync(CleanPet());

        Assert.Equal("steps[0]", Assert.Single(result.Errors, e => e.Code == "step_wrong").Field);
    }
}
