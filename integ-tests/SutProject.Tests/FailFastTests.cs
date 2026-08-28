using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// Fail-fast through a <i>generated</i> validator, which is the path the emitter changed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ValidationModules.Runtime.Tests"/> covers the same mode over
/// <c>DescribedValidator&lt;T&gt;</c>. This project compiles real generated code, so it is the only
/// place the emitted <c>&amp;&amp; ctx.Report(...).ShouldStop</c> shape and the propagation out of a
/// nested descent are actually executed.
/// </para>
/// <para>
/// A <c>Pet</c> with nothing set fails Name, Age is in range at 0, Toys is empty so ItemCount
/// fails, and Home is null so no descent happens. Ordering is declaration order, so the first
/// blocking failure is Name.
/// </para>
/// </remarks>
public class FailFastTests {

    private static readonly PetValidator Validator = new();

    private static ValidationResult Run(Pet pet, ValidationStopMode mode) {
        var collector = new ValidationErrorCollector { StopMode = mode };

        Validator.ValidateInto(collector, pet);

        return collector.ToResult();
    }

    [Fact]
    public void CollectAll_ReportsEveryFailure() {
        var result = Run(new Pet(), ValidationStopMode.CollectAll);

        Assert.True(result.Errors.Count > 1, "the default mode reports the whole set");
        Assert.Contains(result.Errors, error => error.Field == "name");
        Assert.Contains(result.Errors, error => error.Field == "toys");
    }

    [Fact]
    public void StopOnFirstError_ReportsOnlyTheFirstInDeclarationOrder() {
        var result = Run(new Pet(), ValidationStopMode.StopOnFirstError);

        Assert.Equal("name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void ValidateFirst_IsTheEntryPointForIt() {
        var result = Validator.ValidateFirst(new Pet());

        Assert.Equal("name", Assert.Single(result.Errors).Field);
    }

    /// <summary>
    /// The generated descent propagates the nested validator's answer rather than carrying on, so a
    /// failure inside Home ends the pass before Toys is walked.
    /// </summary>
    [Fact]
    public void StopOnFirstError_PropagatesOutOfANestedDescent() {
        var pet = new Pet {
            Name = "Ada",
            Age = 3,
            Home = new Address(),
            Toys = [new Toy()]
        };

        var result = Run(pet, ValidationStopMode.StopOnFirstError);

        Assert.Equal("home.postal_code", Assert.Single(result.Errors).Field);
    }

    /// <summary>The same graph under the default mode finds the toy as well.</summary>
    [Fact]
    public void CollectAll_WalksPastTheNestedFailure() {
        var pet = new Pet {
            Name = "Ada",
            Age = 3,
            Home = new Address(),
            Toys = [new Toy()]
        };

        var result = Run(pet, ValidationStopMode.CollectAll);

        Assert.Contains(result.Errors, error => error.Field == "home.postal_code");
        Assert.Contains(result.Errors, error => error.Field == "toys[0].name");
    }

    /// <summary>
    /// Collection elements stop too: the second element is never validated once the first failed.
    /// </summary>
    [Fact]
    public void StopOnFirstError_StopsBetweenCollectionElements() {
        var pet = new Pet {
            Name = "Ada",
            Age = 3,
            Toys = [new Toy(), new Toy()]
        };

        var result = Run(pet, ValidationStopMode.StopOnFirstError);

        Assert.Equal("toys[0].name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void AValidPet_IsValidEitherWay() {
        var pet = new Pet {
            Name = "Ada",
            Age = 3,
            Toys = [new Toy { Name = "ball" }]
        };

        Assert.True(Run(pet, ValidationStopMode.StopOnFirstError).IsValid);
        Assert.True(Run(pet, ValidationStopMode.CollectAll).IsValid);
    }
}
