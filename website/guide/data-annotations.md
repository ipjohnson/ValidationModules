# DataAnnotations

`System.ComponentModel.DataAnnotations` attributes are read as a second vocabulary and compiled into
the same validators. A model that already carries them needs no edits:

<!-- verify:bare -->
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
| `[EmailAddress]` | its compiled check — [see below](#the-format-validators) | `email` |
| `[Phone]` | its compiled check | `phone` |
| `[Url]` | its compiled check | `url` |
| `[CreditCard]` | its compiled check | `credit_card` |
| `[Base64String]` | its compiled check | `base64` |
| `[FileExtensions]` | its compiled check | `file_extension` |

`[MinLength]`, `[MaxLength]` and `[Length]` apply to strings *and* collections in DataAnnotations, so
the member's own type decides which constraint each becomes. A member that is neither is
[VM0064](/reference/diagnostics#vm0064).

`MinimumIsExclusive` / `MaximumIsExclusive` on `[Range]` are honoured — and the message finally
says so: an exclusive bound reads "must be greater than" / "must be less than" rather than
claiming "between".

### Messages {#messages}

`ErrorMessage` is honoured everywhere, with DataAnnotations' own placeholder dialect resolved at
build time: every argument but the display name is a compile-time constant, so
`ErrorMessage = "The field {0} is over {1} chars"` on a `[StringLength(3)]` compiles to the
finished text, `{0}` filled with the `[Display]` name the front end already resolved. Where
`Validator.TryValidateObject` called `string.Format` per failure, the wire carries a literal.

Resource-backed messages — `ErrorMessageResourceType` / `ErrorMessageResourceName` — compile to a
direct read of the resource accessor property, performed per render, so `CurrentUICulture` and
the satellite fallback chain do their work and nothing resolves reflectively: the property
reference roots the resource class for the trimmer. An explicit `ErrorMessage` beside the pair
wins, which is DataAnnotations' own precedence.

## Two behaviours reproduced on purpose

**`[Required]` treats whitespace as missing.** DataAnnotations trims before testing, and the
compiled form matches. `AllowEmptyStrings = true` opts out of both.

**`[RegularExpression]` is anchored.** DataAnnotations checks that the match starts at 0 and consumes
the whole value. The native `[Pattern]` follows JSON Schema and does *not*. Both are reproduced
faithfully rather than unified, because quietly changing what a model means when you move it is worse
than the inconsistency.

## The format validators

`[EmailAddress]`, `[Phone]`, `[Url]`, `[CreditCard]`, `[Base64String]` and `[FileExtensions]`
compile to the BCL's own checks, reproduced exactly — no attribute is constructed, nothing
allocates on the pass, and the answer is the one `Validator.TryValidateObject` gives:

| Attribute | The compiled check |
|---|---|
| `[EmailAddress]` | exactly one `@`, neither first nor last, and no line breaks — **`a@b` passes** |
| `[Phone]` | `+` signs stripped and a trailing extension (`ext.`, `ext`, `x` plus digits) removed; at least one digit; only digits, whitespace and `-.()` |
| `[Url]` | starts with `http://`, `https://` or `ftp://`, case-insensitively — nothing past the prefix |
| `[CreditCard]` | digits, with spaces and dashes skipped, passing the Luhn mod-10 checksum |
| `[Base64String]` | well-formed Base64, as `Convert.FromBase64String` reads it |
| `[FileExtensions]` | the file name's extension is in the set — default `png,jpg,jpeg,gif`, case-insensitive |

These semantics are looser than the attribute names suggest, and that is Microsoft's position, held
deliberately: every request to tighten `[EmailAddress]` has been closed as by-design, the regex
implementations were removed for denial-of-service reasons years ago, and RFC 5322 genuinely does
permit `a@b` — a dotless domain is a valid address, which is why `root@localhost` delivers. A model
migrating from `TryValidateObject`, MVC model validation, or .NET 10's `AddValidation()` keeps
exactly the checks it had.

Because the semantics are worth knowing, each use reports
[VM0063](/reference/diagnostics#vm0063) — an **Info**, not a warning — stating the compiled check
verbatim at the property that declared it. Want something stricter? Declare a
[`[Pattern]`](/guide/patterns) whose behaviour is written in your own source; the diagnostic says
so too.

Two footnotes. `[Url]` also accepts a `System.Uri` member — absolute, scheme http/https/ftp — which
is the current BCL behaviour; net8's own `UrlAttribute` predates that branch and rejects every
`Uri`, and one semantics is emitted for both target frameworks. And `[CreditCard]` passes the empty
string, exactly as the attribute does — the checksum of nothing is zero — so `[Required]` remains
the presence check.

## Custom rules are invoked

The three DataAnnotations surfaces that carry *user code* — custom `ValidationAttribute`
subclasses, `[CustomValidation]` methods, and `IValidatableObject` — run, with DataAnnotations'
own semantics, because the only faithful reading of user code is to run it. Nothing reflects to
make that happen:

- **A custom attribute** is constructed once, at validator construction, from its
  compile-time-constant arguments — `new EvenNumberAttribute(2) { ErrorMessage = "…" }` lands in
  the generated file as exactly that — and invoked through `GetValidationResult`, the same call
  `Validator.TryValidateObject` makes, minus the discovery. Each use reports
  [VM0060](/reference/diagnostics#vm0060) as an Info carrying the cost model.
- **`[CustomValidation(typeof(T), "Method")]`** is resolved at build time and emitted as a direct
  static call — DataAnnotations resolves the method by name reflectively on every validation. A
  target that cannot be called is [VM0080](/reference/diagnostics#vm0080) at build time, not a
  rule that silently never runs.
- **`IValidatableObject.Validate`** runs last, and only when every other rule passed — which is
  `TryValidateObject`'s sequencing, reproduced. [VM0067](/reference/diagnostics#vm0067) says so at
  the type.

Failures report under the [`custom`](/reference/codes) code, with the rule's own message. Member
names a rule reports at run time — `ValidationResult.MemberNames` — are converted with the same
naming policy the compiled literals were baked with, so everything lands on consistent paths.

**What it costs.** This is the one place validation pays DataAnnotations' own prices: a
`ValidationContext` per check (passing values included), a box for value-type members, and — for
an `IValidatableObject` type — the loss of the boolean fast path, since `IsValid` cannot know
"the whole pass was clean". Everything else on the model keeps the zero-allocation promise. When
the logic is yours to move, a [custom constraint attribute](/guide/custom-constraints) keeps the
attribute ergonomics at straight-line cost, and a [rule class](/guide/rule-classes) expresses the
same rule beside the model:

```csharp
public sealed class CustomerRules : IValidationRulesFor<Customer> {
    public static void Describe(ValidationRules<Customer> rules, Customer x) {
        rules.Ensure(x.Age % 2 == 0, code: "even_age");
    }
}
```

Resource-based messages on *mapped* attributes are compiled — see [Messages](#messages) above.
The reflective resolution DataAnnotations performs survives only inside invoked custom
attributes, where the attribute's own `FormatErrorMessage` runs user code this library will not
rewrite — [VM0081](/reference/diagnostics#vm0081) warns there, and only there.

## What is not compiled, and says so

Silence would be dangerous here in a way it is not for native attributes: an attribute this
generator skipped would still *look* enforced, because you have every reason to think
`TryValidateObject` would have honoured it. One attribute remains uncompiled, and it says so.

| Attribute | Diagnostic | Why |
|---|---|---|
| `[Compare]` | [VM0061](/reference/diagnostics#vm0061) | compares two members |

`[Compare]` has no per-property form. Move it to a [rule class](/guide/rule-classes) — the
declaration form that *can* express a rule spanning two properties:

```csharp
public sealed class CustomerRules : IValidationRulesFor<Customer> {
    public static void Describe(ValidationRules<Customer> rules, Customer x) {
        rules.Ensure(x.Password == x.Confirm, code: "password_mismatch");
    }
}
```

Or into an [`IAsyncValidatorFor<T>`](/guide/async) if the rule needs I/O.

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
