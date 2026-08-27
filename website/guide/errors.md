# The error model

Everything a validation pass produces is a list of `ValidationError`, in a defined order, with codes
from a fixed vocabulary. That determinism is what lets two different engines be substitutable, and
it is pinned rather than incidental.

```csharp
public readonly record struct ValidationError(string Field, string Code, string Message) {
    public ValidationSeverity Severity { get; init; }
}
```

`(Field, Code, Message)` is also the argument order of `context.Add`, so the two never have to be
mentally transposed.

## `ValidationResult`

<!-- verify:models -->
```csharp
var result = new PetValidator().Validate(new Pet());

bool valid = result.IsValid;                            // no error has Severity == Error
bool anything = result.HasErrors;                       // non-empty at any severity
IReadOnlyList<ValidationError> errors = result.Errors;
```

It is **immutable**, and that is a deliberate correction rather than a default. A shared
`ValidationResult.Valid` instance is only safe because there is no `AddError` on it — a mutable
process-wide "success" singleton is exactly the kind of thing any caller can poison.

```csharp
ValidationResult.Valid;                      // the shared empty result
ValidationResult.FromErrors(errors);         // build one
first.Merge(second);                         // returns a new result; mutates neither
```

## Severity

```csharp
public enum ValidationSeverity {
    Error   = 0,   // the value is invalid
    Warning = 1,   // worth surfacing, value accepted
    Info    = 2,   // informational
}
```

`Error` is `0`, so an uninitialised severity is never silently benign. Only `Error` makes
`IsValid` false; a result carrying nothing but warnings is valid **and** has errors, which is why
both properties exist.

The values match FluentValidation's `Severity` exactly, which makes the adapter's mapping a cast
rather than a table.

## Codes

Fixed, and a wire contract. A client switching on `error.Code` depends on these not moving.

| Constraint | Code | Constant |
|---|---|---|
| `[Required]` | `required` | `ValidationCodes.Required` |
| `[StringLength]` | `string_length` | `ValidationCodes.StringLength` |
| `[Range]` | `range` | `ValidationCodes.Range` |
| `[Pattern]` | `pattern` | `ValidationCodes.Pattern` |
| `[AllowedValues]` | `enum` | `ValidationCodes.Enum` |
| `[ItemCount]` | `array_bounds` | `ValidationCodes.ArrayBounds` |
| `rules.Ensure(…)` | `predicate` | `ValidationCodes.Predicate` |
| *(binding failure)* | `invalid` | `ValidationCodes.Invalid` |

Use the constants rather than the literals. Three things emit these codes — generated validators,
hand-written ones, and the FluentValidation adapter — and they have to agree exactly.

`enum` for `[AllowedValues]` reads oddly and is kept anyway: it is already on the wire in the first
consumer, and renaming it would break existing API clients for cosmetics.

`invalid` is the one nothing in this library emits. A validator receives a typed model, so by the
time it runs the conversion has already succeeded — `?limit=abc` where an integer was expected is
the *binder's* failure. It lives here because the vocabulary is defined by the wire rather than by
which library produced the value.

::: tip Override per rule, not globally
`[Required(Code = "pet_name_missing")]` promotes that one rule into your contract deliberately. That
is the intended way to let a client tell two rules on one field apart — see
[`Ensure`](/guide/rule-classes#ensure) for the same argument applied to predicates.
:::

## Ordering

Errors emit in **declaration order**, deterministically:

- properties in source order,
- constraints in attribute order within a property,
- nested objects at the point of their property,
- collection elements ascending.

Registered validators run in registration order, and `ValidationRunner<T>` awaits async ones
sequentially, so the guarantee holds across validators too. The one exception is an async validator
that fans out internally with `Task.WhenAll` — its own errors land in completion order, which is the
author's choice to make.

There is **one** override: within a property, `[Required]` is evaluated first whatever order the
attributes were written in. The next section is why.

## Suppression

A failed `[Required]` suppresses every other error on the same field for the rest of the pass.

```csharp
public sealed record Pet {
    [Required]
    [StringLength(min: 1, max: 100)]
    public string? Name { get; init; }
}
```

A null `Name` reports `required` — once — and not also `string_length`. Reporting both would be
technically true and useless: the caller has one thing to fix.

**This is enforced by `ValidationErrorCollector`, not by the emitted `else if`.** That distinction
looks academic and is not. The FluentValidation adapter maps failures FluentValidation has already
produced; it has no control flow to put an `else` in, so `RuleFor(x => x.Name).NotNull().Length(1,
100)` against a null name hands it two failures. If suppression were a shape in emitted source, the
adapter could not honour it, and the two engines would stop being substitutable on the one semantic
the ordering rules exist to pin.

So it lives at the single point every error passes through. The `else if` in generated code becomes
an optimization — skip work whose result would be discarded — rather than the mechanism.

Three properties, each chosen against a plausible alternative:

- **Forward-only.** A field is suppressed from the moment it fails `required`; errors already
  recorded are not removed. Retroactive removal would make the result depend on the order two
  independent validators happened to run in.
- **Exact path match, not prefix.** `home.postalCode` and `work.postalCode` are different fields,
  and a failed `required` on `home` does not silence `home.postalCode`. (The emitter separately
  declines to descend into an absent object, which is why you do not see those anyway.)
- **`Error` severity only.** A `required` reported as a warning is advisory; silencing the field on
  the strength of it would drop a real failure.

## Field paths

Dotted, with bracketed indices and keys: `home.postalCode`, `toys[3].name`,
`toysByName[favourite].name`. Chosen over JSON Pointer because FluentValidation already produces
this shape, which keeps the adapter's job to a case conversion.

Paths are **compact** — outermost segment, immediate parent, field — with anything in between
elided as `...`. [Nesting and collections](/guide/nesting#field-paths-are-compact) has the table and
the reasoning.

## Nothing short-circuits

All errors are collected. There is no first-failure exit, because a caller fixing a request wants
the whole list rather than one item at a time.

`IsValid` is the exception, and only because it does not need the list:

```csharp
if (!new PetValidator().IsValid(pet)) { … }
```

## Throwing

```csharp
new PetValidator().ValidateAndThrow(pet);
```

Throws `ValidationException` carrying the `ValidationResult`. Nothing else in the library throws on
a validation failure — validating accumulates and returns.

```csharp
public sealed class ValidationException : Exception {
    public ValidationResult Result { get; }
}
```

The message names the first failure and counts the rest — `Validation failed: name required, and 2
more.` — because a message is for a log line and `Result` is there for anything that needs the
detail.

## Writing errors by hand

Inside an `IValidatorFor<T>` or an [`IAsyncValidatorFor<T>`](/guide/async), the context is how you
report:

```csharp
public ValidationFlow Validate(ref ValidationContext context, Pet value) {
    if (value.Name is null && context.ReportRequired("name").ShouldStop) {
        return ValidationFlow.Stop;
    }

    // on the object itself rather than a field of it — cross-field and type-level rules
    if (value.Start > value.End && context.ReportHere("date_order", "start must not be after end.").ShouldStop) {
        return ValidationFlow.Stop;
    }

    return ValidationFlow.Continue;
}
```

`ReportRequired`, `ReportStringLength`, `ReportRange`, `ReportPattern`, `ReportAllowedValues` and
`ReportItemCount` are extensions that compose the standard message and pass the standard code, so a
hand-written validator produces errors indistinguishable from a generated one.
`Report(field, code, message)` is there when you want your own.

## Stopping at the first failure {#stopping}

Every `Report*` call answers a [`ValidationFlow`](#stopping), and so does `Validate` itself. Under
the default `ValidationStopMode.CollectAll` the answer is always `ValidationFlow.Continue` and a
validator that discards it behaves exactly as it always has. Under
`ValidationStopMode.StopOnFirstError` the first blocking failure answers `ValidationFlow.Stop`, and
a validator that propagates it leaves the remaining rules — and any nested descent — unevaluated:

```csharp
var result = validator.ValidateFirst(pet);   // at most one error, and the rest never ran
```

`ValidateFirst` is the entry point; the mode itself lives on the collector, so a caller owning one
can set `StopMode` directly and `ValidateInto` it.

This is genuinely less work, not a filtered result. Nothing is known about the rules that did not
run, which is why the default stays `CollectAll`: a form or a 400 body wants every problem in one
round trip.

Warnings never stop a pass — a warning does not make a value invalid, so stopping on one would hide
the error behind it. And a hand-written `rules.Apply` or `IAsyncValidatorFor<T>` that discards the
flow simply keeps going, the same carve-out those two already have for `IsValid`.

Composing the message at the call site rather than baking a literal is deliberate: the same message
text would otherwise be duplicated into every generated validator, and the emitted binary would
carry a copy per constraint.
