using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ValidationModules;
using ValidationModules.Constraints;

namespace SutProject;

/// <summary>Native constraints, nesting and collections.</summary>
public sealed record Pet
{
    [Required]
    [StringLength(10, Min = 1)]
    public string? Name { get; init; }

    [Pattern("^[A-Z]{3}$")]
    public string? Sku { get; init; }

    /// <summary>
    /// The reference form. The [GeneratedRegex] has to live in consumer source, because source
    /// generators cannot see each other's output - so ours can call it, but could never write it.
    /// </summary>
    [Pattern(typeof(PetPatterns), nameof(PetPatterns.Slug))]
    public string? Slug { get; init; }

    [Range(0, 30)]
    public int Age { get; init; }

    [AllowedValues("available", "pending", "sold")]
    public string? Status { get; init; }

    [ValidateNested]
    public Address? Home { get; init; }

    [ItemCount(min: 1, max: 3)]
    [ValidateNested]
    public IReadOnlyList<Toy> Toys { get; init; } = new List<Toy>();
}

public sealed record Address
{
    [Required]
    [JsonPropertyName("postal_code")]
    public string? PostalCode { get; init; }
}

public sealed record Toy
{
    [Required]
    public string? Name { get; init; }
}

/// <summary>An explicit message, which must emit a literal rather than a composed one.</summary>
public sealed record Owner
{
    [Required(Message = "an owner must be named")]
    public string? Name { get; init; }
}

/// <summary>A nullable value type, and an exclusive bound.</summary>
public sealed record Reading
{
    [Required]
    [Range(0.0, 1.0, ExclusiveMax = true)]
    public double? Ratio { get; init; }
}

/// <summary>Consumer-declared patterns, implemented by the regex source generator.</summary>
public static partial class PetPatterns
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    public static partial Regex Slug();
}

/// <summary>
/// The string-bounds form of <c>[Range]</c>, for the types with no constant form in metadata.
/// </summary>
/// <remarks>
/// Here rather than only in the generator tests because those prove the emitted file compiles, and
/// what matters is that the comparison it compiles to is the right one. A bound parsed into the
/// wrong month would still compile.
/// </remarks>
public sealed record Booking
{
    [Range("2000-01-01", "2100-12-31")]
    public DateOnly Starts { get; init; }

    [Range("0.00", "9.99")]
    public decimal Price { get; init; }

    [Range("00:00:00", "23:59:59")]
    public TimeSpan Window { get; init; }

    [Range("2000-01-01", "2100-01-01", ExclusiveMax = true)]
    public DateTime Effective { get; init; }
}

/// <summary>
/// <c>[MultipleOf]</c> across every numeric shape it accepts, and <c>[UniqueItems]</c>.
/// </summary>
/// <remarks>
/// The floating-point members are the interesting ones. They are checked in the decimal domain
/// rather than with <c>%</c>, because <c>0.3 % 0.01</c> is 0.00999999999999998 in binary floating
/// point - so a naive check rejects almost every price a specification would call valid. Running
/// the emitted comparison is the only way to see that, which is why these are here rather than
/// only in the generator tests.
/// </remarks>
public sealed record Order
{
    [MultipleOf(5)]
    public int Quantity { get; init; }

    [MultipleOf(100)]
    public long Cents { get; init; }

    [MultipleOf("0.05")]
    public decimal Price { get; init; }

    [MultipleOf(0.01)]
    public double Ratio { get; init; }

    [MultipleOf(25)]
    public int? Optional { get; init; }

    [UniqueItems]
    public List<string> Codes { get; init; } = new();

    [UniqueItems]
    public int[] Sizes { get; init; } = Array.Empty<int>();
}

/// <summary>
/// A <c>[Range]</c> with one bound, and a fractional bound written as a numeric literal against a
/// <c>decimal</c>.
/// </summary>
/// <remarks>
/// Both are regressions rather than features. An absent bound used to be emitted as the type's
/// extreme, which reached the caller as "must be between 1 and 7.9228162514264338E+28"; and
/// <c>[Range(0.5, 9.99)]</c> on a decimal emitted <c>price &lt; 0.5</c>, which is CS0019 - an error
/// inside generated code.
/// </remarks>
public sealed record Allocation
{
    [Range(Min = 1)]
    public int AtLeastOne { get; init; }

    [Range(Max = 99)]
    public int AtMostNinetyNine { get; init; }

    [Range(0.5, 9.99)]
    public decimal Fractional { get; init; }
}

/// <summary>
/// One bound at a time. [StringLength(20)] is a maximum, as it is in DataAnnotations. A minimum on
/// its own is named, and [ItemCount(max: 2)] names the one bound its constructor gives.
/// </summary>
public sealed record Passphrase
{
    [StringLength(Min = 12)]
    public string? Value { get; init; }

    [StringLength(20)]
    public string? Label { get; init; }

    [ItemCount(max: 2)]
    public List<string> Hints { get; init; } = [];
}

/// <summary>
/// [ItemCount] on types with no public Count: a bare sequence, and an ImmutableArray, which
/// implements Count only explicitly. A default ImmutableArray has no backing array, and every rule
/// on one, from either surface, reads it as missing.
/// </summary>
public sealed record Post
{
    [ItemCount(1, 3)]
    public IEnumerable<string>? Tags { get; init; }

    [ItemCount(max: 3)]
    [UniqueItems]
    public ImmutableArray<string> Labels { get; init; } = [];

    [ValidateNested]
    public ImmutableArray<Comment> Comments { get; init; } = [];

    public ImmutableArray<string> Keywords { get; init; } = [];

    public ImmutableArray<Comment> Replies { get; init; } = [];
}

public sealed class PostRules : IValidationRulesFor<Post>
{
    public static void Describe(ValidationRules<Post> rules, Post x)
    {
        rules.Count(x.Keywords, max: 3).Each().Length(1, 20);
        rules.Unique(x.Keywords);
        rules.Each(x.Replies);
    }
}

/// <summary>
/// [Required] on an ImmutableArray, from either namespace and inside a nullable. A default array
/// is missing, and an empty one is present, as an empty collection is.
/// </summary>
public sealed record Playlist
{
    [Required]
    public ImmutableArray<string> Tracks { get; init; } = [];

    [System.ComponentModel.DataAnnotations.Required]
    public ImmutableArray<string> Genres { get; init; } = [];

    [Required]
    public ImmutableArray<string>? Moods { get; init; } = ImmutableArray<string>.Empty;
}

/// <summary>
/// Require on an ImmutableArray, alone and with rules chained after it. A default array is
/// reported once, as required, and the chained rules skip it.
/// </summary>
public sealed record Album
{
    public ImmutableArray<string> Artists { get; init; } = [];

    public ImmutableArray<string> Credits { get; init; } = [];

    public ImmutableArray<Song> Reviews { get; init; } = [];
}

public sealed class AlbumRules : IValidationRulesFor<Album>
{
    public static void Describe(ValidationRules<Album> rules, Album x)
    {
        rules.Require(x.Artists);
        rules.Require(x.Credits).Count(1, 5);
        rules.Require(x.Reviews).Each();
    }
}

/// <summary>
/// The collection rules on an ImmutableArray held in a nullable, from attributes and from a rules
/// class. Null and a default array are missing, so both pass. A populated array is counted,
/// checked and walked through the array it holds.
/// </summary>
public sealed record Setlist
{
    [ItemCount(1, 3)]
    [UniqueItems]
    public ImmutableArray<string>? Songs { get; init; }

    [ValidateNested]
    public ImmutableArray<Song>? Openers { get; init; }

    public ImmutableArray<string>? Encores { get; init; }

    public ImmutableArray<Song>? Covers { get; init; }
}

public sealed record Song
{
    [Required]
    public string? Title { get; init; }
}

public sealed class SetlistRules : IValidationRulesFor<Setlist>
{
    public static void Describe(ValidationRules<Setlist> rules, Setlist x)
    {
        rules.Count<string>(x.Encores, 0, 2);
        rules.Unique<string>(x.Encores);
        rules.Each(x.Encores).Length(1, 5);
        rules.Each<Song>(x.Covers);
    }
}

/// <summary>
/// A pattern that backtracks catastrophically, under a timeout the hostile input runs past. The
/// timeout has to fail the pattern rather than throw out of Validate.
/// </summary>
public sealed record Comment
{
    [Pattern("^(a+)+$", MatchTimeoutMilliseconds = 1)]
    public string? Text { get; init; }
}
