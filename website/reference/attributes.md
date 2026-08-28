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
| `When` | `string?` | names a predicate; the constraint is checked only when it holds |
| `Unless` | `string?` | the negation of `When` |

There is no `Severity` on a constraint. Severity is reachable from
[`rules.Ensure(…, severity:)`](/reference/rules-api#ensure) and from `context.Add` in a
hand-written validator.

::: tip Profile attribution is deferred, and its surface has been withdrawn
`FromProfile`, `UntilProfile` and `Profiles` were on this type before profiles were built, so
setting one was an error rather than a restriction. They were removed rather than pinned into the
first stable release — writing one is now an ordinary "no such member" from the compiler.

Every removal is additively reversible, and the analysis is in `docs/deferred-features.md`.
:::

### Conditions

`When` and `Unless` name a member of the type being validated. Three shapes are accepted:

```csharp
public bool IsAuto { get; init; }                 // a bool property
public bool IsAuto() => …;                        // a parameterless bool method
public static bool IsAuto(Claim value) => …;      // a static bool method taking the model
```

```csharp
[Required(When = nameof(IsAuto))]
public string? PlateNumber { get; init; }

[Required(Unless = nameof(IsDraft))]
public string? Reference { get; init; }
```

Setting both on one constraint is [VM0033](/reference/diagnostics#vm0033); write two constraints, or
one negated condition.

Because it lives on the base, every constraint has it — `[ValidateNested]` included, which is the
discriminated-union case: the half of a model its discriminator says to ignore reports nothing.

::: tip A condition is evaluated once per validation pass
Not once per constraint that names it. Conditions may read live static state, so the two are
different answers rather than two spellings of one. The generated validator hoists each distinct
condition into a local above the method body; `DescribedValidator<T>` evaluates them into a
stack-allocated span before testing any rule.

One consequence worth knowing: hoisting means a condition runs even when a condition it is nested
inside is false, so `x => x.Auto.Wheels > 0` under `x => x.Auto != null` will throw rather than
short-circuit. Write the null check into the inner condition.
:::

Three shapes that cannot capture anything is not an accident — it is what makes the self-containment
[VM0072](/reference/diagnostics#vm0072) enforces for `Ensure` predicates hold here by construction.
There is no `WhenType`; shared logic is reached through a one-line forwarder on the model.

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
| `MatchTimeoutMilliseconds` | `int` | `0` | inline form only; `0` is no timeout |
| `Anchored` | `bool` | `false` | |
| `Code` | `string?` | `"pattern"` | |
| `Message` | `string?` | *composed* | |

Constructors: `(string pattern)` and `(Type regexProvider, string regexMember)`.

```csharp
[Pattern("^[A-Z]{3}$")]
[Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
```

`MatchTimeoutMilliseconds` becomes the emitted `Regex`'s match timeout, and a pattern that exceeds
it throws `RegexMatchTimeoutException` rather than returning a verdict — the same thing
`[RegularExpression]` does. Worth setting for any pattern that can backtrack catastrophically on
input you do not control. It applies to the inline form only: the reference form's `Regex` belongs
to you, so set the timeout on your own `[GeneratedRegex]`. Setting it also passes `Options`
explicitly, which costs the binary-size win described under
[VM0017](/reference/diagnostics#vm0017) — paid only where a timeout was asked for.

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

| Member | Type | |
|---|---|---|
| `Polymorphism` | `Polymorphism` | how the descent treats subtypes; constructor argument |

Tells the emitter to descend — into an object, into each element of a collection, or into each value
of a dictionary. See [Nesting and collections](/guide/nesting).

Does not recurse into a value that failed `[Required]`.

### `Polymorphism` {#polymorphism}

A descent dispatches on the **declared** type, so a subtype's own rules are not reached unless you
ask for them. Which is what this asks for:

```csharp
[ValidateNested(Polymorphism.CompileTime)]
public Payment? Payment { get; init; }
```

| Mode | | |
|---|---|---|
| `DeclaredOnly` | the declared type's rules and nothing else | no switch emitted, zero cost |
| `CompileTime` | a type switch over the subtypes visible at build time | no allocation, no container |
| `Runtime` | resolves a validator for the value's runtime type | a `GetType()` and a dictionary lookup |

`CompileTime` emits a type switch, most-derived first, and exactly one arm runs. The declared type's
validator sits in the `default` arm rather than after the switch — each subtype validator already
checks everything it inherits, so running both would report the base's failures twice.

`Runtime` resolves through the provider on the validation pass, which means it **composes**: a
separately registered `IValidatorFor<Card>` runs alongside the generated one, where `CompileTime`
consults no container and so cannot. It needs `Add<Assembly>Validators()` to have been called, and
there is no fallback — a missing provider throws rather than quietly checking less.

::: warning Never inferred
Dispatching automatically over whatever subtypes the generator happened to see would make coverage
depend on physical assembly layout: it would work while `Payment`, `Card` and `Bank` sat together
and shrink silently the day one moved to a package — no code change, no warning, no failing test.
Unearned confidence is worse than no feature, so the mode is always named.
[VM0031](/reference/diagnostics#vm0031) prompts for one on an unsealed target.
:::

Subtypes are found by inverting the base chain over the compilation. Types in referenced assemblies
are not enumerated, so a subtype declared in another assembly is not currently a `CompileTime`
dispatch target — use `Runtime` for a hierarchy that spans assemblies.

## `[GenerateValidator]`

No members. Emits a validator for a type that carries no constraints of its own — because a
[rule class](/guide/rule-classes) supplies them, or because you want the nested walk.

```csharp
[GenerateValidator]
public sealed record Address { … }
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
    ValidationFlow Validate(ref ValidationContext context, T value);
}

public interface IAsyncValidatorFor<in T> {
    ValueTask ValidateAsync(ValidationContext context, T value, CancellationToken cancellationToken = default);
}
```

The service interface is `IValidatorFor<T>`, not `IValidator<T>` — FluentValidation owns that name,
and an adapter's author will have both namespaces imported.
