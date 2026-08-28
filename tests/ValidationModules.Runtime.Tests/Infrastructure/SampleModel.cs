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

    [Required]
    public string? Tag { get; init; }

    [Pattern("^[A-Z]{3}$")]
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

    public ValidationFlow Validate(ref ValidationContext context, Address value) {
        if (string.IsNullOrWhiteSpace(value.PostalCode) &&
            context.Report("postalCode", "required", "postalCode is required.").ShouldStop) {
            return ValidationFlow.Stop;
        }

        if (value.Country is { Length: not 2 } &&
            context.Report("country", "string_length", "country must be exactly 2 characters.").ShouldStop) {
            return ValidationFlow.Stop;
        }

        return ValidationFlow.Continue;
    }
}

/// <summary>Shape of what the generator emits for <see cref="Toy"/>.</summary>
public sealed class ToyValidator : IValidatorFor<Toy> {
    public static readonly ToyValidator Instance = new();

    private ToyValidator() { }

    public ValidationFlow Validate(ref ValidationContext context, Toy value) {
        if (string.IsNullOrWhiteSpace(value.Name) &&
            context.Report("name", "required", "name is required.").ShouldStop) {
            return ValidationFlow.Stop;
        }

        return ValidationFlow.Continue;
    }
}

/// <summary>Shape of what the generator emits for <see cref="Pet"/> under the default profile.</summary>
public sealed class PetValidator : IValidatorFor<Pet> {
    public static readonly PetValidator Instance = new();

    private PetValidator() { }

    public ValidationFlow Validate(ref ValidationContext context, Pet value) {
        // Required suppresses the other constraints on the same field, so a null name produces one
        // error rather than one per rule. The generator emits exactly this else-if shape.
        if (string.IsNullOrWhiteSpace(value.Name)) {
            if (context.Report("name", "required", "name is required.").ShouldStop) {
                return ValidationFlow.Stop;
            }
        } else if (value.Name.Length > 10) {
            if (context.Report("name", "string_length", "name must be at most 10 characters.").ShouldStop) {
                return ValidationFlow.Stop;
            }
        }

        if (value.Home is { } home) {
            var nested = context.Push("home");

            if (AddressValidator.Instance.Validate(ref nested, home).ShouldStop) {
                return ValidationFlow.Stop;
            }
        }

        if (value.Toys.Count < 1 &&
            context.Report("toys", "array_bounds", "toys must contain at least 1 item.").ShouldStop) {
            return ValidationFlow.Stop;
        }

        for (var i = 0; i < value.Toys.Count; i++) {
            var item = context.PushIndex("toys", i);

            if (ToyValidator.Instance.Validate(ref item, value.Toys[i]).ShouldStop) {
                return ValidationFlow.Stop;
            }
        }

        return ValidationFlow.Continue;
    }
}

/// <summary>Shape of what the generator emits for <see cref="Pet"/> under <see cref="V2"/>.</summary>
public sealed class PetValidatorV2 : IValidatorFor<Pet> {
    public static readonly PetValidatorV2 Instance = new();

    private PetValidatorV2() { }

    public ValidationFlow Validate(ref ValidationContext context, Pet value) {
        if (PetValidator.Instance.Validate(ref context, value).ShouldStop) {
            return ValidationFlow.Stop;
        }

        if (value.Tag is null &&
            context.Report("tag", "required", "tag is required.").ShouldStop) {
            return ValidationFlow.Stop;
        }

        return ValidationFlow.Continue;
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
            context.Report("name", "duplicate", "name is already taken.");
        }
    }
}
