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
| `FromProfile` | `Type?` | **not implemented** — [VM0019](/reference/diagnostics#vm0019) |
| `UntilProfile` | `Type?` | **not implemented** — VM0019 |
| `Profiles` | `Type[]?` | **not implemented** — VM0019 |

There is no `Severity` on a constraint. Severity is reachable from
[`rules.Ensure(…, severity:)`](/reference/rules-api#ensure) and from `context.Add` in a
hand-written validator.

::: danger Profile attribution is declared but not implemented
Profiles are Stage 3 of the plan and are **not built**. The declaration surface shipped ahead of the
implementation, so this compiles and reads exactly as the design describes:

```csharp
[Required(FromProfile = typeof(V2))]     // VM0019, an error
public string? Tag { get; init; }
```

Were it accepted, the rule would be enforced **unconditionally** — including under V1, rejecting
data the caller was entitled to send. So it is [VM0019](/reference/diagnostics#vm0019), an error
rather than a warning, and the same for assembly-level `[DefaultValidationProfile]`.

Declaring `IValidationProfile` types is harmless. Attaching a rule to one is what does not work.
:::

## `[Required]`

| Member | Type | Default | |
|---|---|---|---|
| `AllowEmptyStrings` | `bool` | `false` | treat `""` and `"   "` as present |
| `Code` | `string?` | `"required"` | |
| `Message` | `string?` | *composed* | |

```csharp
[Required]                              public string? Name { get; init; }
[Required(AllowEmptyStrings = true)]    public string? Note { get; init; }
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
[StringLength(min: 1, max: 100)]   public string? Name  { get; init; }
[StringLength(Max = 500)]          public string? Notes { get; init; }
[StringLength(Min = 8)]            public string? Token { get; init; }
```

Constructors: `()` and `(int min, int max)`. Strings only —
[VM0001](/reference/diagnostics#vm0001). Inverted bounds are
[VM0008](/reference/diagnostics#vm0008).

Length is `string.Length` — UTF-16 code units, not grapheme clusters.

## `[Range]`

| Member | Type | Default |
|---|---|---|
| `Min` | `object` | — |
| `Max` | `object` | — |
| `ExclusiveMin` | `bool` | `false` |
| `ExclusiveMax` | `bool` | `false` |
| `Code` | `string?` | `"range"` |
| `Message` | `string?` | *composed* |

Constructors: `(int, int)`, `(long, long)`, `(double, double)`, `(string, string)`.

```csharp
[Range(0, 30)]                          public int    Age   { get; init; }
[Range(0.0, 1.0, ExclusiveMax = true)]  public double Ratio { get; init; }
```

Numeric and date-like types only — [VM0003](/reference/diagnostics#vm0003).

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
[AllowedValues("available", "pending", "sold")] public string? Status { get; init; }
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
[ItemCount(min: 1, max: 10)] public List<string> Tags { get; init; } = [];
```

Collections only — [VM0002](/reference/diagnostics#vm0002). A `string` is not a collection here.
Counted without enumerating where a `Count` or `Length` exists; walked once otherwise.

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
