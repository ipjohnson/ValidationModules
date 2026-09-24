# Validation codes

Every error has a `Code`. The built-in codes are constants on `ValidationCodes`, and they are part
of the public API, so application logic and translations can depend on them.

## Built-in codes

| Constant | Value | Reported by |
| --- | --- | --- |
| `ValidationCodes.Required` | `required` | `[Required]`, `Require`, `RequireAllowingEmpty` |
| `ValidationCodes.StringLength` | `string_length` | `[StringLength]`, `Length`, and `[MinLength]`, `[MaxLength]` and `[Length]` on strings |
| `ValidationCodes.ArrayBounds` | `array_bounds` | `[ItemCount]`, `Count`, and `[MinLength]`, `[MaxLength]` and `[Length]` on collections |
| `ValidationCodes.Range` | `range` | `[Range]`, `Range`, `RangeAtLeast`, `RangeAtMost` |
| `ValidationCodes.MultipleOf` | `multiple_of` | `[MultipleOf]`, `MultipleOf` |
| `ValidationCodes.Pattern` | `pattern` | `[Pattern]`, `Pattern`, `[RegularExpression]` |
| `ValidationCodes.Enum` | `enum` | `[AllowedValues]`, `[DeniedValues]`, `[EnumDefined]`, `AllowedValues` |
| `ValidationCodes.UniqueItems` | `unique_items` | `[UniqueItems]`, `Unique` |
| `ValidationCodes.Email` | `email` | `[EmailAddress]` |
| `ValidationCodes.Phone` | `phone` | `[Phone]` |
| `ValidationCodes.Url` | `url` | `[Url]` |
| `ValidationCodes.CreditCard` | `credit_card` | `[CreditCard]` |
| `ValidationCodes.Base64` | `base64` | `[Base64String]` |
| `ValidationCodes.FileExtension` | `file_extension` | `[FileExtensions]` |
| `ValidationCodes.Custom` | `custom` | A `CustomConstraintAttribute` or `IConstraintFor<T>` attribute without its own code, a custom DataAnnotations `ValidationAttribute`, `[CustomValidation]`, and `IValidatableObject` |
| `ValidationCodes.Predicate` | `predicate` | An `Ensure` whose condition yields no code of its own |
| `ValidationCodes.Invalid` | `invalid` | Nothing in this library. It is reserved for a value that could not be converted to the property's type, such as text in a number field, when a request binder reports it. |

The same attributes in `ValidationModules.Constraints` and in
`System.ComponentModel.DataAnnotations` report the same codes. A `Report` helper, such as
`ReportRange`, reports the code of its check unless it is given a `code:` argument.

## Codes from Ensure

An `Ensure` in a rules class without `code:` gets a code derived from its condition. The members
are written in snake_case and the operators as words:

| Condition | Code |
| --- | --- |
| `x.Start < x.End` | `start_less_than_end` |
| `x.End > x.Start` | `end_greater_than_start` |
| `x.Age >= 18` | `age_greater_than_or_equal_18` |
| `x.Paid && x.Shipped` | `paid_and_shipped` |
| `!x.Cancelled` | `not_cancelled` |
| `x.Name != null` | `name_is_not_null` |
| `!string.IsNullOrEmpty(x.Name)` | `name_is_not_null_or_empty` |
| `x.Items.Count > 0` | `items_is_not_empty` |
| `x.Status == "active"` | `status_equal_active` |
| `x.Total - x.Paid > 0` | `total_minus_paid_greater_than_0` |

Changing the condition changes the code. The generator reports each derived code as `VM3103`, an
informational diagnostic. Pass `code:` to fix the code when clients depend on it. When nothing can
be derived, the code is `predicate`.

## Your own codes

`Code` on a constraint attribute, `code:` on `Ensure`, and the `code` argument of a hand-written
`Report` call set your own codes. Language packs can translate them like built-in codes.

`ValidationModules_CodeNamespace` adds a prefix to every code you set and every code derived from an
`Ensure`:

```xml
<PropertyGroup>
  <ValidationModules_CodeNamespace>shop</ValidationModules_CodeNamespace>
</PropertyGroup>
```

With this setting, `code: "stay_order"` reports `shop.stay_order`. Built-in codes are never
prefixed.
