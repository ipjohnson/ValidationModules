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
`postcode is required.` Numbers and dates in a message are formatted with the invariant culture. To
show messages in another language, use a formatter as described in
[Messages and languages](./messages).

## Severity

| Severity | `IsValid` | Stops a fail-fast pass | Blocks async rules |
| --- | --- | --- | --- |
| `Error` | `false` | Yes | Yes |
| `Warning` | unchanged | No | No |
| `Info` | unchanged | No | No |

The built-in attribute checks and the rule methods of a rules class always report `Error`. A warning
or an informational entry comes from `Ensure(..., severity:)`, from a `rules.Context` report, or
from hand-written code such as a validator or an `IConstraintFor<T>` attribute.

## Field names

Field names are fixed when the generator runs. By default each member name is written in camelCase,
so `PostalCode` becomes `postalCode`. A member with `[JsonPropertyName("postal_code")]` or
`[Display(Name = "postal_code")]` uses that name instead. The `ValidationModules_FieldNaming`
property changes the default for the whole project:

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

`ValidateFirst` stops at the first failure with `Error` severity and returns it, along with any
warnings recorded before it:

```csharp
ValidationResult first = validator.ValidateFirst(order);
```

A collector with `StopMode = ValidationStopMode.StopOnFirstError` does the same for
`ValidateInto`, described below. Nested objects and later list elements are not visited after the
first error.

The generator writes an early return after every check so that a fail-fast pass stops quickly. Set
`ValidationModules_FailFast` to `false` to leave the returns out. The validators are then smaller,
and a fail-fast pass still records only one error, but it runs every check.

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

Some validation reads services during the pass: [runtime polymorphism](./nesting#subtypes)
and rules classes that use an interface from another assembly. `validator.Validate(value)` has no
service provider. For those types, validate through `ValidationRunner<T>` resolved from a scope, or
build the collector with the provider:

```csharp
var collector = new ValidationErrorCollector(serviceProvider);
validator.ValidateInto(collector, order);
```

## Depth limit

A single pass can descend at most 64 levels. Each nested object and each list element is one level.
The 65th level throws an `InvalidOperationException`, which usually means the object graph contains
a cycle. The limit is `ValidationErrorCollector.DefaultDepthLimit`.
