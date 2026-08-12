using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins the path shapes and the ordering guarantees in IMPLEMENTATION-PLAN.md §4. Two engines
/// producing "the same" errors in different orders are not substitutable, so these are the
/// assertions the conformance suite will eventually run against every engine.
/// </summary>
public class ValidationContextTests {

    [Fact]
    public void Add_RootField_HasNoPathPrefix() {
        var result = PetValidator.Instance.Validate(new Pet { Toys = [new Toy { Name = "ball" }] });

        Assert.Equal("name", result.Errors[0].Field);
    }

    [Fact]
    public void Push_NestedObject_PrefixesFieldWithParent() {
        var pet = ValidPet() with { Home = new Address() };

        var result = PetValidator.Instance.Validate(pet);

        Assert.Contains(result.Errors, error => error.Field == "home.postalCode");
    }

    [Fact]
    public void PushIndex_CollectionElement_UsesBracketedIndex() {
        var pet = ValidPet() with { Toys = [new Toy { Name = "ball" }, new Toy()] };

        var result = PetValidator.Instance.Validate(pet);

        Assert.Equal("toys[1].name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Push_SiblingContexts_DoNotOverwriteEachOther() {
        // The failure this guards against: with a depth-indexed path stack, the second Push
        // clobbers the first, and both errors come out under the later segment.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        var first = context.Push("home");
        var second = context.Push("work");

        first.Add("postalCode", "required", "x");
        second.Add("postalCode", "required", "x");

        Assert.Equal(
            ["home.postalCode", "work.postalCode"],
            collector.ToResult().Errors.Select(error => error.Field));
    }

    [Fact]
    public void Push_StoredAndUsedLater_StillReportsItsOwnPath() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        var stored = context.Push("home");

        // Unrelated pushes in between, which a shared mutable path stack would not survive.
        context.Push("work").Add("postalCode", "required", "x");
        context.PushIndex("toys", 7).Add("name", "required", "x");

        stored.Add("postalCode", "required", "x");

        Assert.Equal("home.postalCode", collector.ToResult().Errors[^1].Field);
    }

    [Fact]
    public void AddHere_AddsAgainstTheObjectRatherThanAField() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.Push("home").AddHere("incomplete", "address is incomplete.");

        Assert.Equal("home", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Validate_MultipleFailures_EmitsInDeclarationOrder() {
        var pet = new Pet { Home = new Address(), Toys = [new Toy()] };

        var result = PetValidator.Instance.Validate(pet);

        Assert.Equal(
            ["name", "home.postalCode", "toys[0].name"],
            result.Errors.Select(error => error.Field));
    }

    [Fact]
    public void Validate_FailedRequired_SuppressesOtherConstraintsOnTheSameField() {
        var pet = ValidPet() with { Name = "   " };

        var result = PetValidator.Instance.Validate(pet);

        var error = Assert.Single(result.Errors);
        Assert.Equal("required", error.Code);
    }

    [Fact]
    public void Validate_MultipleFailures_CollectsAllOfThem() {
        var pet = new Pet { Home = new Address(), Toys = [new Toy(), new Toy()] };

        var result = PetValidator.Instance.Validate(pet);

        Assert.Equal(4, result.Errors.Count);
    }

    [Fact]
    public void ErrorCount_SnapshottedAroundABlock_DetectsLocalFailure() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        var before = context.ErrorCount;
        context.Add("name", "required", "x");
        var after = context.ErrorCount;

        Assert.Equal(0, before);
        Assert.Equal(1, after);
    }

    [Fact]
    public void Profile_FlowsFromTheCollector() {
        var collector = new ValidationErrorCollector(typeof(V2));

        var context = new ValidationContext(collector);

        Assert.Equal(typeof(V2), context.Profile);
    }

    [Fact]
    public void Constructor_NullCollector_Throws() {
        Assert.Throws<ArgumentNullException>(() => new ValidationContext(null!));
    }

    private static Pet ValidPet() =>
        new() {
            Name = "Rex",
            Tag = "tag",
            Sku = "ABC",
            Toys = [new Toy { Name = "ball" }],
        };
}
