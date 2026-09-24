# DataAnnotations

The generator compiles the attributes from `System.ComponentModel.DataAnnotations` into the same
validators it writes for its own attributes. A model that already uses DataAnnotations gets a
generated validator without any changes.

## Existing models

<!-- verify:bare -->
```csharp
using System.ComponentModel.DataAnnotations;

public sealed class Customer
{
    [Required]
    [StringLength(20, MinimumLength = 2)]
    public string? Name { get; set; }

    [RegularExpression("[A-Z]{3}")]
    public string? Code { get; set; }

    [Range(1, 120)]
    public int Age { get; set; }

    [MaxLength(3)]
    public List<string> Tags { get; set; } = new();
}
```

With both packages installed, this model gets a `CustomerValidator` like any other. A customer named
`a`, with the code `xABCx`, age `0` and four tags, reports:

| `Field` | `Code` | `Message` |
| --- | --- | --- |
| `name` | `string_length` | name must be between 2 and 20 characters. |
| `code` | `pattern` | code is not in the required format. |
| `age` | `range` | age must be between 1 and 120. |
| `tags` | `array_bounds` | tags must be at most 3 items. |

The attributes are not created or called at run time. The generator reads their arguments and
writes the same checks it writes for the attributes in `ValidationModules.Constraints`.

## What each attribute becomes

| DataAnnotations attribute | Checks | Code |
| --- | --- | --- |
| `[Required]` | The value is present. A string must not be empty or whitespace unless `AllowEmptyStrings` is set. | `required` |
| `[StringLength]` | The string length, with `MinimumLength`. | `string_length` |
| `[MinLength]`, `[MaxLength]`, `[Length]` | The length of a string, or the number of items in a collection. | `string_length` or `array_bounds` |
| `[Range]` | The bounds, with `MinimumIsExclusive` and `MaximumIsExclusive`. String bounds are parsed as the property's type at build time. | `range` |
| `[RegularExpression]` | The whole value matches the expression. | `pattern` |
| `[AllowedValues]`, `[DeniedValues]` | The value is in, or not in, the list. | `enum` |
| `[EmailAddress]`, `[Phone]`, `[Url]`, `[CreditCard]`, `[Base64String]`, `[FileExtensions]` | The same rules as the DataAnnotations attributes. | `email`, `phone`, `url`, `credit_card`, `base64`, `file_extension` |
| `[CustomValidation]` on a property | Calls the named static method directly. | `custom` |
| A class derived from `ValidationAttribute` | Creates the attribute once and calls it. | `custom` |
| `IValidatableObject` on the model | Calls `Validate` after every other rule, when nothing has been reported. | `custom` |

The generator reports what it does with some of these as informational diagnostics. `VM2004` states
the exact rule a format attribute applies, `VM2002` notes that a custom `ValidationAttribute` runs
its own code, and `VM2006` notes when `IValidatableObject.Validate` is called.

These attributes are not compiled, and the generator reports a warning:

| Attribute | Diagnostic | Instead |
| --- | --- | --- |
| `[Compare]` | `VM2003` | `Ensure(x.Confirm == x.Password)` in a [rules class](./rule-classes#ensure) |
| `[EnumDataType]` | `VM2007` | Type the property as the enum and use `[EnumDefined]` |

`[Display(Name = ...)]` is not a check. It supplies `{0}` in an `ErrorMessage`, and it also
replaces the error's field name, so `[Display(Name = "Postal code")]` reports the field
`Postal code`. Through a runner, the name can be re-cased. See the warning under
[Field names](./errors#field-names).

## Messages

A built-in attribute without `ErrorMessage` reports this library's code and default message, not the
DataAnnotations default text.

On a built-in attribute, an `ErrorMessage` is filled in at build time. `{0}` is the display name,
which is the
`[Display(Name)]` value or the property name. `{1}` and `{2}` are the attribute's arguments, in the
order DataAnnotations uses:

```csharp
[Display(Name = "nickname")]
[StringLength(10, ErrorMessage = "The {0} must be at most {1} characters.")]
public string? Nickname { get; set; }
```

This reports `The nickname must be at most 10 characters.` The code stays `string_length`, and the
text is authored, so language packs leave it unchanged.

`ErrorMessageResourceType` with `ErrorMessageResourceName` on a built-in attribute reads the
resource property each time the message is rendered, without reflection. On a custom
`ValidationAttribute`, DataAnnotations resolves the resource with reflection, which trimming can
break. The generator reports that case as `VM2009`.

## Differences from Validator

The generated validator behaves like `Validator.TryValidateObject` with `validateAllProperties:
true`, with these differences:

- Every error has a code, and the default messages are this library's.
- Attributes on fields and on the class itself are not read. Neither are `[MetadataType]` classes.
- Attributes declared on an interface's properties apply to the classes that implement it.
- `IValidatableObject.Validate` runs only when the whole validation pass has reported nothing so
  far, warnings included, and including other objects of the same graph.
- `[RegularExpression]` rejects an empty string unless the expression matches it. DataAnnotations
  accepts an empty string for this attribute.
- `[Range]` with its bounds in the wrong order is accepted at build time and always fails.
- A custom `ValidationAttribute` that calls `ValidationContext.GetService` gets the pass's services
  only when the pass has a service provider, as it does through `ValidationRunner<T>`.

## Names shared by both namespaces

`Required`, `StringLength`, `Range`, `AllowedValues`, `DeniedValues`, `EmailAddress`, `Phone`,
`Url`, `CreditCard`, `Base64String` and `FileExtensions` exist in both
`ValidationModules.Constraints` and `System.ComponentModel.DataAnnotations`. `ValidationContext` and
`ValidationResult` exist in both `ValidationModules` and `System.ComponentModel.DataAnnotations`. A
file that imports both namespaces gets error `CS0104` for any of these names.

Import one namespace per model file. Where a file needs both, alias one of them:

<!-- verify:bare -->
```csharp
using ValidationModules.Constraints;
using DataAnnotations = System.ComponentModel.DataAnnotations;

public sealed class Account
{
    [Required, StringLength(3, 20)]
    public string? Handle { get; init; }

    [DataAnnotations.EmailAddress]
    public string? Email { get; init; }
}
```

The two kinds of attribute can be mixed on one model, and on one property.

## Moving a model to the ValidationModules attributes

The generator compiles a DataAnnotations model as it is, so there is no need to convert one. When
you do change a file's `using` from `System.ComponentModel.DataAnnotations` to
`ValidationModules.Constraints`, most attributes keep their meaning. These do not:

| DataAnnotations | ValidationModules |
| --- | --- |
| `[StringLength(50)]` | `[StringLength(max: 50)]`. The first argument here is the minimum. |
| `[StringLength(50, MinimumLength = 2)]` | `[StringLength(2, 50)]` |
| `[MinLength]`, `[MaxLength]`, `[Length]` | `[StringLength]` on a string, `[ItemCount]` on a collection |
| `[RegularExpression("x")]` | `[Pattern(@"\A(?:x)\z")]`. `[Pattern]` matches anywhere in the value unless the expression is anchored. |
| `ErrorMessage = "The {0} field is invalid."` | `Message = "The {field} field is invalid."` |
| `[EnumDataType(typeof(Tier))]` | `[EnumDefined]` on a property of type `Tier` |
| `[Compare]`, `IValidatableObject` | A rules class |
| A custom `ValidationAttribute` | A `CustomConstraintAttribute` or an `IConstraintFor<T>` attribute. One class can also serve both. See [Custom constraints](./custom-constraints#a-check-with-state). |

## Turn the DataAnnotations support off

When another system already enforces the DataAnnotations attributes, such as MVC model validation,
tell the generator to ignore them:

```xml
<PropertyGroup>
  <ValidationModules_DataAnnotations>Ignore</ValidationModules_DataAnnotations>
</PropertyGroup>
```

The generator then reports `VM2001`, an informational diagnostic, for each attribute it ignores. The
`ValidationModules.Constraints` attributes are compiled as usual. A type whose only rules are
DataAnnotations attributes gets no validator at all, so `IValidatorFor<T>` no longer resolves for
it.

## Native AOT

The built-in DataAnnotations attributes are compiled into plain checks and need no reflection.
`[RegularExpression]` is always compiled as an inline regular expression, which adds the regular
expression interpreter to a Native AOT binary. It is not subject to
`ValidationModules_PatternPolicy`. In an AOT application, prefer `[Pattern]` with a
`[GeneratedRegex]` member. See [Patterns](./patterns).
