# Error codes

The machine-readable vocabulary, as constants on `ValidationCodes`.

```csharp
if (error.Code == ValidationCodes.Required) { … }
```

Generated validators and hand-written ones both emit these codes, and they have to agree exactly.
Otherwise a client switching on `ValidationError.Code` breaks depending on which one found the
error. Using the constants rather than the literals is what stops that drifting silently.

| Code | Constant | Emitted by |
|---|---|---|
| `required` | `ValidationCodes.Required` | `[Required]`, `rules.Require(…)` |
| `string_length` | `ValidationCodes.StringLength` | `[StringLength]`, `.Length(…)` |
| `range` | `ValidationCodes.Range` | `[Range]`, `.Range(…)` |
| `pattern` | `ValidationCodes.Pattern` | `[Pattern]`, `.Pattern(…)` |
| `enum` | `ValidationCodes.Enum` | `[AllowedValues]`, `.AllowedValues(…)` |
| `array_bounds` | `ValidationCodes.ArrayBounds` | `[ItemCount]`, `.Count(…)` |
| `multiple_of` | `ValidationCodes.MultipleOf` | `[MultipleOf]`, `.MultipleOf(…)` |
| `unique_items` | `ValidationCodes.UniqueItems` | `[UniqueItems]`, `.Unique(…)` |
| `predicate` | `ValidationCodes.Predicate` | an `Ensure` with nothing derivable in its condition |
| `email` | `ValidationCodes.Email` | DataAnnotations `[EmailAddress]` |
| `phone` | `ValidationCodes.Phone` | DataAnnotations `[Phone]` |
| `url` | `ValidationCodes.Url` | DataAnnotations `[Url]` |
| `credit_card` | `ValidationCodes.CreditCard` | DataAnnotations `[CreditCard]` |
| `base64` | `ValidationCodes.Base64` | DataAnnotations `[Base64String]` |
| `file_extension` | `ValidationCodes.FileExtension` | DataAnnotations `[FileExtensions]` |
| `custom` | `ValidationCodes.Custom` | [custom constraint attributes](/guide/custom-constraints), custom `ValidationAttribute`s, `[CustomValidation]`, `IValidatableObject` |
| `invalid` | `ValidationCodes.Invalid` | nothing in this library, see below |

These are a **wire contract**. A client attaching messages to form inputs, or branching on failure
kind, depends on them not moving.

## `range` covers three shapes

`[Range(1, 99)]`, `[Range(Min = 1)]` and `[Range(Max = 99)]` all report `range`. Only the message
differs between "must be between 1 and 99", "must be at least 1", and "must be at most 99", because
the
failure is the same one and a client should not have to learn a second code for it.

An absent bound is never named. A specification setting only `minimum` used to compose the type's
extreme into the message as the other bound.

## The two that read oddly

**`enum` for `[AllowedValues]`.** Named for OpenAPI's `enum` keyword, which is where the code
originates and what the first consumer already puts on the wire. Renaming it would break existing API
consumers for cosmetics.

**`invalid`, which nothing here emits.** A validator receives a typed model, so by the time it runs
the conversion has already succeeded. A `?limit=abc` where an integer was expected is the *binder's*
failure, not validation's.

It lives in this vocabulary anyway, because the vocabulary is defined by the wire rather than by
which library produced the value. A client switching on `Code` sees this alongside the rest, and
splitting it out would leave every other code in one place and this one somewhere a consumer has
to already know to look.

## The format family gets a code each

`email`, `phone`, `url`, `credit_card`, `base64` and `file_extension` are six codes rather than one
`format`, for the reason `unique_items` is not folded into `array_bounds`: a client mapping codes to
its own messages wants to say "enter a valid email address", not "invalid format", and the field
name alone cannot tell it which. This is also the localization seam, because the code is stable and
machine-readable, and the client owns the words.

It is deliberately distinct from `range` rather than folded into it: the value never became the right
type, so no constraint on it was evaluated at all, and reporting `range` would claim one was.

## Overriding a code

Every constraint carries `Code`:

```csharp
[Required(Code = "pet_name_missing")]
public string? Name { get; init; }
```

```csharp
rules.Ensure(x.Discount <= x.Price * 0.5m, code: "discount_too_large");
```

That pins the code against a later change to the condition, which is the reason to pass one.

## Why `Ensure` derives its code {#why-ensure-derives-its-code}

An `Ensure` reports a code derived from the same render its message comes from, so
`x.Start < x.End` reports `start_less_than_end`. Without it every predicate in an application shared
one key, and a translation catalogue keyed by code could not tell two of them apart.

**The code moves when the rule moves, and that is the point.** Widening `<` to `<=` changes what the
user is told and what a client should do about it. A key that survived the edit would be asserting
that nothing happened, and a translation carried across it would be quietly wrong. This is the model
gettext has used for decades: the message identifier *is* the source string, so rewording the source
invalidates the translation by construction.

**A rename does not move it.** The code derives from the render, and the render puts members under
their wire names, so a property renamed in C# behind a pinned `[JsonPropertyName]` moves neither the
message nor the code. Rename one that is not pinned and `ValidationError.Field` has already moved,
so nothing breaks that was not broken already.

Operator spellings are the `System.Linq.Expressions.ExpressionType` names in snake_case:

| C# | Fragment | C# | Fragment |
|---|---|---|---|
| `<` | `less_than` | `==` | `equal` |
| `<=` | `less_than_or_equal` | `!=` | `not_equal` |
| `>` | `greater_than` | `&&` | `and` |
| `>=` | `greater_than_or_equal` | `\|\|` | `or` |
| `!` | `not` | method call | its name, snake_cased |

Spelled out rather than abbreviated because the abbreviated dialects disagree with each other: OData
spells `<=` as `le` where MongoDB and Django spell it `lte`. There was no short convention to adopt,
and the spelled-out form is what both `ExpressionType` and FluentValidation's comparison validators
already use.

Common idioms are named for what they assert rather than for the tokens that spell them, which puts
the subject first:

| Condition | Code |
|---|---|
| `string.IsNullOrEmpty(x.Name)` | `name_is_null_or_empty` |
| `!string.IsNullOrWhiteSpace(x.Name)` | `name_is_not_null_or_blank` |
| `x.Name == null`, `x.Name is null` | `name_is_null` |
| `x.Name != null`, `x.Name is not null` | `name_is_not_null` |
| `x.Items.Count == 0`, `x.Items.Length == 0` | `items_is_empty` |
| `x.Items.Count > 0` | `items_is_not_empty` |

Two spellings of one assertion share a code on purpose. `== null` and `is null` say the same thing,
so a client and a translator should see the same key for both. The emptiness idioms fire only when
the comparison is the whole rule: `x.Items.Count > 0 && x.Paid` is a count being used rather than an
emptiness test, and keeps `items_count_greater_than_0_and_paid`.

::: warning Every code here is pinned
The spellings and the idiom table are a wire contract. Respelling one operator or recognising one
more idiom moves the codes of rules nobody edited, which is churn with no semantic reason behind it.
`RuleText.CodeDerivationContract` and a corpus checksum in product source enforce that: accepting
the test snapshot is deliberately not enough to move a code, because the constant has to be edited
too. It moves only in a major release.
:::

[VM3103](/reference/diagnostics#vm3103) states the derived code at each rule, since it is the one
part of a rules class you cannot read off the source.

## Severity

Independent of the code.

```csharp
public enum ValidationSeverity {
    Error   = 0,
    Warning = 1,
    Info    = 2,
}
```

`Error` is `0`, so an uninitialised severity is never silently benign. Only `Error` makes
`ValidationResult.IsValid` false. A result carrying nothing but warnings is valid *and* has errors,
which is why `IsValid` and `HasErrors` are separate properties.

The numeric values match FluentValidation's `Severity`, so a migration can cast between the two
rather than translate through a table.

## Messages

Composed at the call site from the field name and the bounds, not baked in as literals:

| Code | Message |
|---|---|
| `required` | `name is required.` |
| `string_length` | `name must be between 1 and 100 characters.` |
| `string_length` | `notes must be at most 500 characters.` |
| `range` | `age must be between 0 and 30.` |
| `enum` | `status must be one of: available, pending, sold.` |
| `array_bounds` | `tags must be between 1 and 10 items.` |
| `predicate` | *the predicate, rendered*, as `start < end.` |

Composing rather than emitting a literal per constraint keeps the same text out of the binary once
per constraint site. Override with `Message` on any constraint.

**Assert on `Code` in tests, not on `Message`.** The code is the contract; the message is text that
may legitimately be reworded.
