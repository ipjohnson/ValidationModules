# Testing

A generated validator is an ordinary class with a static `Instance`. Testing one needs no harness:

```csharp
[Fact]
public void Name_IsRequired() {
    var result = new PetValidator().Validate(new Pet { Name = null });

    var error = Assert.Single(result.Errors);
    Assert.Equal("name", error.Field);
    Assert.Equal(ValidationCodes.Required, error.Code);
}
```

Assert on `Code`, not on `Message`. The code is a wire contract; the message is human-facing text
that may legitimately be reworded.

## Testing that a model is valid

```csharp
[Fact]
public void WellFormedPet_IsValid() {
    Assert.True(new PetValidator().IsValid(ValidPet()));
}
```

A builder that starts from a valid value and breaks one thing keeps each test about one rule:

```csharp
private static Pet ValidPet(Action<PetBuilder>? mutate = null) { … }

[Fact]
public void Name_TooLong_IsRejected() {
    var result = new PetValidator().Validate(ValidPet(p => p.Name = new string('a', 101)));

    Assert.Equal(ValidationCodes.StringLength, Assert.Single(result.Errors).Code);
}
```

The `Assert.Single` matters as much as the code: it catches a second, unintended failure that a
`Assert.Contains` would let through.

## Testing paths

Nested and collection errors are where a path assertion earns its place:

```csharp
[Fact]
public void NestedFailure_IsPathed() {
    var pet = ValidPet(p => p.Home = new Address { PostalCode = null });

    Assert.Equal("home.postalCode", Assert.Single(new PetValidator().Validate(pet).Errors).Field);
}

[Fact]
public void ElementFailure_CarriesItsIndex() {
    var pet = ValidPet(p => p.Toys = [new Toy { Name = "ok" }, new Toy { Name = null }]);

    Assert.Equal("toys[1].name", Assert.Single(new PetValidator().Validate(pet).Errors).Field);
}
```

Remember that paths are [compact](/guide/nesting#field-paths-are-compact). At three or more descents
the middle is elided, so assert what the library actually reports, which is
`body...address.postalCode`, rather than the full ancestry.

## Testing ordering and suppression

Two semantics worth a test of their own, because they are easy to break and quiet when broken:

```csharp
[Fact]
public void FailedRequired_SuppressesTheRestOfItsField() {
    // Name carries [Required] and [StringLength(1, 100)]. A null value fails one of them, not both.
    var result = new PetValidator().Validate(ValidPet(p => p.Name = null));

    Assert.Equal(ValidationCodes.Required, Assert.Single(result.Errors).Code);
}

[Fact]
public void Errors_ArriveInDeclarationOrder() {
    var pet = ValidPet(p => { p.Name = null; p.Age = 99; });

    Assert.Equal(["name", "age"], new PetValidator().Validate(pet).Errors.Select(e => e.Field));
}
```

## Testing async validators

`IAsyncValidatorFor<T>` takes the context by value, so a test constructs a collector and reads it
afterwards:

```csharp
[Fact]
public async Task DuplicateSku_IsReported() {
    var pets = Substitute.For<IPetRepository>();
    pets.ExistsAsync("ABC", Arg.Any<CancellationToken>()).Returns(true);

    var collector = new ValidationErrorCollector();
    var context = new ValidationContext(collector);

    await new PetUniquenessValidator(pets).ValidateAsync(context, new Pet { Sku = "ABC" }, default);

    Assert.Equal("duplicate", Assert.Single(collector.ToResult().Errors).Code);
}
```

To test the composition rather than one validator, construct a `ValidationRunner<T>` directly. It
takes its validators as constructor arguments, so no container is needed:

```csharp
var runner = new ValidationRunner<Pet>(
    [new PetValidator()],
    [new PetUniquenessValidator(pets)]);

var result = await runner.ValidateAsync(pet);
```

That is also how you test the gate: give it a structurally invalid value and assert the async
validator was never called.

## Testing registration

```csharp
[Fact]
public void GeneratedTable_RegistersAValidatorForEveryModel() {
    var provider = new ServiceCollection()
        .AddSampleValidators()
        .BuildServiceProvider();

    Assert.NotNull(provider.GetService<IValidatorFor<Pet>>());
}
```

With DependencyModules, load the emitted module the same way the application does:

```csharp
var provider = new ServiceCollection()
    .AddModule<ValidationModule>()
    .BuildServiceProvider();
```

## Testing the generator itself

If you are extending the generator, or writing your own on
`ValidationModules.SourceGenerator.Impl`, the two shapes that pay off are both in this repository's
own test suite.

**Drive the generator and assert on diagnostics.** A project that fails to build cannot also be a
test project, so the only way to test a diagnostic is to run the generator over a source string:

```csharp
var result = GeneratorHarness.Run("""
    using ValidationModules.Constraints;

    namespace Sample;

    public sealed record Pet {
        [StringLength(1, 10)]
        public int Age { get; init; }
    }
    """);

Assert.Single(result.Diagnostics, d => d.Id == "VM1001");
```

Assert both halves: that it fires on the bad input, *and* that it stays silent on the good one. A
diagnostic that fires on everything passes the first assertion.

**Golden-file the emitted text.** Substring assertions are right for "this constraint produced a
call" and wrong for the thing most likely to go wrong: a change that alters everything slightly.
Emitted source is real output. It lands in a consumer's `obj/`, the trimmer sees it, and its
allocation behaviour is a documented promise, so a whole-file diff is the review surface that
matters.

```csharp
[Fact]
public void FlatModel_EveryConstraintKind() {
    Snapshot.Match(Emit(source));
}
```

Accept an intended change with `UPDATE_SNAPSHOTS=1` and read the diff:

```bash
UPDATE_SNAPSHOTS=1 dotnet test tests/ValidationModules.SourceGenerator.Tests
```

Compile what you emitted, in the same harness. A golden file can then never record something that
does not build.

## Running the suite

```bash
dotnet test --configuration Release
dotnet test --configuration Release --collect:"XPlat Code Coverage" --settings coverlet.runsettings
```

The public API is pinned by a snapshot at
`tests/ValidationModules.Runtime.Tests/Snapshots/PublicApiTests.RuntimeApi.verified.txt`. It is one
file listing every public type and member, and it is the quickest way to read the surface.
