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
first stable release, so writing one is now an ordinary "no such member" from the compiler.

Every removal is additively reversible.
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

Setting both on one constraint is [VM1403](/reference/diagnostics#vm1403); write two constraints, or
one negated condition.

Because it lives on the base, every constraint has it, `[ValidateNested]` included, which is the
discriminated-union case: the half of a model its discriminator says to ignore reports nothing.

::: tip A condition is evaluated once per validation pass
Not once per constraint that names it. Conditions may read live static state, so the two are
different answers rather than two spellings of one. The generated validator hoists each distinct
condition into a local above the method body. (This is the attribute surface's rule. In a
[rule class](/guide/rule-classes), conditions are `if` statements and evaluate where written.)

One consequence worth knowing: hoisting means a condition runs even when a condition it is nested
inside is false, so `x => x.Auto.Wheels > 0` under `x => x.Auto != null` will throw rather than
short-circuit. Write the null check into the inner condition.
:::

Three shapes that cannot capture anything is not an accident. It is what makes the self-containment
a `static abstract Describe` gives `Ensure` predicates hold here by construction.
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
never fail, which is [VM1201](/reference/diagnostics#vm1201).

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

Constructors: `()` and `(int min = 0, int max = int.MaxValue)`. Both parameters default to the
unbounded sentinels, so either may be omitted: `[StringLength(min: 12)]` and
`[StringLength(Min = 12)]` read identically, and `[StringLength(max: 40)]` gives one upper bound.
Positionally the first argument is `min` - note the vocabulary difference from DataAnnotations,
whose single-argument `StringLength(50)` is a maximum - so prefer the named form when giving one
bound. Strings only, [VM1001](/reference/diagnostics#vm1001). Inverted bounds are
[VM1101](/reference/diagnostics#vm1101).

Length is `string.Length`, in UTF-16 code units rather than grapheme clusters.

## `[Range]`

| Member | Type | Default |
|---|---|---|
| `Min` | `object?` | `null`, unbounded below |
| `Max` | `object?` | `null`, unbounded above |
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

[Range("0.01", "10000.00")]
public decimal Price { get; init; }

[Range(Min = "2020-01-01")]
public DateOnly Effective { get; init; }
```

Numeric and date-like types only, which is [VM1003](/reference/diagnostics#vm1003) otherwise.

An absent bound emits no comparison and is not named in the message. Neither bound is
[VM1102](/reference/diagnostics#vm1102).

The `(string, string)` overload is for the types with no constant form in metadata: `decimal`,
`DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan`, `DateTimeOffset`. The bound is parsed against the
member's type at build time and emitted as a constructor call, in both the comparison and the
message. A bound that does not parse is [VM1103](/reference/diagnostics#vm1103).

## `[Pattern]`

| Member | Type | Default |
|---|---|---|
| `Pattern` | `string?` | *(none)* | inline form |
| `RegexProvider` | `Type?` | *(none)* | reference form |
| `RegexMember` | `string?` | *(none)* | reference form |
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
it throws `RegexMatchTimeoutException` rather than returning a verdict, which is the same thing
`[RegularExpression]` does. Worth setting for any pattern that can backtrack catastrophically on
input you do not control. It applies to the inline form only: the reference form's `Regex` belongs
to you, so set the timeout on your own `[GeneratedRegex]`. Setting it also passes `Options`
explicitly, which costs the binary-size win described under
[VM1301](/reference/diagnostics#vm1301), paid only where a timeout was asked for.

Strings only. Unanchored by default, following JSON Schema. `Options` is not consulted in the
reference form, so put them on your `[GeneratedRegex]`. `RegexOptions.Compiled` is
[VM1302](/reference/diagnostics#vm1302).

See [Patterns and regex](/guide/patterns) for which form to use.

## `[EmailAddress]`

| Member | Type | Default |
|---|---|---|
| `Code` | `string?` | `"email"` |
| `Message` | `string?` | *composed* |

```csharp
[Required, EmailAddress]
public string? Email { get; init; }
```

The first of five format validators carried under `System.ComponentModel.DataAnnotations`' exact
names and semantics, so migrating a model is swapping a using directive. The check is the BCL's
own: exactly one `@`, neither first nor last, and no line breaks. `a@b` passes, because RFC 5322
permits a dotless domain. A stricter grammar is a `[Pattern]`.

Strings only, which is [VM1001](/reference/diagnostics#vm1001) otherwise. Like every format
validator, null passes; presence is `[Required]`'s question.

## `[Phone]`

| Member | Type | Default |
|---|---|---|
| `Code` | `string?` | `"phone"` |
| `Message` | `string?` | *composed* |

```csharp
[Phone]
public string? Contact { get; init; }
```

After stripping every `+`, trailing whitespace, and a trailing extension (`ext.`, `ext` or `x`
followed by digits), the value must contain at least one digit and nothing but digits, whitespace
and `- . ( )`. Strings only.

## `[Url]`

| Member | Type | Default |
|---|---|---|
| `Code` | `string?` | `"url"` |
| `Message` | `string?` | *composed* |

```csharp
[Url]
public string? Homepage { get; init; }

[Url]
public Uri? Docs { get; init; }
```

On a string: it must start with `http://`, `https://` or `ftp://`, case-insensitively, and
nothing past the prefix is checked. On a `System.Uri` member: absolute, with one of those three
schemes. Any other member type is [VM1001](/reference/diagnostics#vm1001).

## `[CreditCard]`

| Member | Type | Default |
|---|---|---|
| `Code` | `string?` | `"credit_card"` |
| `Message` | `string?` | *composed* |

```csharp
[CreditCard]
public string? CardNumber { get; init; }
```

The digits - dashes and spaces skipped - must pass the Luhn mod-10 checksum. Strings only.

## `[Base64String]`

| Member | Type | Default |
|---|---|---|
| `Code` | `string?` | `"base64"` |
| `Message` | `string?` | *composed* |

```csharp
[Base64String]
public string? Signature { get; init; }
```

Well-formed Base64 as `Convert.FromBase64String` reads it, whitespace included. Strings only.
`Base64String` rather than `Base64`, matching the BCL: a name that is almost the DataAnnotations
name would be a trap.

## `[FileExtensions]`

| Member | Type | Default |
|---|---|---|
| `Extensions` | `string?` | `"png,jpg,jpeg,gif"` |
| `Code` | `string?` | `"file_extension"` |
| `Message` | `string?` | *composed* |

```csharp
[FileExtensions(Extensions = "pdf,docx")]
public string? Attachment { get; init; }
```

The file name's extension must be one of the set, compared case-insensitively. Plural, matching
the BCL's own slightly awkward name, and normalized exactly as the BCL normalizes it - spaces and
dots removed, lowercased, split on commas - so its quirks survive: `tar.gz` reads as `.targz`.
Strings only.

## `[AllowedValues]`

| Member | Type | Default |
|---|---|---|
| `Values` | `object[]` | *(none)* |
| `Comparison` | `StringComparison` | `Ordinal` |
| `Code` | `string?` | `"enum"` |
| `Message` | `string?` | *composed* |

```csharp
[AllowedValues("available", "pending", "sold")]
public string? Status { get; init; }
```

Constructor is `params object[]`. The permitted set is echoed in the message, because an enum's
members are
a schema fact, published in your OpenAPI document anyway.

## `[DeniedValues]`

| Member | Type | Default |
|---|---|---|
| `Values` | `object[]` | *(none)* |
| `Code` | `string?` | `"enum"` |
| `Message` | `string?` | *composed* |

```csharp
[DeniedValues("admin", "root", "system")]
public string? Username { get; init; }
```

`[AllowedValues]` negated: the value must be none of the set. It compiles as the same membership
check with the direction flipped and emits the same `enum` code - which is also how the
DataAnnotations bridge reads the BCL pair - so override `Code` when a client needs to tell the
two apart.

## `[EnumDefined]`

| Member | Type | Default |
|---|---|---|
| `Code` | `string?` | `"enum"` |
| `Message` | `string?` | *composed* |

```csharp
[EnumDefined]
public PetKind Kind { get; init; }
```

The enum member must be one of its type's declared values - the guard against
`(PetKind)42` arriving through a permissive deserializer. The members are read at build time, so
the check is a comparison rather than `Enum.IsDefined`: no boxing, no reflection, nothing for the
trimmer to keep. On a `[Flags]` enum the check becomes a mask test, because `Read | Write` is a
legitimate value that equals no single member. Enums only, which is
[VM1006](/reference/diagnostics#vm1006) otherwise.

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

Collections only, which is [VM1002](/reference/diagnostics#vm1002) otherwise. A `string` is not a
collection here.
Counted without enumerating where a `Count` or `Length` exists; walked once otherwise.

## `[MultipleOf]`

| Member | Type | Default |
|---|---|---|
| `Divisor` | `object` | *(none)* |
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

Numeric types only, which is [VM1004](/reference/diagnostics#vm1004) otherwise. The divisor must be
greater than zero
([VM1104](/reference/diagnostics#vm1104)) and must have a form the member's type can be checked
against ([VM1105](/reference/diagnostics#vm1105)).

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

Collections only, which is [VM1005](/reference/diagnostics#vm1005) otherwise. Elements are compared
with
`EqualityComparer<T>.Default`; an element type with no equality of its own compares by reference and
is [VM1202](/reference/diagnostics#vm1202).

## `[ValidateNested]`

| Member | Type | |
|---|---|---|
| `Polymorphism` | `Polymorphism` | how the descent treats subtypes; constructor argument |

Tells the emitter to descend into an object, into each element of a collection, or into each value
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
validator sits in the `default` arm rather than after the switch, because each subtype validator
already
checks everything it inherits, so running both would report the base's failures twice.

`Runtime` resolves through the provider on the validation pass, which means it **composes**: a
separately registered `IValidatorFor<Card>` runs alongside the generated one, where `CompileTime`
consults no container and so cannot. It needs `Add<Assembly>Validators()` to have been called, and
there is no fallback. A missing provider throws rather than quietly checking less. The machinery
behind the mode is public: the registration maps each validated type to an `IDynamicValidator`
adapter in a `DynamicValidatorRegistry`, and the generated descent resolves through
`DynamicValidation` - one lookup per descent, never per rule.

::: warning Never inferred
Dispatching automatically over whatever subtypes the generator happened to see would make coverage
depend on physical assembly layout: it would work while `Payment`, `Card` and `Bank` sat together
and shrink silently the day one moved to a package, with no code change, no warning, and no failing
test.
Unearned confidence is worse than no feature, so the mode is always named.
[VM1503](/reference/diagnostics#vm1503) prompts for one on an unsealed target.
:::

Subtypes are found by inverting the base chain over the compilation. Types in referenced assemblies
are not enumerated, so a subtype declared in another assembly is not currently a `CompileTime`
dispatch target. Use `Runtime` for a hierarchy that spans assemblies.

## `[GenerateValidator]`

No members. Emits a validator for a type that carries no constraints of its own, either because a
[rule class](/guide/rule-classes) supplies them, or because you want the nested walk.

```csharp
[GenerateValidator]
public sealed record Address { … }
```

## `[PerValidationInstance]`

No members, and not a constraint: it marks a [custom constraint
attribute](/guide/custom-constraints) implementing `IConstraintFor<T>`, telling the emitter to
construct the attribute at every check instead of hoisting one shared instance into a static
field. For an attribute that keeps per-pass state; the construction cost is
[VM1603](/reference/diagnostics#vm1603), paid where it was asked for.

The base class for authoring your own attribute-shaped constraints is
`CustomConstraintAttribute` (the static-check shape) or `ValidationConstraintAttribute` plus
`IConstraintFor<T>` (the instance shape); both are the [custom
constraints](/guide/custom-constraints) guide's subject.

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

The whole constraint vocabulary is read as a second front end, and every DataAnnotations
validation attribute has a native equivalent - under the same name where the concept is the same
- so a model file needs exactly one using. See [DataAnnotations](/guide/data-annotations) for the
mapping and for what is deliberately not compiled.

## Interfaces

### `IValidationRulesFor<T>`

```csharp
public interface IValidationRulesFor<T> {
    static abstract void Describe(ValidationRules<T> rules, T x);
}
```

Declares rules for `T` from outside it, in a body that is read at build time and never run. See
[Rule classes](/guide/rule-classes).

### `IValidatorFor<T>` and `IAsyncValidatorFor<T>`

```csharp
public interface IValidatorFor<in T> {
    ValidationFlow Validate(ref ValidationContext context, T value);
}

public interface IAsyncValidatorFor<in T> {
    ValueTask ValidateAsync(ValidationContext context, T value, CancellationToken cancellationToken = default);
}
```

The service interface is `IValidatorFor<T>`, not `IValidator<T>`. FluentValidation owns that name,
and a project using both libraries would have to disambiguate every use.
