using System.Collections.Immutable;
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
public class GeneratedValidatorTests
{
    [Fact]
    public void Generator_ProducedAValidatorPerAnnotatedType()
    {
        Assert.NotNull(new PetValidator());
        Assert.NotNull(new AddressValidator());
        Assert.NotNull(new ToyValidator());
    }

    [Fact]
    public void Validate_CleanValue_IsValid()
    {
        Assert.True(new PetValidator().IsValid(ValidPet()));
    }

    /// <summary>
    /// A bare sequence is counted through <c>Enumerable.Count</c>: a list or an array by its
    /// count, and a lazy sequence by walking it once.
    /// </summary>
    [Fact]
    public void ItemCount_OnABareSequence_CountsItsItems()
    {
        var validator = new PostValidator();

        Assert.True(validator.Validate(new Post { Tags = ["a", "b"] }).IsValid);
        Assert.True(validator.Validate(new Post { Tags = null }).IsValid);
        Assert.False(validator.Validate(new Post { Tags = [] }).IsValid);
        Assert.False(validator.Validate(new Post { Tags = new[] { "a", "b", "c", "d" } }).IsValid);

        var walks = 0;
        var lazy = Enumerable
            .Range(0, 5)
            .Select(i =>
            {
                walks++;
                return i.ToString();
            });

        var error = Assert.Single(validator.Validate(new Post { Tags = lazy }).Errors);

        Assert.Equal(ValidationCodes.ArrayBounds, error.Code);
        Assert.Equal(5, walks);
        Assert.False(validator.IsValid(new Post { Tags = [] }));
    }

    [Fact]
    public void ItemCount_OnAnImmutableArray_ReadsLength()
    {
        var validator = new PostValidator();

        Assert.True(validator.Validate(new Post { Tags = ["a"], Labels = ["x", "y"] }).IsValid);

        var error = Assert.Single(
            validator.Validate(new Post { Tags = ["a"], Labels = ["w", "x", "y", "z"] }).Errors
        );

        Assert.Equal("labels", error.Field);
        Assert.Equal(ValidationCodes.ArrayBounds, error.Code);
    }

    /// <summary>
    /// A default ImmutableArray has no backing array, so reading its Length, enumerating it or
    /// boxing it as an IReadOnlyList throws. It reads as missing instead, the way null does for a
    /// reference-typed collection, under the attributes and the rules class alike.
    /// </summary>
    [Fact]
    public void DefaultImmutableArrays_ReadAsMissing()
    {
        var validator = new PostValidator();
        var post = new Post
        {
            Tags = ["a"],
            Labels = default,
            Comments = default,
            Keywords = default,
            Replies = default,
        };

        Assert.True(validator.Validate(post).IsValid);
        Assert.True(validator.IsValid(post));
    }

    [Fact]
    public void PresentImmutableArrays_AreStillChecked()
    {
        var result = new PostValidator().Validate(
            new Post
            {
                Tags = ["a"],
                Labels = ["x", "x"],
                Comments = [new Comment { Text = "b" }],
                Keywords = ["", "k", "k"],
                Replies = [new Comment { Text = "b" }],
            }
        );

        Assert.Equal(
            [
                ("comments[0].text", ValidationCodes.Pattern),
                ("keywords", ValidationCodes.UniqueItems),
                ("keywords[0]", ValidationCodes.StringLength),
                ("labels", ValidationCodes.UniqueItems),
                ("replies[0].text", ValidationCodes.Pattern),
            ],
            result
                .Errors.Select(e => (e.Field, e.Code))
                .OrderBy(e => e.Field, StringComparer.Ordinal)
        );
    }

    /// <summary>
    /// [Required] reads a default ImmutableArray as missing, from either namespace and inside a
    /// nullable, and captures no value for it. An empty array is present, as an empty collection
    /// is.
    /// </summary>
    [Fact]
    public void Required_OnAnImmutableArray_FailsOnlyADefaultArray()
    {
        var validator = new PlaylistValidator();
        var missing = new Playlist
        {
            Tracks = default,
            Genres = default,
            Moods = default(ImmutableArray<string>),
        };
        var errors = validator.Validate(missing).Errors;

        Assert.Equal(
            [
                ("genres", ValidationCodes.Required),
                ("moods", ValidationCodes.Required),
                ("tracks", ValidationCodes.Required),
            ],
            errors.Select(e => (e.Field, e.Code)).OrderBy(e => e.Field, StringComparer.Ordinal)
        );
        Assert.All(errors, error => Assert.Null(error.Value));
        Assert.False(validator.IsValid(missing));
        Assert.False(validator.IsValid(new Playlist { Moods = null }));

        var empty = new Playlist();
        var populated = new Playlist
        {
            Tracks = ["a"],
            Genres = ["b"],
            Moods = ImmutableArray.Create("c"),
        };

        Assert.True(validator.Validate(empty).IsValid);
        Assert.True(validator.IsValid(empty));
        Assert.True(validator.Validate(populated).IsValid);
        Assert.True(validator.IsValid(populated));
    }

    /// <summary>
    /// The collection rules read an ImmutableArray held in a nullable through the array it holds.
    /// Null and a default array are missing, so every rule passes them.
    /// </summary>
    [Fact]
    public void CollectionRules_OnANullableImmutableArray_ReadTheArrayItHolds()
    {
        var validator = new SetlistValidator();
        var defaults = new Setlist
        {
            Songs = default(ImmutableArray<string>),
            Openers = default(ImmutableArray<Song>),
            Encores = default(ImmutableArray<string>),
            Covers = default(ImmutableArray<Song>),
        };

        Assert.True(validator.Validate(new Setlist()).IsValid);
        Assert.True(validator.IsValid(new Setlist()));
        Assert.True(validator.Validate(defaults).IsValid);
        Assert.True(validator.IsValid(defaults));

        var invalid = new Setlist
        {
            Songs = ImmutableArray.Create("a", "a", "b", "c"),
            Openers = ImmutableArray.Create(new Song()),
            Encores = ImmutableArray.Create("", "too long", "ok"),
            Covers = ImmutableArray.Create(new Song { Title = "t" }, new Song()),
        };

        Assert.Equal(
            [
                ("covers[1].title", ValidationCodes.Required),
                ("encores", ValidationCodes.ArrayBounds),
                ("encores[0]", ValidationCodes.StringLength),
                ("encores[1]", ValidationCodes.StringLength),
                ("openers[0].title", ValidationCodes.Required),
                ("songs", ValidationCodes.ArrayBounds),
                ("songs", ValidationCodes.UniqueItems),
            ],
            validator
                .Validate(invalid)
                .Errors.Select(e => (e.Field, e.Code))
                .OrderBy(e => e.Field, StringComparer.Ordinal)
                .ThenBy(e => e.Code, StringComparer.Ordinal)
        );
        Assert.False(validator.IsValid(invalid));
    }

    /// <summary>
    /// Require reads a default ImmutableArray as missing and reports it once, as required, so the
    /// rules chained after it skip it. An empty array is present, so Require passes it and a
    /// chained Count still reads it.
    /// </summary>
    [Fact]
    public void Require_OnAnImmutableArray_FailsOnlyADefaultArray()
    {
        var validator = new AlbumValidator();
        var missing = validator.Validate(
            new Album
            {
                Artists = default,
                Credits = default,
                Reviews = default,
            }
        );

        Assert.Equal(
            [
                ("artists", ValidationCodes.Required),
                ("credits", ValidationCodes.Required),
                ("reviews", ValidationCodes.Required),
            ],
            missing
                .Errors.Select(e => (e.Field, e.Code))
                .OrderBy(e => e.Field, StringComparer.Ordinal)
        );

        var empty = Assert.Single(validator.Validate(new Album()).Errors);

        Assert.Equal(("credits", ValidationCodes.ArrayBounds), (empty.Field, empty.Code));
        Assert.True(
            validator
                .Validate(
                    new Album
                    {
                        Artists = ["a"],
                        Credits = ["b"],
                        Reviews = [new Comment { Text = "a" }],
                    }
                )
                .IsValid
        );
    }

    /// <summary>
    /// A null value is rejected where it enters. <c>IsValid</c> reached through the interface binds
    /// straight to the generated method, which is why that method carries a guard of its own.
    /// </summary>
    [Fact]
    public void ANullValue_IsRejectedRatherThanDereferenced()
    {
        IValidatorFor<Pet> validator = new PetValidator();

        Assert.Equal(
            "value",
            Assert.Throws<ArgumentNullException>(() => validator.IsValid(null!)).ParamName
        );
        Assert.Equal(
            "value",
            Assert.Throws<ArgumentNullException>(() => validator.Validate(null!)).ParamName
        );
    }

    [Fact]
    public void Validate_CleanValue_AllocatesNothing()
    {
        // The validator is held, not constructed per call — which is what a container does with a
        // singleton, and what any caller should do. Constructing one per validation allocates the
        // object and, on first descent, the array of nested validators behind it.
        var validator = new PetValidator();
        var collector = new ValidationErrorCollector();
        var pet = ValidPet();

        for (var i = 0; i < 200; i++)
        {
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
        Assert.Equal(
            0,
            BestOf(
                windows: 5,
                () =>
                {
                    for (var i = 0; i < 500; i++)
                    {
                        collector.Reset();
                        validator.ValidateInto(collector, pet);
                    }
                }
            )
        );
    }

    /// <summary>
    /// The smallest number of bytes <paramref name="work"/> allocated across
    /// <paramref name="windows"/> runs.
    /// </summary>
    private static long BestOf(int windows, Action work)
    {
        var best = long.MaxValue;

        for (var window = 0; window < windows; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();

            work();

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (allocated < best)
            {
                best = allocated;
            }

            if (best == 0)
            {
                return 0;
            }
        }

        return best;
    }

    [Fact]
    public void Validate_MissingRequired_UsesTheSharedCodeAndComposedMessage()
    {
        var result = new PetValidator().Validate(ValidPet() with { Name = null });

        var error = Assert.Single(result.Errors, e => e.Field == "name");
        Assert.Equal(ValidationCodes.Required, error.Code);
        Assert.Equal("name is required.", error.Message);
    }

    [Fact]
    public void Validate_FailedRequired_SuppressesTheLengthConstraintOnTheSameField()
    {
        // Name carries both [Required] and [StringLength]. One error, not two.
        var result = new PetValidator().Validate(ValidPet() with { Name = null });

        Assert.Single(result.Errors);
    }

    [Fact]
    public void Validate_Errors_EmitInDeclarationOrder()
    {
        var pet = new Pet
        {
            Home = new Address(),
            Toys = new List<Toy> { new() },
        };

        var result = new PetValidator().Validate(pet);

        Assert.Equal(
            new[] { "name", "home.postal_code", "toys[0].name" },
            result.Errors.Select(error => error.Field)
        );
    }

    [Fact]
    public void Validate_NestedObject_IsPathedThroughItsProperty()
    {
        var result = new PetValidator().Validate(ValidPet() with { Home = new Address() });

        // The field name comes from [JsonPropertyName], which outranks the camel-case default.
        Assert.Equal("home.postal_code", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_CollectionElements_AreIndexed()
    {
        var pet = ValidPet() with
        {
            Toys = new List<Toy>
            {
                new() { Name = "ball" },
                new(),
            },
        };

        var result = new PetValidator().Validate(pet);

        Assert.Equal("toys[1].name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_ItemCount_ReadsTheCollectionRatherThanItsElements()
    {
        var result = new PetValidator().Validate(ValidPet() with { Toys = new List<Toy>() });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.ArrayBounds, error.Code);
        Assert.Equal("toys must be between 1 and 3 items.", error.Message);
    }

    [Fact]
    public void Validate_Pattern_IsUnanchoredForTheNativeAttribute()
    {
        // [Pattern("^[A-Z]{3}$")] - the anchors are the author's, not ours.
        Assert.True(new PetValidator().IsValid(ValidPet() with { Sku = "ABC" }));
        Assert.False(new PetValidator().IsValid(ValidPet() with { Sku = "abc" }));
    }

    [Fact]
    public void Validate_PatternTimeout_FailsThePatternInsteadOfThrowing()
    {
        // The input that exhausts a match timeout is the hostile input the timeout exists for, so
        // it has to come back as a validation failure rather than as an exception.
        var hostile = new Comment { Text = new string('a', 40) + "!" };

        var error = Assert.Single(new CommentValidator().Validate(hostile).Errors);
        Assert.Equal("text", error.Field);
        Assert.Equal(ValidationCodes.Pattern, error.Code);
        Assert.False(new CommentValidator().IsValid(hostile));
    }

    [Fact]
    public void Validate_Range_ProducesTheComposedMessage()
    {
        var result = new PetValidator().Validate(ValidPet() with { Age = 99 });

        Assert.Equal("age must be between 0 and 30.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_AllowedValues_ListsThePermittedSet()
    {
        var result = new PetValidator().Validate(ValidPet() with { Status = "unknown" });

        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.Enum, error.Code);
        Assert.Equal("status must be one of: available, pending, sold.", error.Message);
    }

    [Fact]
    public void Validate_ExplicitMessage_IsEmittedVerbatim()
    {
        var result = new OwnerValidator().Validate(new Owner());

        Assert.Equal("an owner must be named", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_NullableValueType_IsReadThroughItsValue()
    {
        Assert.True(new ReadingValidator().IsValid(new Reading { Ratio = 0.5 }));

        var missing = new ReadingValidator().Validate(new Reading());
        Assert.Equal(ValidationCodes.Required, Assert.Single(missing.Errors).Code);
    }

    [Fact]
    public void Validate_ExclusiveUpperBound_RejectsTheBoundItself()
    {
        Assert.False(new ReadingValidator().IsValid(new Reading { Ratio = 1.0 }));
        Assert.True(new ReadingValidator().IsValid(new Reading { Ratio = 0.999 }));
    }

    [Fact]
    public void GeneratedValidators_RegisterThroughTheGeneratedExtension()
    {
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
            second.ServiceProvider.GetRequiredService<IValidatorFor<Pet>>()
        );
    }

    private static Pet ValidPet() =>
        new()
        {
            Name = "Rex",
            Sku = "ABC",
            Age = 3,
            Status = "available",
            Toys = new List<Toy> { new() { Name = "ball" } },
        };
}
