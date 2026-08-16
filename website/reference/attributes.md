# Attributes

Every attribute the generator reads, in `ValidationModules.Constraints` unless noted.

None of these is ever constructed at run time. Their arguments are read out of metadata during the
build and compiled into comparisons.

## Shared members

Every constraint derives from `ValidationConstraintAttribute` and inherits:

| Member | Type | |
|---|---|---|
| `Code` | `string?` | overrides the machine-readable code |
| `Message` | `string?` | overrides the composed message |

There is no `Severity` on a constraint. Severity is reachable from
[`rules.Ensure(…, severity:)`](/reference/rules-api#ensure) and from `context.Add` in a
hand-written validator.

::: tip Profile attribution is deferred, and its surface has been withdrawn
`FromProfile`, `UntilProfile` and `Profiles` were on this type before profiles were built, so
setting one was an error rather than a restriction. They were removed rather than pinned into the
first stable release — writing one is now an ordinary "no such member" from the compiler.

Every removal is additively reversible, and the analysis is in `docs/deferred-features.md`.
:::

## `[Required]`

| Member | Type | Default | |
|---|---|---|---|
| `AllowEmptyStrings` | `bool` | `false` | treat `""` and `"   "` as present |
| `Code` | `string?` | `"required"` | |
| `Message` | `string?` | *composed* | |

```csharp
[Required]
public string? Name { get; init; }

[Required(AllowEmptyStrings = true)]
public string? Note { get; init; }
```

Fails on null; on a `string`, also on empty and whitespace-only. On a non-nullable value type it can
never fail — [VM0004](/reference/diagnostics#vm0004).

## `[StringLength]`

| Member | Type | Default |
|---|---|---|
| `Min` | `int` | `0` |
| `Max` | `int` | `int.MaxValue` |
| `Code` | `string?` | `"string_length"` |
| `Message` | `string?` | *composed* |

```csharp
[StringLength(min: 1, max: 100)]
public string? Name { get; init; }

[StringLength(Max = 500)]
public string? Notes { get; init; }

[StringLength(Min = 8)]
public string? Token { get; init; }
```

Constructors: `()` and `(int min, int max)`. Strings only —
[VM0001](/reference/diagnostics#vm0001). Inverted bounds are
[VM0008](/reference/diagnostics#vm0008).

Length is `string.Length` — UTF-16 code units, not grapheme clusters.

## `[Range]`

| Member | Type | Default |
|---|---|---|
| `Min` | `object?` | `null` — unbounded below |
| `Max` | `object?` | `null` — unbounded above |
| `ExclusiveMin` | `bool` | `false` |
| `ExclusiveMax` | `bool` | `false` |
| `Code` | `string?` | `"range"` |
| `Message` | `string?` | *composed* |

Constructors: `()`, `(int, int)`, `(long, long)`, `(double, double)`, `(string, string)`.

```csharp
[Range(0, 30)]
public int Age { get; init; }

[Range(0.0, 1.0, ExclusiveMax = true)]
public double Ratio { get; init; }

[Range(Min = 1)]
public int Quantity { get; init; }
```

Numeric and date-like types only — [VM0003](/reference/diagnostics#vm0003).

An absent bound emits no comparison and is not named in the message. Neither bound is
[VM0026](/reference/diagnostics#vm0026).

The `(string, string)` overload is for the types with no constant form in metadata — `decimal`,
`DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan`, `DateTimeOffset`. The bound is parsed against the
member's type at build time and emitted as a constructor call, in both the comparison and the
message. A bound that does not parse is [VM0065](/reference/diagnostics#vm0065).

## `[Pattern]`

| Member | Type | Default |
|---|---|---|
| `Pattern` | `string?` | — | inline form |
| `RegexProvider` | `Type?` | — | reference form |
| `RegexMember` | `string?` | — | reference form |
| `Options` | `RegexOptions` | `None` | inline form only |
| `MatchTimeoutMilliseconds` | `int` | `0` | |
| `Anchored` | `bool` | `false` | |
| `Code` | `string?` | `"pattern"` | |
| `Message` | `string?` | *composed* | |

Constructors: `(string pattern)` and `(Type regexProvider, string regexMember)`.

```csharp
[Pattern("^[A-Z]{3}$")]
[Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
```

Strings only. Unanchored by default, following JSON Schema. `Options` is not consulted in the
reference form — put them on your `[GeneratedRegex]`. `RegexOptions.Compiled` is
[VM0016](/reference/diagnostics#vm0016).

See [Patterns and regex](/guide/patterns) for which form to use.

## `[AllowedValues]`

| Member | Type | Default |
|---|---|---|
| `Values` | `object[]` | — |
| `Comparison` | `StringComparison` | `Ordinal` |
| `Code` | `string?` | `"enum"` |
| `Message` | `string?` | *composed* |

```csharp
[AllowedValues("available", "pending", "sold")]
public string? Status { get; init; }
```

Constructor is `params object[]`. The permitted set is echoed in the message — an enum's members are
a schema fact, published in your OpenAPI document anyway.

## `[ItemCount]`

| Member | Type | Default |
|---|---|---|
| `Min` | `int` | `0` |
| `Max` | `int` | `int.MaxValue` |
| `Code` | `string?` | `"array_bounds"` |
| `Message` | `string?` | *composed* |

```csharp
[ItemCount(min: 1, max: 10)]
public List<string> Tags { get; init; } = [];
```

Collections only — [VM0002](/reference/diagnostics#vm0002). A `string` is not a collection here.
Counted without enumerating where a `Count` or `Length` exists; walked once otherwise.

## `[MultipleOf]`

| Member | Type | Default |
|---|---|---|
| `Divisor` | `object` | — |
| `Code` | `string?` | `"multiple_of"` |
| `Message` | `string?` | *composed* |

Constructors: `(int)`, `(long)`, `(double)`, `(string)`.

```csharp
[MultipleOf(5)]
public int Quantity { get; init; }

[MultipleOf("0.05")]
public decimal Price { get; init; }

[MultipleOf(0.01)]
public double Ratio { get; init; }
```

Numeric types only — [VM0021](/reference/diagnostics#vm0021). The divisor must be greater than zero
([VM0022](/reference/diagnostics#vm0022)) and must have a form the member's type can be checked
against ([VM0023](/reference/diagnostics#vm0023)).

`double` and `float` are checked in the decimal domain rather than with `%`, because
`0.3 % 0.01` is `0.00999999999999998` in binary floating point. See
[the guide](/guide/constraints#multipleof) for what that costs and what it buys.

## `[UniqueItems]`

| Member | Type | Default |
|---|---|---|
| `Code` | `string?` | `"unique_items"` |
| `Message` | `string?` | *composed* |

Constructors: `()`. Presence is the constraint.

```csharp
[UniqueItems]
public List<string> Codes { get; init; } = [];
```

Collections only — [VM0024](/reference/diagnostics#vm0024). Elements are compared with
`EqualityComparer<T>.Default`; an element type with no equality of its own compares by reference and
is [VM0025](/reference/diagnostics#vm0025).

## `[ValidateNested]`

No members. Tells the emitter to descend — into an object, into each element of a collection, or
into each value of a dictionary. See [Nesting and collections](/guide/nesting).

Does not recurse into a value that failed `[Required]`.

## `[GenerateValidator]`

No members. Emits a validator for a type that carries no constraints of its own — because a
[rule class](/guide/rule-classes) supplies them, or because you want the nested walk.

```csharp
[GenerateValidator]
public record Address { … }
```

## Attributes read from elsewhere

### `System.Text.Json.Serialization.JsonPropertyName`

Overrides the derived field name, highest precedence.

```csharp
[Required]
[JsonPropertyName("pet_name")]
public string? Name { get; init; }      // errors report "pet_name"
```

### `System.ComponentModel.DataAnnotations.Display`

`Name` overrides the derived field name, below `[JsonPropertyName]`.

### `System.ComponentModel.DataAnnotations.*`

The whole constraint vocabulary is read as a second front end. See
[DataAnnotations](/guide/data-annotations) for the mapping and for what is deliberately not
compiled.

## Interfaces

### `IValidationRulesFor<T>`

```csharp
public interface IValidationRulesFor<T> {
    void Describe(ValidationRules<T> rules);
}
```

Declares rules for `T` from outside it. Read at build time by the generator and runnable at run time
by `DescribedValidator<T>`. See [Rule classes](/guide/rule-classes).

### `IValidatorFor<T>` and `IAsyncValidatorFor<T>`

```csharp
public interface IValidatorFor<in T> {
    void Validate(ref ValidationContext context, T value);
}

public interface IAsyncValidatorFor<in T> {
    ValueTask ValidateAsync(ValidationContext context, T value, CancellationToken cancellationToken = default);
}
```

The service interface is `IValidatorFor<T>`, not `IValidator<T>` — FluentValidation owns that name,
and an adapter's author will have both namespaces imported.
