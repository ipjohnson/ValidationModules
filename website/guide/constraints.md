# Constraints

Ten attributes, all in `ValidationModules.Constraints`. Each one is read at build time and
becomes a branch; none of them is ever constructed at run time. When the vocabulary is missing
the constraint your domain repeats — a SKU, a slug, an IBAN — you can
[add your own attribute](/guide/custom-constraints) and it compiles the same way.

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed record Pet {
    [Required]
    public string? Name { get; init; }

    [StringLength(min: 1, max: 100)]
    public string? Name2 { get; init; }

    [Range(0, 30)]
    public int Age { get; init; }

    [Pattern("^[A-Z]{3}$")]
    public string? Sku { get; init; }

    [AllowedValues("available", "pending", "sold")]
    public string? Status { get; init; }

    [ItemCount(min: 1, max: 10)]
    public List<string> Tags { get; init; } = [];

    [MultipleOf(5)]
    public int Quantity { get; init; }

    [UniqueItems]
    public List<string> Codes { get; init; } = [];

    [ValidateNested]
    public Address? Home { get; init; }
}

public sealed record Address {
    [Required]
    public string? PostalCode { get; init; }
}
```

A type is picked up because it carries at least one constraint. Nothing needs to be registered, and
there is no marker interface. If you want a validator for a type that has no constraints of its own
— because a [rule class](/guide/rule-classes) supplies them, or because you want the nested walk —
mark it `[GenerateValidator]`.

## `[Required]`

Emits code `required`.

```csharp
[Required]
public string? Name { get; init; }
```

```csharp
if (string.IsNullOrWhiteSpace(value.Name)) ctx.ReportRequired("name");
```

What counts as missing depends on the type:

| Property type | Fails when |
|---|---|
| `string` | null, empty, or **whitespace only** |
| any reference type | null |
| `Nullable<T>` | null |
| non-nullable value type | never — this is [VM0004](/reference/diagnostics#vm0004) |

::: warning Whitespace-only strings are treated as missing
`"   "` fails `[Required]`. This matches `System.ComponentModel.DataAnnotations`, which trims before
testing, so a model moved from DataAnnotations behaves the same way. Set
`[Required(AllowEmptyStrings = true)]` to check for null alone.
:::

A failed `[Required]` **suppresses every other error on the same field** for the rest of the pass, so
a null `Name` reports `required` and not also `string_length`. That rule lives in the collector
rather than in the emitted `else if` — see [the error model](/guide/errors#suppression) for why it
has to.

## `[StringLength]`

Emits code `string_length`. Strings only; anything else is
[VM0001](/reference/diagnostics#vm0001).

```csharp
[StringLength(min: 1, max: 100)]
public string? Name { get; init; }

[StringLength(Max = 500)]
public string? Notes { get; init; }

[StringLength(Min = 8)]
public string? Token { get; init; }
```

The named form exists so declaring one bound reads as declaring one bound. `Min` defaults to `0` and
`Max` to `int.MaxValue`, so the omitted side imposes nothing. Inverted bounds are
[VM0008](/reference/diagnostics#vm0008).

Length is measured in UTF-16 code units — `string.Length`, not grapheme clusters. An emoji outside
the BMP counts as two.

## `[Range]`

Emits code `range`. Numeric and date-like types only; anything else is
[VM0003](/reference/diagnostics#vm0003).

```csharp
[Range(0, 30)]
public int Age { get; init; }

[Range(0.0, 1.0, ExclusiveMax = true)]
public double Ratio { get; init; }
```

Bounds are inclusive unless you say otherwise:

```csharp
if ((value.Age < 0 || value.Age > 30)) ctx.ReportRange("age", 0, 30);
```

`ExclusiveMin` and `ExclusiveMax` turn the corresponding comparison into `<=` / `>=`. Applying
`[Range]` to a `Nullable<T>` checks the value only when it has one; combine it with `[Required]` if
null should also fail.

Either bound may stand alone, through the named form:

```csharp
[Range(Min = 1)]
public int Quantity { get; init; }
```

An absent bound emits no comparison and is not named in the message — `quantity must be at least 1.`
rather than a second bound nobody wrote. A `[Range]` with neither bound can never fail, and is
[VM0026](/reference/diagnostics#vm0026).

::: tip String bounds, for the types with no constant form
`decimal`, `DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan` and `DateTimeOffset` have no constant form
in metadata, so their bounds are written as strings and parsed against the member's own type at
build time:

```csharp
[Range("2000-01-01", "2100-12-31")]
public DateOnly Born { get; init; }

[Range("0.00", "9.99")]
public decimal Price { get; init; }

[Range("00:00:00", "23:59:59")]
public TimeSpan Window { get; init; }
```

The bound is emitted as a constructor call — `new global::System.DateOnly(2000, 1, 1)` — in both the
comparison and the message, so the two cannot disagree. A bound that does not parse is
[VM0065](/reference/diagnostics#vm0065) at the declaration, not an error inside a generated file.

A `DateTime` bound is `DateTimeKind.Unspecified`: `"2000-01-01"` carries no zone, and anchoring it to
the build machine's would make the same source mean two things.
:::

## `[Pattern]`

Emits code `pattern`. Strings only; anything else is [VM0001](/reference/diagnostics#vm0001).

```csharp
[Pattern("^[A-Z]{3}$")]
public string? Sku { get; init; }
```

Two forms, and which one you use is the single biggest decision on this page for an AOT build:

```csharp
// Inline. Convenient, and roots the regex parser and interpreter — about 450 KB, once.
[Pattern("^[A-Z]{3}$")]
public string? Sku { get; init; }

// Referenced. Resolves to a [GeneratedRegex] you declared, so nothing is interpreted.
[Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
public string? Sku { get; init; }
```

[Patterns and regex](/guide/patterns) covers the size difference, the policy that governs it, and
what to do about it. The short version: in an AOT-facing project the inline form is
[VM0017](/reference/diagnostics#vm0017) by default.

A pattern that will not parse is [VM0006](/reference/diagnostics#vm0006), reported with the regex
engine's own message. Patterns are unanchored by default, following JSON Schema — `[Pattern("abc")]`
matches `"xabcx"`. Write `^…$` if you mean the whole value.

::: tip `RegexOptions.Compiled` is ignored
Setting it is [VM0016](/reference/diagnostics#vm0016). It emits IL through `Reflection.Emit`, which
is exactly what this library exists to avoid; patterns go through `[GeneratedRegex]` instead.
:::

## `[AllowedValues]`

Emits code `enum` — named for OpenAPI's `enum` keyword, which is where the code comes from.

```csharp
[AllowedValues("available", "pending", "sold")]
public string? Status { get; init; }
```

```csharp
if (value.Status is not null &&
    (value.Status != "available" && value.Status != "pending" && value.Status != "sold"))
    ctx.ReportAllowedValues("status", "available, pending, sold");
```

Comparison is `StringComparison.Ordinal` by default; `Comparison` changes it. The permitted set is
echoed in the message, deliberately — an enum's members are a *schema* fact, published in your
OpenAPI document anyway, so repeating them back discloses nothing the caller could not already read.

## `[ItemCount]`

Emits code `array_bounds`. Collections only; anything else is
[VM0002](/reference/diagnostics#vm0002).

```csharp
[ItemCount(min: 1, max: 10)]
public List<string> Tags { get; init; } = [];
```

`Min`/`Max` behave exactly as `[StringLength]`'s do, including the named form and the defaults.

A `string` is **not** a collection here, even though it implements `IEnumerable<char>`. Taking that
reading would turn a length constraint into a per-character walk, so `[ItemCount]` on a string is
VM0002 and `[StringLength]` is what you wanted.

The count is read without enumerating wherever a `Count` or `Length` exists. Where it does not — a
bare `IEnumerable<T>` — the emitter walks it once instead, so the constraint still applies rather
than being silently skipped.

## `[MultipleOf]`

Emits code `multiple_of`. OpenAPI's `multipleOf`. Numeric types only; anything else is
[VM0021](/reference/diagnostics#vm0021).

```csharp
[MultipleOf(5)]
public int Quantity { get; init; }

[MultipleOf("0.05")]
public decimal Price { get; init; }

[MultipleOf(0.01)]
public double Ratio { get; init; }
```

The divisor must be greater than zero — [VM0022](/reference/diagnostics#vm0022). This is an error
rather than a dropped rule because `value % 0` is a compile error for an integral member and a
`DivideByZeroException` for a decimal one, and both would land inside a generated file. A divisor
with no form the member's type can be checked against is
[VM0023](/reference/diagnostics#vm0023): a fractional divisor on an `int`, or a string that does
not parse.

`decimal` takes its divisor as a string, for the same reason `[Range]` does — it has no constant
form in metadata.

::: warning `double` and `float` are not checked with `%`
In binary floating point `0.3 % 0.01` is `0.00999999999999998`. A naive check against
`multipleOf: 0.01` rejects 0.3, 1.05, 99.99 and 1234.56 — every value a specification author would
call valid.

So a `double` or `float` member converts to `decimal` first, which rounds to 15 significant digits
and cancels exactly that error:

```csharp
if (!ConstraintChecks.IsMultipleOf(value.Ratio, 0.01m)) ctx.ReportMultipleOf("ratio", 0.01m);
```

Integral and `decimal` members are already exact, and compile to a plain comparison:

```csharp
if ((value.Quantity % 5 != 0)) ctx.ReportMultipleOf("quantity", 5);
```

The one case with no answer is a floating-point value past `decimal`'s range, around 7.9e28. Its
spacing there is wider than any realistic divisor, so it is reported as a failure rather than
passed as a value that could not be evaluated.
:::

## `[UniqueItems]`

Emits code `unique_items`. OpenAPI's `uniqueItems`. Collections only; anything else is
[VM0024](/reference/diagnostics#vm0024). No arguments — presence is the constraint.

```csharp
[UniqueItems]
public List<string> Codes { get; init; } = [];
```

This is the one constraint here that is not a comparison, so it is the one that calls into the
runtime rather than being written inline:

```csharp
if (value.Codes is not null && !ConstraintChecks.AllUnique(value.Codes)) ctx.ReportUniqueItems("codes");
```

`AllUnique` compares pairwise below sixteen elements and allocates a `HashSet<T>` above them, so a
request body's worth of elements still costs nothing on the heap. It takes an `IEnumerable<T>`, so
a property with no `Count` is checked like any other.

::: warning Elements need equality of their own
Comparison is `EqualityComparer<T>.Default` — value equality for records, primitives and anything
implementing `IEquatable<T>`, and **reference equality** for a class that overrides none of it. Two
elements with identical contents would then both pass, which is a rule succeeding for the wrong
reason. The generator reports [VM0025](/reference/diagnostics#vm0025) rather than letting it
through quietly.

A `HashSet<T>` or a dictionary cannot fail this constraint, since its own type already guarantees
what the rule asks for.
:::

## `[ValidateNested]`

Carries no check of its own. It tells the emitter to descend, and what descending means depends on
the property's shape — see [Nesting and collections](/guide/nesting).

```csharp
[ValidateNested]
public Address? Home { get; init; }

[ValidateNested]
public List<Toy> Toys { get; init; } = [];

[ValidateNested]
public Dictionary<string, Toy> ToysByName { get; init; } = new();
```

`[ValidateNested]` does not recurse into a value that failed `[Required]` — there is nothing to walk,
and reporting `home.postalCode` on an absent `home` would be noise.

## Overriding the code and the message

Every constraint carries `Code` and `Message`:

```csharp
[Required(Code = "pet_name_missing", Message = "A pet needs a name.")]
public string? Name { get; init; }
```

`Code` is a wire contract — something a client switches on — so change it only when you mean to.
`Message` is human-facing and safe to reword. Both are baked in as literals at build time.

## Field names

The name in `ValidationError.Field` is derived from the property name, camelCase by default:

```csharp
public string? PostalCode { get; init; }   // → "postalCode"
```

Precedence, highest first:

1. `[JsonPropertyName("…")]` on the property
2. `[Display(Name = "…")]`
3. the `ValidationModules_FieldNaming` MSBuild property — `CamelCase` (default), `PascalCase`,
   `SnakeCase`, `AsDeclared`

Because the name is baked in at generation time, nothing computes it per validation.
[MSBuild properties](/reference/msbuild#validationmodules-fieldnaming) has the details.

## Constraints on records

A positional record parameter needs the `property:` target, or the attribute lands on the parameter
and is never read:

```csharp
public sealed record Pet([property: Required] string Name);   // correct
public sealed record Pet([Required] string Name);             // silently validates nothing
```

::: warning The wrong form is caught, and it is worth knowing why it needs catching
The second form is [VM0051](/reference/diagnostics#vm0051). Without that diagnostic it is silent in
every direction: the attribute binds to the constructor parameter, so the property carries no
metadata, **no validator is emitted at all**, `IValidatorFor<Pet>` does not resolve, and a runner
merging zero validators reports every value as valid.

A record with an explicit body avoids the question:

```csharp
public sealed record Pet {
    [Required]
    public string? Name { get; init; }
}
```
:::

## `[EnumDefined]`

Emits code `enum`. Enum types only; anything else is
[VM0027](/reference/diagnostics#vm0027). No arguments — presence is the constraint.

```csharp
[EnumDefined]
public PaymentMethod Method { get; init; }
```

An enum is an integer with names on some of it. Nothing stops `(PaymentMethod)99` existing, and a
deserialiser handed `99` from the wire produces exactly that — so a handler switching on the value
falls through every case it was written for. This is the check that says the value came from the set
the type describes.

The members are known while the validator is being written, so the emitted test is a comparison
against them:

```csharp
if ((value.Method != PaymentMethod.Card && value.Method != PaymentMethod.Cash &&
     value.Method != PaymentMethod.Transfer)) ctx.ReportAllowedValues("method", "Card, Cash, Transfer");
```

Never `Enum.IsDefined`, which boxes, searches, and needs the enum's metadata kept alive under
trimming. This is a check a generator can afford and a reflection-based library charges for.

### `[Flags]` enums are a mask, not a membership test

On a `[Flags]` enum a combination of declared members is a legitimate value that equals no single
member, so membership would reject exactly what the type exists to express. The question there is
whether any bit outside the declared ones is set:

```csharp
if (((value.Rights & ~(Access.None | Access.Read | Access.Write | Access.Delete)) != 0))
    ctx.Report("rights", ValidationCodes.Enum, "rights must be a combination of: None, Read, Write, Delete.");
```

`Read | Delete` passes. `(Access)64` does not.

::: tip Absent is not undefined
`[EnumDefined]` on a nullable enum accepts `null` — it says nothing about whether a value is
required. Pair it with `[Required]` when you need both.
:::

