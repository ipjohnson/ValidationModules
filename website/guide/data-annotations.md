# DataAnnotations

`System.ComponentModel.DataAnnotations` attributes are read as a second vocabulary and compiled into
the same validators. A model that already carries them needs no edits:

```csharp
using System.ComponentModel.DataAnnotations;

public class Customer {
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; set; }

    [Range(0, 120)]
    public int Age { get; set; }

    [RegularExpression("^[A-Z]{3}$")]
    public string? Sku { get; set; }
}
```

That produces the same `CustomerValidator` a native-attribute model would, with the same codes,
messages and field paths. The origin of a rule stops mattering the moment it is read.

**No `ValidationAttribute` is ever constructed and no `IsValid` is ever called.** The arguments are
read out of metadata at build time and compiled, which is what keeps this free of the reflection
`Validator.TryValidateObject` would otherwise do.

This is on by default. Set `ValidationModules_DataAnnotations` to `Ignore` to turn it off.

## What is compiled

| DataAnnotations | Becomes | Code |
|---|---|---|
| `[Required]` | `[Required]` | `required` |
| `[StringLength(max, MinimumLength = min)]` | `[StringLength]` | `string_length` |
| `[MinLength]` / `[MaxLength]` / `[Length]` | `[StringLength]` **or** `[ItemCount]` | depends |
| `[Range(min, max)]` | `[Range]` | `range` |
| `[RegularExpression]` | `[Pattern]`, **anchored** | `pattern` |
| `[AllowedValues]` | `[AllowedValues]` | `enum` |
| `[DeniedValues]` | `[AllowedValues]`, negated | `enum` |

`[MinLength]`, `[MaxLength]` and `[Length]` apply to strings *and* collections in DataAnnotations, so
the member's own type decides which constraint each becomes. A member that is neither is
[VM0064](/reference/diagnostics#vm0064).

`MinimumIsExclusive` / `MaximumIsExclusive` on `[Range]` are honoured, as is `ErrorMessage`
everywhere.

## Two behaviours reproduced on purpose

**`[Required]` treats whitespace as missing.** DataAnnotations trims before testing, and the
compiled form matches. `AllowEmptyStrings = true` opts out of both.

**`[RegularExpression]` is anchored.** DataAnnotations checks that the match starts at 0 and consumes
the whole value. The native `[Pattern]` follows JSON Schema and does *not*. Both are reproduced
faithfully rather than unified, because quietly changing what a model means when you move it is worse
than the inconsistency.

## What is not compiled, and says so

Silence would be dangerous here in a way it is not for native attributes: a `[EmailAddress]` that
this generator skips still *looks* enforced, because you have every reason to think
`TryValidateObject` would have honoured it. So everything recognised and not compiled is reported.

| Attribute | Diagnostic | Why |
|---|---|---|
| `[EmailAddress]`, `[Phone]`, `[Url]`, `[CreditCard]`, `[Base64String]`, `[FileExtensions]` | [VM0063](/reference/diagnostics#vm0063) | see below |
| `[Compare]` | [VM0061](/reference/diagnostics#vm0061) | compares two members |
| `[CustomValidation]`, any `ValidationAttribute` subclass | [VM0060](/reference/diagnostics#vm0060) | carries arbitrary code |
| `IValidatableObject` | [VM0067](/reference/diagnostics#vm0067) | `Validate` is not called |

### The format validators

`[EmailAddress]` and friends are skipped rather than approximated, and the reason is worth stating:
DataAnnotations' `EmailAddressAttribute` accepts anything with exactly one `@` that is not at either
end. `a@b` passes. That is far more lenient than almost anyone declaring `[EmailAddress]` believes
they asked for.

Reproducing it faithfully would ship a surprise; reproducing it *strictly* would change the meaning
of an existing model. So it reports VM0063 and asks you to declare a `[Pattern]` whose behaviour is
visible in your own source.

### Cross-field and custom rules

`[Compare]` and custom `ValidationAttribute` subclasses have no per-property form. Move them to a
[rule class](/guide/rule-classes) — which is the declaration form that *can* express a rule spanning
two properties:

```csharp
public sealed class CustomerRules : IValidationRulesFor<Customer> {
    public void Describe(ValidationRules<Customer> rules) {
        rules.Ensure(x => x.Password == x.Confirm, code: "password_mismatch");
    }
}
```

Or into an [`IAsyncValidatorFor<T>`](/guide/async) if the rule needs I/O.

### `IValidatableObject`

Its `Validate` method is not called by the generated validator. The type is still validated for
everything else it declares — dropping it entirely would be a worse answer than validating what can
be validated and saying what was left out.

Call it yourself from an async validator if you need it, or move the rule.

## Turning it off

```xml
<PropertyGroup>
    <ValidationModules_DataAnnotations>Ignore</ValidationModules_DataAnnotations>
</PropertyGroup>
```

Every skipped constraint is then [VM0010](/reference/diagnostics#vm0010), once per constraint — so
turning it off does not silently unvalidate a model. A type whose only rules were DataAnnotations
gets no validator at all.

The switch governs one vocabulary. A type carrying both keeps its native constraints.

## Mixing the two vocabularies

You can, but you will need to disambiguate:

```csharp
using System.ComponentModel.DataAnnotations;
using ValidationModules.Constraints;   // error CS0104: 'Required' is an ambiguous reference
```

Five names collide: `Required`, `StringLength`, `Range`, `AllowedValues`, and the length family.
Alias one of them, or qualify:

```csharp
[System.ComponentModel.DataAnnotations.Required]
public string? Legacy { get; set; }

[ValidationModules.Constraints.Required]
public string? Current { get; set; }
```

In practice most models want one or the other. The DataAnnotations front end exists so that moving
to this library does not require rewriting your models first — not so that you have to mix them.
