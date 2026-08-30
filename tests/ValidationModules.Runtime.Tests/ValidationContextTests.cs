using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins the path shapes and the ordering guarantees. Two engines
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
    public void Push_SequentialSiblingDescents_EachReportItsOwnPath() {
        // The shape every engine emits: descend, use, unwind, descend again. The emitter scopes
        // each child context to its own if-block or loop iteration, and EachRule/NestedRule do the
        // same, so two sibling contexts are never live at once.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        {
            var home = context.Push("home");
            home.Report("postalCode", "required", "x");
        }

        {
            var work = context.Push("work");
            work.Report("postalCode", "required", "x");
        }

        Assert.Equal(
            ["home.postalCode", "work.postalCode"],
            collector.ToResult().Errors.Select(error => error.Field));
    }

    [Fact]
    public void Push_ParentAddsAfterAChildHasUnwound_StillReportsItsOwnPath() {
        // A parent context stays valid across a completed child descent - the child's segments sit
        // above the parent's depth and are never read from it. This is what makes a validator able
        // to descend into a nested object and then report on its own fields afterwards.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.Push("home").Report("postalCode", "required", "x");
        context.PushIndex("toys", 7).Report("name", "required", "x");
        context.Report("id", "required", "x");

        Assert.Equal(
            ["home.postalCode", "toys[7].name", "id"],
            collector.ToResult().Errors.Select(error => error.Field));
    }

    [Fact]
    public void AddHere_AddsAgainstTheObjectRatherThanAField() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.Push("home").ReportHere("incomplete", "address is incomplete.");

        Assert.Equal("home", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Push_TwoDeep_ReportsEverySegment() {
        // The boundary case for elision: two descents is the whole path, so the marker must not
        // appear. Getting this off by one would put `...` into the commonest nested shape there is.
        var collector = new ValidationErrorCollector();

        new ValidationContext(collector).Push("body").PushIndex("lines", 3).Report("sku", "required", "x");

        Assert.Equal("body.lines[3].sku", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Push_ThreeDeep_ElidesTheMiddleAndSaysSo() {
        var collector = new ValidationErrorCollector();

        new ValidationContext(collector)
            .Push("body").Push("order").Push("address")
            .Report("postalCode", "required", "x");

        Assert.Equal("body...address.postalCode", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Push_FarDeeper_StillKeepsOnlyOutermostAndImmediateParent() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector).Push("body");

        for (var i = 0; i < 20; i++) {
            context = context.Push($"level{i}");
        }

        context.Report("name", "required", "x");

        Assert.Equal("body...level19.name", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Push_IndexedOutermostSegment_KeepsItsIndex() {
        // Rendering `toys.owner.name` for what is really `toys[3].owner.name` would not be a
        // shortened path, it would be a false one - it asserts an object at `toys`.
        var collector = new ValidationErrorCollector();

        new ValidationContext(collector).PushIndex("toys", 3).Push("owner").Report("name", "required", "x");

        Assert.Equal("toys[3].owner.name", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Push_IndexAndKeyOnBothRetainedSegments_SurviveElision() {
        var collector = new ValidationErrorCollector();

        new ValidationContext(collector)
            .PushIndex("lines", 2).Push("shipTo").PushKey("tags", "a")
            .Report("value", "required", "x");

        Assert.Equal("lines[2]...tags[a].value", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void AddHere_AtDepth_PathsTheObjectRatherThanAField() {
        var collector = new ValidationErrorCollector();

        new ValidationContext(collector)
            .Push("body").Push("order").PushIndex("lines", 4)
            .ReportHere("incomplete", "line is incomplete.");

        Assert.Equal("body...lines[4]", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Push_PastMaxDepth_ThrowsRatherThanOverflowingTheStack() {
        var context = new ValidationContext(new ValidationErrorCollector());

        for (var i = 0; i < ValidationErrorCollector.DefaultDepthLimit; i++) {
            context = context.Push("child");
        }

        var exception = Assert.Throws<InvalidOperationException>(() => context.Push("child"));

        Assert.Contains("cycle", exception.Message);
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
        context.Report("name", "required", "x");
        var after = context.ErrorCount;

        Assert.Equal(0, before);
        Assert.Equal(1, after);
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

    // ---- path mode -----------------------------------------------------------------------

    [Fact]
    public void Bounded_IsTheDefault_AndElidesTheMiddle() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.Push("body").Push("order").Push("address").Report("postalCode", "required", "x");

        Assert.Equal("body...address.postalCode", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Full_RendersEverySegmentWalked() {
        var collector = new ValidationErrorCollector(ValidationPathMode.Full);
        var context = new ValidationContext(collector);

        context.Push("body").Push("order").Push("address").Report("postalCode", "required", "x");

        Assert.Equal("body.order.address.postalCode", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Full_KeepsIndicesAndKeysOnEverySegment() {
        var collector = new ValidationErrorCollector(ValidationPathMode.Full);
        var context = new ValidationContext(collector);

        context.Push("spec").PushIndex("containers", 2).PushKey("labels", "app")
               .Report("value", "required", "x");

        Assert.Equal(
            "spec.containers[2].labels[app].value",
            Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Full_AtDepthOneAndZero_MatchesBounded() {
        var full = new ValidationErrorCollector(ValidationPathMode.Full);
        var context = new ValidationContext(full);

        context.Report("id", "required", "x");
        context.Push("home").Report("postalCode", "required", "x");

        Assert.Equal(
            ["id", "home.postalCode"],
            full.ToResult().Errors.Select(error => error.Field));
    }

    [Fact]
    public void Full_ReportsTheContainerItselfWithAddHere() {
        var collector = new ValidationErrorCollector(ValidationPathMode.Full);
        var context = new ValidationContext(collector);

        context.Push("body").PushIndex("lines", 4).ReportHere("invalid", "x");

        Assert.Equal("body.lines[4]", Assert.Single(collector.ToResult().Errors).Field);
    }

    // ---- depth-first contract ------------------------------------------------------------

    [Fact]
    public void Push_TwoLiveSiblingContexts_ThrowsRatherThanReportingTheWrongPath() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        var first = context.Push("home");
        var second = context.Push("work");

        second.Report("postalCode", "required", "x");

        // `first` now describes a segment that has been overwritten. Reporting it would attribute
        // the error to "work", so it fails instead.
        var exception = Assert.Throws<InvalidOperationException>(
            () => first.Report("postalCode", "required", "x"));

        Assert.Contains("no longer describes where it was created", exception.Message);
    }

    [Fact]
    public void Push_ContextUsedAfterADeeperDescentUnwound_IsStillValid() {
        // The legitimate shape: a parent reports on itself after a child has finished.
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        var body = context.Push("body");
        body.Push("address").Report("postalCode", "required", "x");
        body.Report("id", "required", "x");

        Assert.Equal(
            ["body.address.postalCode", "body.id"],
            collector.ToResult().Errors.Select(error => error.Field));
    }

    [Fact]
    public void Push_LoopOverElements_ReusesTheSameDepthWithoutTripping() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        for (var i = 0; i < 50; i++) {
            context.PushIndex("toys", i).Push("owner").Report("name", "required", "x");
        }

        Assert.Equal(50, collector.ToResult().Errors.Count);
    }
}
