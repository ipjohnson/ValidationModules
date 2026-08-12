using ValidationModules;
using ValidationModules.Constraints;

namespace ValidationModules.Runtime.Tests.Infrastructure;

/// <summary>
/// The model the runtime tests validate, and the hand-written validators that stand in for
/// generated ones until Stage 2.
/// </summary>
/// <remarks>
/// The validators below are written the way the emitter is specified to write them - static
/// singletons, no constructor, nested validators referenced statically, a Required failure
/// suppressing the rest of its field through an else-if. That is deliberate: these tests pin the
/// semantics the generator will have to reproduce, so they double as the emitter's spec.
/// </remarks>
public sealed class V1 : IValidationProfile;

public sealed class V2 : IValidationProfile<V1>;

public sealed class Strict : IValidationProfile;

public sealed record Address {
    [Required] public string? PostalCode { get; init; }

    [StringLength(min: 2, max: 2)] public string? Country { get; init; }
}

public sealed record Toy {
    [Required] public string? Name { get; init; }
}

public sealed record Pet {
    [Required]
    [StringLength(min: 1, max: 10)]
    public string? Name { get; init; }

    [Required(FromProfile = typeof(V2))]
    public string? Tag { get; init; }

    [Pattern("^[A-Z]{3}$", Profiles = [typeof(Strict)])]
    public string? Sku { get; init; }

    [ValidateNested]
    public Address? Home { get; init; }

    [ItemCount(min: 1, max: 3)]
    [ValidateNested]
    public IReadOnlyList<Toy> Toys { get; init; } = [];
}

/// <summary>Shape of what the generator emits for <see cref="Address"/>.</summary>
public sealed class AddressValidator : IValidatorFor<Address> {
    public static readonly AddressValidator Instance = new();

    private AddressValidator() { }

    public void Validate(ref ValidationContext context, Address value) {
        if (string.IsNullOrWhiteSpace(value.PostalCode)) {
            context.Add("postalCode", "required", "postalCode is required.");
        }

        if (value.Country is { Length: not 2 }) {
            context.Add("country", "string_length", "country must be exactly 2 characters.");
        }
    }
}

/// <summary>Shape of what the generator emits for <see cref="Toy"/>.</summary>
public sealed class ToyValidator : IValidatorFor<Toy> {
    public static readonly ToyValidator Instance = new();

    private ToyValidator() { }

    public void Validate(ref ValidationContext context, Toy value) {
        if (string.IsNullOrWhiteSpace(value.Name)) {
            context.Add("name", "required", "name is required.");
        }
    }
}

/// <summary>Shape of what the generator emits for <see cref="Pet"/> under the default profile.</summary>
public sealed class PetValidator : IValidatorFor<Pet> {
    public static readonly PetValidator Instance = new();

    private PetValidator() { }

    public void Validate(ref ValidationContext context, Pet value) {
        // Required suppresses the other constraints on the same field, so a null name produces one
        // error rather than one per rule. The generator emits exactly this else-if shape.
        if (string.IsNullOrWhiteSpace(value.Name)) {
            context.Add("name", "required", "name is required.");
        } else if (value.Name.Length > 10) {
            context.Add("name", "string_length", "name must be at most 10 characters.");
        }

        if (value.Home is { } home) {
            var nested = context.Push("home");

            AddressValidator.Instance.Validate(ref nested, home);
        }

        if (value.Toys.Count < 1) {
            context.Add("toys", "array_bounds", "toys must contain at least 1 item.");
        }

        for (var i = 0; i < value.Toys.Count; i++) {
            var item = context.PushIndex("toys", i);

            ToyValidator.Instance.Validate(ref item, value.Toys[i]);
        }
    }
}

/// <summary>Shape of what the generator emits for <see cref="Pet"/> under <see cref="V2"/>.</summary>
public sealed class PetValidatorV2 : IValidatorFor<Pet> {
    public static readonly PetValidatorV2 Instance = new();

    private PetValidatorV2() { }

    public void Validate(ref ValidationContext context, Pet value) {
        PetValidator.Instance.Validate(ref context, value);

        if (value.Tag is null) {
            context.Add("tag", "required", "tag is required.");
        }
    }
}

/// <summary>A hand-written business rule, standing in for one that would hit a database.</summary>
public sealed class PetNameUniquenessValidator : IAsyncValidatorFor<Pet> {
    private readonly HashSet<string> _taken;

    public PetNameUniquenessValidator(params string[] taken) {
        _taken = new HashSet<string>(taken, StringComparer.Ordinal);
    }

    public async ValueTask ValidateAsync(ValidationContext context, Pet value, CancellationToken cancellationToken) {
        await Task.Yield();

        if (value.Name is not null && _taken.Contains(value.Name)) {
            context.Add("name", "duplicate", "name is already taken.");
        }
    }
}
