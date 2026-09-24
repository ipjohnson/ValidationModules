# Results and errors

`Validate` returns a `ValidationResult` that holds every failure as a `ValidationError`. This page
covers both types, severities, field paths, stopping at the first error, and exceptions.

## ValidationResult

| Member | Meaning |
| --- | --- |
| `Errors` | Every failure, in the order the checks ran. |
| `IsValid` | `false` when at least one entry has `Error` severity. |
| `HasErrors` | `true` when `Errors` has any entry, at any severity. |
| `Merge(other)` | A new result with this result's errors followed by the other's. |
| `ValidationResult.Valid` | The shared empty result. A pass with no failures returns it. |
| `ValidationResult.FromErrors(errors)` | A result built from a sequence of errors. |

A result that holds only warnings is valid and still has entries, so `IsValid` and `HasErrors` are
both `true`.

## ValidationError

| Property | Example | Meaning |
| --- | --- | --- |
| `Field` | `lines[1].shipTo.postcode` | The path to the value that failed. |
| `Code` | `required` | A stable identifier for the kind of failure. |
| `Message` | `postcode is required.` | Default English text for a person to read. |
| `Severity` | `Error` | `Error`, `Warning` or `Info`. |
| `Value` | `120` | The value that failed, when it was captured. Otherwise `null`. |
| `MessageInfo` | | The message template and its arguments, for formatters. |
| `MessageIsAuthored` | `false` | `true` when the text came from your own `Message` or `message:`. |

`ToString()` returns `field: code - message`. The error also deconstructs into its field, code and
message:

```csharp
var (field, code, message) = result.Errors[0];
```

Key application logic and translations on `Code`. The codes of the built-in checks are constants on
`ValidationCodes`, listed in the [codes reference](../reference/codes). A constraint attribute's
`Code` property, an `Ensure` call's `code:` argument and a hand-written `Report` call set your own.

`Message` renders the default English text each time it is read. A `{field}` in a template becomes
the last segment of `Field`, so an error at `lines[1].shipTo.postcode` reads
`postcode is required.` A property with `[Display(Name = "Postcode")]` is named by that label
instead, so its message reads `Postcode is required.` Numbers and dates in a message are formatted
with the invariant culture. To show messages in another language, use a formatter as described in
[Messages and languages](./messages).

## Severity

| Severity | `IsValid` | Stops a `StopOnFirstError` pass | Blocks async rules |
| --- | --- | --- | --- |
| `Error` | `false` | Yes | Yes |
| `Warning` | unchanged | No | No |
| `Info` | unchanged | No | No |

The built-in attribute checks and the rule methods of a rules class always report `Error`. A warning
or an informational entry comes from `Ensure(..., severity:)`, from a `rules.Context` report, or
from hand-written code such as a validator or an `IConstraintFor<T>` attribute.

## Field names

Field names are fixed when the generator runs. By default each member name is written in camelCase,
so `PostalCode` becomes `postalCode`. A member with `[JsonPropertyName("postal_code")]` uses that
name instead. `[Display(Name = "Postal code")]` does not change the field. It labels the member in
messages, as it does in DataAnnotations, so the error's `Field` is `postalCode` and its `Message` is
`Postal code is required.` The `ValidationModules_FieldNaming` property changes the default for the
whole project:

| Value | `PostalCode` becomes |
| --- | --- |
| not set | `postalCode` |
| `SnakeCase` | `postal_code` |
| `PascalCase` or `AsDeclared` | `PostalCode` |

```xml
<PropertyGroup>
  <ValidationModules_FieldNaming>SnakeCase</ValidationModules_FieldNaming>
</PropertyGroup>
```

The value is case-sensitive. An unrecognised value means camelCase.

The registration method also registers an `IValidationFieldNamer` for the same policy:
`CamelCaseFieldNamer`, `SnakeCaseFieldNamer` or `PascalCaseFieldNamer`, each with a shared
`Instance`. When a validation pass has a service provider, as it does through `ValidationRunner<T>`
and in ASP.NET Core, a field name that your own validator reports without a `.` or `[` goes through
that namer. A report such as `context.Report(nameof(Order.Reference), ...)` then uses the same
spelling as the generated checks. A pass without a service provider, such as
`validator.Validate(value)`, keeps field names as written.

The generated checks report their final names, so the namer never changes them, and a
`[JsonPropertyName("GivenName")]` reports `GivenName` with or without a runner. The same holds for
the member names that `IValidatableObject` and `[CustomValidation]` return: `nameof(GivenName)` is
reported under the property's field name.

A policy of your own derives from `FieldNamer` and implements `ToFieldName`. Register it before the
registration method, which keeps a namer that is already registered. It spells the names your own
validators report. The generated checks keep the names `ValidationModules_FieldNaming` gave them, so
a policy of your own should spell a name the way that setting does. `FieldNamer` also provides
`Combine` and `CombineIndex`, which join a parent path and a field name.

## Paths

A path joins the member names from the validated object down to the value that failed:

| Location | `Field` |
| --- | --- |
| A member of the validated object | `reference` |
| A member of a nested object | `shipTo.postcode` |
| A member of a list element | `lines[1].sku` |
| A member of a dictionary value | `byKey[work].postcode` |
| The validated object itself | the empty string |

By default, a path that passes through three or more nested objects keeps the first and the last of
them and replaces the ones between with `...`. This is `ValidationPathMode.Bounded`, and it limits
the length of paths in deeply nested data. `ValidationPathMode.Full` keeps every segment:

```csharp
var bounded = validator.Validate(order);
var full = validator.Validate(order, ValidationPathMode.Full);
```

| Mode | `Field` |
| --- | --- |
| `Bounded` | `lines[1]...location.latitude` |
| `Full` | `lines[1].shipTo.location.latitude` |

`ValidationRunner<T>`, `ValidationErrorCollector` and the ASP.NET Core `ValidationProblemOptions`
take the same setting.

## Captured values

A failed attribute check records the value that failed in `Value`. The value never appears in the
default message. A [formatter](./messages#formatters) can include it. To stop capturing values, for
example because the model holds personal data, set:

```xml
<PropertyGroup>
  <ValidationModules_CaptureValues>false</ValidationModules_CaptureValues>
</PropertyGroup>
```

Rules declared in a rules class do not capture values.

## Stop at the first error

By default a pass runs every check. This is `ValidationStopMode.CollectAll`. `ValidateFirst` runs
in `ValidationStopMode.StopOnFirstError` instead. It stops at the first failure with `Error`
severity and returns it, along with any warnings recorded before it:

```csharp
ValidationResult first = validator.ValidateFirst(order);
```

A collector with `StopMode = ValidationStopMode.StopOnFirstError` does the same for
`ValidateInto`, described below. Nested objects and later list elements are not visited after the
first error.

The generator writes an early return after every check so that a `StopOnFirstError` pass ends
quickly. Set `ValidationModules_FailFast` to `false` to leave the returns out. The validators are
then smaller. A `StopOnFirstError` pass still records only one error, but it runs every check.

## Throw instead of returning

`ValidateAndThrow` throws a `ValidationException` when the result is not valid. The exception's
`Result` property holds the full result. Its message names the first error and counts the rest:

```text
Validation failed: reference required, and 1 more.
```

A result with only warnings does not throw. In an ASP.NET Core application,
`AddValidationProblemDetails` registers a handler that turns the exception into a problem details
response. See [ASP.NET Core](./aspnetcore#thrown-validation-exceptions).

## Collect errors from several validators

A `ValidationErrorCollector` gathers errors from more than one pass into one result:

```csharp
var collector = new ValidationErrorCollector();

orderValidator.ValidateInto(collector, order);
customerValidator.ValidateInto(collector, customer);

ValidationResult result = collector.ToResult();
```

The collector's constructor takes a `ValidationPathMode`, an `IServiceProvider`, or both. It also
has a `StopMode` property. `Reset()` clears the errors and keeps the settings. A collector is for
one pass at a time. For work that runs concurrently, give each branch its own collector and combine
the results with `Merge`.

`collector.Add(error)` records a `ValidationError` created elsewhere, for example one converted from
another validation library, and takes its `Field` as given. Once `Add` has recorded a `required`
error with `Error` severity for a field, it drops later errors for the same field. Create the error
with `new ValidationError(field, code, message)`, or with the constructor that takes a value and a
`ValidationMessageInfo`.

Some validation reads services during the pass: [runtime polymorphism](./nesting#subtypes)
and rules classes that use an interface from another assembly. `validator.Validate(value)` has no
service provider. For those types, validate through `ValidationRunner<T>` resolved from a scope, or
build the collector with the provider:

```csharp
var collector = new ValidationErrorCollector(serviceProvider);
validator.ValidateInto(collector, order);
```

## A null value

`Validate`, `ValidateFirst`, `ValidateInto`, `ValidateAndThrow` and `IsValid` throw
`ArgumentNullException` for a null value, as `Validator.TryValidateObject` does for a null instance.
So do `ValidationRunner<T>.Validate` and `ValidateAsync`. In ASP.NET Core, `.Validate<T>()` lets a
null argument through to the handler instead. See [ASP.NET Core](./aspnetcore).

## Depth limit

A single pass can descend at most 64 levels. Each nested object and each list element is one level.
The 65th level throws an `InvalidOperationException`, which usually means the object graph contains
a cycle. The limit is `ValidationErrorCollector.DefaultDepthLimit`.
