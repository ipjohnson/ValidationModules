# The error model

Everything a validation pass produces is a list of `ValidationError`, in a defined order, with codes
from a fixed vocabulary. That determinism is what lets two different engines be substitutable, and
it is pinned rather than incidental.

```csharp
public readonly record struct ValidationError {
    public string Field { get; }
    public string Code { get; }
    public object? Value { get; }                    // captured, never rendered by any default
    public ValidationSeverity Severity { get; init; }
    public ValidationMessageInfo? MessageInfo { get; }

    public string Message { get; }                   // rendered by this read
}
```

The message is data until something reads it. A structural failure stores the constraint's template
and arguments in one shared `ValidationMessageInfo` per constraint site, and `Message` renders on
read. That is what lets one result render differently per reader. The three-argument
`ValidationError(field, code, message)` constructor still exists for errors that arrive as finished
text, and `(Field, Code, Message)` is the argument order everywhere, so the two never have to be
transposed. [Messages and translation](/guide/messages) covers the rest, including the redaction
rules for the attempted value.

## `ValidationResult`

<!-- verify:models -->
```csharp
var result = new PetValidator().Validate(new Pet());

bool valid = result.IsValid;                            // no error has Severity == Error
bool anything = result.HasErrors;                       // non-empty at any severity
IReadOnlyList<ValidationError> errors = result.Errors;
```

It is **immutable**, on purpose. A shared `ValidationResult.Valid` instance is only safe because
there is no `AddError` on it. A mutable process-wide "success" singleton is something any caller
could poison.

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

The numeric values match FluentValidation's `Severity`, so a migration can cast between the two
rather than translate through a table.

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

Use the constants rather than the literals. Generated validators and hand-written ones both emit
these codes, and they have to agree exactly.

`enum` for `[AllowedValues]` reads oddly and is kept anyway: it is already on the wire in the first
consumer, and renaming it would break existing API clients for cosmetics.

`invalid` is the one nothing in this library emits. A validator receives a typed model, so by the
time it runs the conversion has already succeeded. A `?limit=abc` where an integer was expected is
the *binder's* failure. It lives here because the vocabulary is defined by the wire rather than by
which library produced the value.

::: tip Override per rule, not globally
`[Required(Code = "pet_name_missing")]` promotes that one rule into your contract deliberately. That
is the intended way to let a client tell two rules on one field apart. See
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
that fans out internally with `Task.WhenAll`. Its own errors land in completion order, which is the
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

A null `Name` reports `required` once, and not also `string_length`. Reporting both would be
technically true and useless, because the caller has one thing to fix.

**This is enforced by `ValidationErrorCollector`, not by the emitted `else if`.** The distinction
matters because not every error arrives through emitted control flow. A hand-written
`IValidatorFor<T>` reports whatever it decides to report, and the DataAnnotations front end maps
results that `Validator.TryValidateObject` has already produced. Neither one has an `else` to put
the rule in.

Suppression therefore lives at the single point every error passes through, so every engine gets it.
The `else if` in generated code is an optimization on top. It skips work whose result would be
discarded.

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
`toysByName[favourite].name`. Chosen over JSON Pointer because .NET clients already read this
shape: ASP.NET Core's `ModelState` keys and FluentValidation's property names both use it.

Paths are **compact**: the outermost segment, the immediate parent, and the field, with anything in
between elided as `...`. [Nesting and collections](/guide/nesting#field-paths-are-compact) has the
table and the reasoning.

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
a validation failure. Validating accumulates and returns.

```csharp
public sealed class ValidationException : Exception {
    public ValidationResult Result { get; }
}
```

The message names the first failure and counts the rest, as in
`Validation failed: name required, and 2 more.` A message is for a log line, and `Result` is there
for anything that needs the detail.

## Writing errors by hand

Inside an `IValidatorFor<T>` or an [`IAsyncValidatorFor<T>`](/guide/async), the context is how you
report:

```csharp
public ValidationFlow Validate(ref ValidationContext context, Pet value) {
    if (value.Name is null && context.ReportRequired("name").ShouldStop) {
        return ValidationFlow.Stop;
    }

    // on the object itself rather than a field of it: cross-field and type-level rules
    if (value.Start > value.End && context.ReportHere("date_order", "start must not be after end.").ShouldStop) {
        return ValidationFlow.Stop;
    }

    return ValidationFlow.Continue;
}
```

`ReportRequired`, `ReportStringLength`, `ReportRange`, `ReportPattern`, `ReportAllowedValues`, and
`ReportItemCount` are extensions that compose the standard message and pass the standard code, so a
hand-written validator produces errors indistinguishable from a generated one. Use
`Report(field, code, message)` when you want your own.

## Stopping at the first failure {#stopping}

Every `Report*` call answers a `ValidationFlow`, and so does `Validate` itself. Under
the default `ValidationStopMode.CollectAll` the answer is always `ValidationFlow.Continue` and a
validator that discards it behaves exactly as it always has. Under
`ValidationStopMode.StopOnFirstError` the first blocking failure answers `ValidationFlow.Stop`, and
a validator that propagates it leaves the remaining rules unevaluated, including any nested
descent:

```csharp
var result = validator.ValidateFirst(pet);   // at most one error, and the rest never ran
```

`ValidateFirst` is the entry point. The mode itself lives on the collector, so a caller that owns
one can set `StopMode` directly and `ValidateInto` it.

This is genuinely less work rather than a filtered result. Nothing is known about the rules that
did not run, which is why the default stays `CollectAll`. A form or a 400 body wants every problem
in one round trip.

Warnings never stop a pass. A warning does not make a value invalid, so stopping on one would hide
the error behind it.

A hand-written `rules.Apply` or `IAsyncValidatorFor<T>` that discards the flow simply keeps going,
the same carve-out those two already have for `IsValid`. It still reports one error: the collector
closes the pass at its first blocking failure. The *result* never depends on whether the code
running it propagates the flow, only on how much work it did to get there. That is also what makes
[`ValidationModules_FailFast`](/reference/msbuild#validationmodules-failfast) a size trade rather
than a behaviour switch.

Nothing composes a message at the call site. A failing rule records the template and arguments and
moves on, and the text exists once, in the runtime's `ValidationMessageTemplates`, rather than
duplicated into every generated validator. The prose is built by whoever reads it. See
[Messages and translation](/guide/messages).
