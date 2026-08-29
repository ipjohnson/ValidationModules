# Error codes

The machine-readable vocabulary, as constants on `ValidationCodes`.

```csharp
if (error.Code == ValidationCodes.Required) { … }
```

Three things emit these codes — generated validators, hand-written ones, and the FluentValidation
adapter — and they have to agree exactly, or a client switching on `ValidationError.Code` breaks
depending on which engine found the error. Constants rather than literals is what stops that
drifting silently.

| Code | Constant | Emitted by |
|---|---|---|
| `required` | `ValidationCodes.Required` | `[Required]`, `rules.Required(…)` |
| `string_length` | `ValidationCodes.StringLength` | `[StringLength]`, `.Length(…)` |
| `range` | `ValidationCodes.Range` | `[Range]`, `.Range(…)` |
| `pattern` | `ValidationCodes.Pattern` | `[Pattern]`, `.Pattern(…)` |
| `enum` | `ValidationCodes.Enum` | `[AllowedValues]`, `.AllowedValues(…)` |
| `array_bounds` | `ValidationCodes.ArrayBounds` | `[ItemCount]`, `.Count(…)` |
| `multiple_of` | `ValidationCodes.MultipleOf` | `[MultipleOf]`, `.MultipleOf(…)` |
| `unique_items` | `ValidationCodes.UniqueItems` | `[UniqueItems]`, `.Unique(…)` |
| `predicate` | `ValidationCodes.Predicate` | `rules.Ensure(…)` |
| `email` | `ValidationCodes.Email` | DataAnnotations `[EmailAddress]` |
| `phone` | `ValidationCodes.Phone` | DataAnnotations `[Phone]` |
| `url` | `ValidationCodes.Url` | DataAnnotations `[Url]` |
| `credit_card` | `ValidationCodes.CreditCard` | DataAnnotations `[CreditCard]` |
| `base64` | `ValidationCodes.Base64` | DataAnnotations `[Base64String]` |
| `file_extension` | `ValidationCodes.FileExtension` | DataAnnotations `[FileExtensions]` |
| `invalid` | `ValidationCodes.Invalid` | nothing in this library — see below |

These are a **wire contract**. A client attaching messages to form inputs, or branching on failure
kind, depends on them not moving.

## `range` covers three shapes

`[Range(1, 99)]`, `[Range(Min = 1)]` and `[Range(Max = 99)]` all report `range`. Only the message
differs — "must be between 1 and 99", "must be at least 1", "must be at most 99" — because the
failure is the same one and a client should not have to learn a second code for it.

An absent bound is never named. A specification setting only `minimum` used to compose the type's
extreme into the message as the other bound.

## The two that read oddly

**`enum` for `[AllowedValues]`.** Named for OpenAPI's `enum` keyword, which is where the code
originates and what the first consumer already puts on the wire. Renaming it would break existing API
consumers for cosmetics.

**`invalid`, which nothing here emits.** A validator receives a typed model, so by the time it runs
the conversion has already succeeded — `?limit=abc` where an integer was expected is the *binder's*
failure, not validation's.

It lives in this vocabulary anyway, because the vocabulary is defined by the wire rather than by
which library produced the value. A client switching on `Code` sees this alongside the rest, and
splitting it out would leave every other code in one place and this one somewhere a consumer has
to already know to look.

## The format family gets a code each

`email`, `phone`, `url`, `credit_card`, `base64` and `file_extension` are six codes rather than one
`format`, for the reason `unique_items` is not folded into `array_bounds`: a client mapping codes to
its own messages wants to say "enter a valid email address", not "invalid format", and the field
name alone cannot tell it which. This is also the localization seam — the code is stable and
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
rules.Ensure(x => x.Discount <= x.Price * 0.5m, code: "discount_too_large");
```

That promotes one rule into your contract deliberately, which is the intended way to let a client
tell two rules on one field apart. Two `Ensure`s on one field otherwise both report `predicate`,
distinguished by their messages.

## Why `Ensure` does not derive its code

Slugging or hashing the predicate would read better and was rejected: message and code have opposite
churn requirements.

The message is human-facing and *should* track the rule — which is why `Ensure`'s message is the
predicate itself, rendered. The code is a wire contract. Derive it from the expression and widening a
bound from `30` to `35` becomes a breaking change for every client switching on it, and reordering
does the same if the code carries an ordinal.

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
`ValidationResult.IsValid` false — a result carrying nothing but warnings is valid *and* has errors,
which is why `IsValid` and `HasErrors` are separate properties.

The values match FluentValidation's `Severity` exactly, which makes the adapter's mapping a cast
rather than a table.

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
| `predicate` | *the predicate, rendered* — `start < end.` |

Composing rather than emitting a literal per constraint keeps the same text out of the binary once
per constraint site. Override with `Message` on any constraint.

**Assert on `Code` in tests, not on `Message`.** The code is the contract; the message is text that
may legitimately be reworded.
