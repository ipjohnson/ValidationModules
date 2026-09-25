# Constraint attributes

Constraint attributes declare rules on the properties of a model. They are in the
`ValidationModules.Constraints` namespace, and the generator reads them at build time.

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed class Product
{
    [Required, StringLength(40, Min = 3)]
    public string? Name { get; init; }

    [Range(0.01, 10_000)]
    public decimal Price { get; init; }

    [Url]
    public string? ImageUrl { get; init; }

    [AllowedValues("draft", "active", "retired")]
    public string? Status { get; init; }

    [ItemCount(max: 10), UniqueItems]
    public List<string> Tags { get; init; } = [];

    [ValidateNested]
    public Dimensions? Size { get; init; }
}

public sealed class Dimensions
{
    [Range(1, 500)]
    public int WidthCm { get; init; }
}
```

## The attributes

| Attribute | Applies to | Passes when | Code |
| --- | --- | --- | --- |
| `[Required]` | any property | the value is present | `required` |
| `[StringLength]` | `string` | the length is within the bounds | `string_length` |
| `[Range]` | numbers, dates and times | the value is within the bounds | `range` |
| `[MultipleOf]` | numbers | the value divides by the divisor | `multiple_of` |
| `[Pattern]` | `string` | the value matches a regular expression | `pattern` |
| `[EmailAddress]` | `string` | the value has one `@`, not first or last | `email` |
| `[Phone]` | `string` | the value contains only phone number characters | `phone` |
| `[Url]` | `string`, `Uri` | the value is an http, https or ftp URL | `url` |
| `[CreditCard]` | `string` | the digits pass the Luhn checksum | `credit_card` |
| `[Base64String]` | `string` | the value is valid Base64 | `base64` |
| `[FileExtensions]` | `string` | the file extension is in a list | `file_extension` |
| `[AllowedValues]` | any property | the value is in a list | `enum` |
| `[DeniedValues]` | any property | the value is not in a list | `enum` |
| `[EnumDefined]` | enums | the value is a declared member | `enum` |
| `[ItemCount]` | collections | the number of items is within the bounds | `array_bounds` |
| `[UniqueItems]` | collections | no item appears twice | `unique_items` |
| `[ValidateNested]` | objects, collections, dictionaries | the nested validators pass | from those validators |

`[GenerateValidator]` goes on a type rather than a property. It makes the generator write a
validator for a type that has no constraints of its own. The [attributes
reference](../reference/attributes) gives the details of each attribute, including its messages.

An attribute on a property of the wrong type is a build error. `[StringLength]` on an `int` reports
`VM1001`, and `[Range]` on a `string` reports `VM1003`.

## Null values

Every constraint except `[Required]` passes a `null` value. Add `[Required]` when the value must be
present:

```csharp
[Required, EmailAddress]
public string? Email { get; init; }
```

`[Required]` on a string also rejects the empty string and whitespace. Set `AllowEmptyStrings =
true` to reject only `null`. On a collection, `[Required]` rejects only `null`, so an empty list
passes. Use `[ItemCount(min: 1)]` to require at least one item.

When `[Required]` fails, the other constraints on the same property are skipped. The property then
reports one error. `[Required]` is checked first, whatever the order of the attributes.

`[Required]` on a property whose type is a non-nullable value type, such as `int` or `Guid`, can
never fail. The generator reports `VM1201` and drops it. Make the property nullable, or constrain
its value with `[Range]`.

`ImmutableArray<T>` is the exception. A default `ImmutableArray<T>` has no array behind it, so it
reads as missing, as `null` does. `[Required]` fails on it and passes an empty array. Every other
constraint passes it, and `[ValidateNested]` skips it. On an `ImmutableArray<T>?`, `[Required]`
fails on `null` and on a default array.

## Bounds

`[StringLength]` reads its arguments as the DataAnnotations attribute of the same name does. The
one positional argument is the maximum, and the minimum is the named `Min`:

```csharp
[StringLength(40)]              // at most 40 characters
[StringLength(40, Min = 3)]     // 3 to 40 characters
[StringLength(Min = 3)]         // at least 3 characters
```

`[ItemCount]` takes the minimum first and the maximum second, as the DataAnnotations `[Length]`
does:

```csharp
[ItemCount(1, 10)]      // 1 to 10 items
[ItemCount(max: 10)]    // at most 10 items
[ItemCount(min: 1)]     // at least 1 item
```

::: tip Moving from DataAnnotations
`[StringLength(50)]` means at most 50 characters under either namespace. DataAnnotations names the
minimum `MinimumLength`, so `[StringLength(50, MinimumLength = 2)]` becomes
`[StringLength(50, Min = 2)]`.
:::

`[Range]` takes both bounds, inclusive by default. `ExclusiveMin` and `ExclusiveMax` make either
bound exclusive, and the named `Min` and `Max` properties set one bound alone:

```csharp
[Range(0, 100)]                         // 0 to 100
[Range(0, 100, ExclusiveMax = true)]    // at least 0 and less than 100
[Range(Min = 18)]                       // at least 18
[Range("2024-01-01", "2030-12-31")]     // a DateOnly or DateTime between two dates
```

String bounds are parsed at build time as the property's own type, so they work for `DateTime`,
`DateOnly`, `TimeOnly`, `TimeSpan`, `DateTimeOffset` and `decimal`. A bound that does not parse is
reported as `VM1103`, and bounds that no value satisfies are reported as `VM1101`. The parsed
bounds do not depend on the machine that builds the project. A `DateTimeOffset` bound written
without an offset is read as UTC. A `DateTime` bound written with `Z` or an offset keeps its
instant, converted to UTC, and one written without a zone is compared as written.

## Codes and messages

Every constraint reports a built-in code and a default message. `Code` and `Message` replace them:

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed class Parcel
{
    [Range(
        1,
        1000,
        Code = "weight_out_of_range",
        Message = "Weight must be between 1 and 1000 grams."
    )]
    public int WeightGrams { get; init; }

    [EmailAddress(Message = "{field} must be a work address.")]
    public string? ContactEmail { get; init; }
}
```

`Message` is literal text. The one placeholder is `{field}`, which becomes the property's field
name, here `contactEmail`, or its `[Display(Name)]` label when it has one. Other placeholders, such
as `{0}`, are printed as written. A language
pack does not replace a `Message` set on a built-in attribute or on a `CustomConstraintAttribute`.
The default messages are listed in
[Messages and languages](./messages#default-messages).

The built-in constraint attributes always report `Error` severity. For a warning, use `Ensure`
with `severity:` in a [rules class](./rule-classes#ensure).

## Conditions

`When` applies a constraint only when a condition is true. `Unless` applies it only when the
condition is false. The condition is the name of a member of the model:

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed class Shipment
{
    public bool IsPickup { get; init; }

    public string? Country { get; init; }

    [Required(Unless = nameof(IsPickup))]
    public string? Address { get; init; }

    [Required(When = nameof(NeedsCustoms))]
    public string? CustomsCode { get; init; }

    public bool NeedsCustoms() => Country is not null && Country != "GB";
}
```

The member can be a `bool` property, a parameterless method that returns `bool`, or a static method
that takes the model and returns `bool`. A name that does not exist is reported as `VM1401`, a
member of another shape as `VM1402`, and a constraint that sets both `When` and `Unless` as
`VM1403`. Each condition is evaluated once for each validation. Conditions that involve more than
one member are easier to write in a rules class.

## Where attributes go

The generator reads attributes on instance properties that have a readable getter. It does not read
fields or static properties, and reports a constraint on one as `VM1011`. It does not read indexers
either, and reports a constraint on one as `VM1014`. A constrained property without an accessible
getter is reported as `VM1007`.

On a positional record, an attribute on a parameter applies to the constructor parameter, not to
the property. Add the `property:` target:

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed record Customer(
    [property: Required] string? Name,
    [property: EmailAddress] string? Email
);
```

Without `property:`, the generator reports `VM1008` and the attribute has no effect.

A type inherits the constraints on the properties of its base classes and on the interfaces it
implements. The base class's properties are checked first. An `override` keeps the base property's
constraints and adds its own. A property that hides a base property with `new` replaces the base
property's constraints, whether or not the new property declares any, and the generator reports
`VM1009`. It does not read explicit interface implementations, or base properties the validator
cannot reach, such as `protected` properties or `internal` properties in another assembly.

A generic type cannot carry constraints, because its validator could not be registered without
`MakeGenericType`. The generator reports `VM1010`. Put the constraints on a closed type instead.

## Names shared with DataAnnotations

`Required`, `StringLength`, `Range`, `AllowedValues`, `DeniedValues`, `EmailAddress`, `Phone`,
`Url`, `CreditCard`, `Base64String` and `FileExtensions` exist in both
`ValidationModules.Constraints` and `System.ComponentModel.DataAnnotations`. A file that imports
both namespaces gets error `CS0104` for those names. Import one namespace per file, or alias one of
them. [DataAnnotations](./data-annotations) explains how the generator treats the DataAnnotations
attributes.
