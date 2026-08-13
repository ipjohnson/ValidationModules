# Constraints

Seven attributes, all in `ValidationModules.Constraints`. Each one is read at build time and
becomes a branch; none of them is ever constructed at run time.

```csharp
using ValidationModules.Constraints;

public record Pet {
    [Required]                                      public string? Name   { get; init; }
    [StringLength(min: 1, max: 100)]                public string? Name2  { get; init; }
    [Range(0, 30)]                                  public int     Age    { get; init; }
    [Pattern("^[A-Z]{3}$")]                         public string? Sku    { get; init; }
    [AllowedValues("available", "pending", "sold")] public string? Status { get; init; }
    [ItemCount(min: 1, max: 10)]                    public List<string> Tags { get; init; } = [];
    [ValidateNested]                                public Address? Home  { get; init; }
}
```

A type is picked up because it carries at least one constraint. Nothing needs to be registered, and
there is no marker interface. If you want a validator for a type that has no constraints of its own
— because a [rule class](/guide/rule-classes) supplies them, or because you want the nested walk —
mark it `[GenerateValidator]`.

## `[Required]`

Emits code `required`.

```csharp
[Required] public string? Name { get; init; }
```

```csharp
if (string.IsNullOrWhiteSpace(value.Name)) ctx.AddRequired("name");
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
[StringLength(min: 1, max: 100)] public string? Name  { get; init; }
[StringLength(Max = 500)]        public string? Notes { get; init; }
[StringLength(Min = 8)]          public string? Token { get; init; }
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
[Range(0, 30)]                         public int    Age   { get; init; }
[Range(0.0, 1.0, ExclusiveMax = true)] public double Ratio { get; init; }
```

Bounds are inclusive unless you say otherwise:

```csharp
if ((value.Age < 0 || value.Age > 30)) ctx.AddRange("age", 0, 30);
```

`ExclusiveMin` and `ExclusiveMax` turn the corresponding comparison into `<=` / `>=`. Applying
`[Range]` to a `Nullable<T>` checks the value only when it has one; combine it with `[Required]` if
null should also fail.

::: danger The string-bounds overload does not work
`RangeAttribute` has a `(string min, string max)` overload, documented for `decimal`, `DateTime`,
`DateOnly` and `TimeSpan`. **It is not implemented.** The bound is emitted as a quoted string
literal rather than parsed, so `[Range("2000-01-01", "2100-01-01")]` on a `DateOnly` emits
`value.Born < "2000-01-01"` and the generated file does not compile.

[VM0065](/reference/diagnostics#vm0065) is the diagnostic declared to catch this, and it is one of
three that are declared but never reported. Until it is fixed, express a date bound with
[`rules.Ensure`](/guide/rule-classes#ensure) instead:

```csharp
rules.Ensure(x => x.Born >= new DateOnly(2000, 1, 1));
```
:::

## `[Pattern]`

Emits code `pattern`. Strings only; anything else is [VM0001](/reference/diagnostics#vm0001).

```csharp
[Pattern("^[A-Z]{3}$")] public string? Sku { get; init; }
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
[AllowedValues("available", "pending", "sold")] public string? Status { get; init; }
```

```csharp
if (value.Status is not null &&
    (value.Status != "available" && value.Status != "pending" && value.Status != "sold"))
    ctx.AddAllowedValues("status", "available, pending, sold");
```

Comparison is `StringComparison.Ordinal` by default; `Comparison` changes it. The permitted set is
echoed in the message, deliberately — an enum's members are a *schema* fact, published in your
OpenAPI document anyway, so repeating them back discloses nothing the caller could not already read.

## `[ItemCount]`

Emits code `array_bounds`. Collections only; anything else is
[VM0002](/reference/diagnostics#vm0002).

```csharp
[ItemCount(min: 1, max: 10)] public List<string> Tags { get; init; } = [];
```

`Min`/`Max` behave exactly as `[StringLength]`'s do, including the named form and the defaults.

A `string` is **not** a collection here, even though it implements `IEnumerable<char>`. Taking that
reading would turn a length constraint into a per-character walk, so `[ItemCount]` on a string is
VM0002 and `[StringLength]` is what you wanted.

The count is read without enumerating wherever a `Count` or `Length` exists. Where it does not — a
bare `IEnumerable<T>` — the emitter walks it once instead, so the constraint still applies rather
than being silently skipped.

## `[ValidateNested]`

Carries no check of its own. It tells the emitter to descend, and what descending means depends on
the property's shape — see [Nesting and collections](/guide/nesting).

```csharp
[ValidateNested] public Address? Home { get; init; }
[ValidateNested] public List<Toy> Toys { get; init; } = [];
[ValidateNested] public Dictionary<string, Toy> ToysByName { get; init; } = new();
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
public record Pet([property: Required] string Name);   // correct
public record Pet([Required] string Name);             // silently validates nothing
```

::: danger This is not currently diagnosed
[VM0051](/reference/diagnostics#vm0051) exists for exactly this and is **never reported**. The
second form compiles, emits **no validator at all**, and produces no diagnostic — the type looks
unconstrained to the generator, because as far as the metadata is concerned it is. Nothing is
registered for it, so `IValidatorFor<Pet>` does not resolve and a `ValidationRunner<Pet>` merging
zero validators reports every value as valid.

Prefer a record with an explicit body until that is fixed:

```csharp
public record Pet {
    [Required] public string? Name { get; init; }
}
```
:::
