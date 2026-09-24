# Rules API

This page lists the members a rules class uses. For how they fit together, see
[Rules classes](../guide/rule-classes).

## IValidationRulesFor&lt;T&gt;

```csharp
public interface IValidationRulesFor<T>
{
    static abstract void Describe(ValidationRules<T> rules, T x);
}
```

A rules class implements it with `public static void Describe(ValidationRules<T> rules, T x)`. The
generator reads the body and never calls it. A class can implement the interface for several types.

## ValidationRules&lt;T&gt;

`ValidationRules<T>` is the type of the `rules` parameter. Every method except `Ensure`, `As` and
`Apply` returns a `PropertyRules<T, TValue>`, which later rules in the same chain apply to. Every
method that takes a value, except `As`, also takes an optional `field:`, which replaces the field
name derived from the value.

### Presence

| Method | Passes when | Code |
| --- | --- | --- |
| `Require(string? value)` | The string is not `null`, empty or whitespace. | `required` |
| `RequireAllowingEmpty(string? value)` | The string is not `null`. | `required` |
| `Require<TValue>(TValue? value)` | The reference or nullable value is not `null`. | `required` |

`Require` on a property of a non-nullable value type can never fail, and the generator reports
`VM3101`.

### Strings

| Method | Passes when | Code |
| --- | --- | --- |
| `Length(string? value, int min = 0, int max = int.MaxValue)` | The length is within the bounds. | `string_length` |
| `Pattern(string? value, Func<Regex> pattern)` | The regular expression matches. Pass a method group, such as a `[GeneratedRegex]` method. | `pattern` |

### Numbers, dates and times

| Method | Passes when | Code |
| --- | --- | --- |
| `Range<TValue>(TValue value, TValue min, TValue max)` | The value is within the bounds, inclusive. | `range` |
| `RangeAtLeast<TValue>(TValue value, TValue min)` | The value is at least `min`. | `range` |
| `RangeAtMost<TValue>(TValue value, TValue max)` | The value is at most `max`. | `range` |
| `MultipleOf(long? value, long divisor)` | The value divides by the divisor. | `multiple_of` |
| `MultipleOf(decimal? value, decimal divisor)` | The value divides by the divisor. | `multiple_of` |
| `MultipleOf(double? value, double divisor)` | The value divides by the divisor. | `multiple_of` |

The range methods take any struct that implements `IComparable<TValue>` and `IFormattable`, and each
has an overload for the nullable form. `Range` with constant bounds in the wrong order is reported
as `VM1101`.

The `double` overload converts the value to `decimal` before it divides, as `[MultipleOf]` does on a
`double` property, so `0.3` is a multiple of `0.1`. A constant divisor that is zero or negative is
reported as `VM1104`.

### Values

| Method | Passes when | Code |
| --- | --- | --- |
| `AllowedValues<TValue>(TValue value, TValue[] allowed)` | The value is one of `allowed`. | `enum` |

Write `allowed` as an array or a collection expression of constants, such as
`["active", "pending"]`. The chained form takes the values as separate arguments,
`.AllowedValues("active", "pending")`. A value that is not a compile-time constant is reported as
`VM3108`, and an empty set as `VM3109`.

### Collections

| Method | Effect | Code |
| --- | --- | --- |
| `Count<TElement>(IReadOnlyList<TElement>? value, int min = 0, int max = int.MaxValue)` | Passes when the number of items is within the bounds. | `array_bounds` |
| `Unique<TElement>(IEnumerable<TElement>? value)` | Passes when no item appears twice. | `unique_items` |
| `Each(IReadOnlyList<string>? value)` | Applies the rules chained after it to every element. | from those rules |
| `Each<TElement>(IReadOnlyList<TElement>? value)` | Runs the validators for `TElement` on every element. | from those validators |
| `Nested<TValue>(TValue? value)` | Runs the validators for `TValue` on the value. | from those validators |

`Nested` is for a single object. Use `Each` for a collection of objects. `Each` accepts a list of
strings or of a reference type. For a list of numbers or other value types, check the elements in a
loop and report through `Context`. `Nested` on a collection, and a second `Nested` or `Each` in the
chain after a descent, are reported as `VM3001`. A descent into a type with no rules is dropped with
`VM1501`, and one into a property that already has `[ValidateNested]` is dropped with `VM3106`.

### Conditions

```csharp
public ValidationRules<T> Ensure(
    bool condition,
    string? field = null,
    string? code = null,
    string? message = null,
    ValidationSeverity severity = ValidationSeverity.Error
);
```

Reports an error when `condition` is `false`. The expression is copied into the validator as
written and is not guarded against `null`. Without `field:`, the field is the first member of `x`
that the condition reads. Without `code:`, the code is derived from the condition, as described in
[Validation codes](./codes#codes-from-ensure). Without `message:`, the message is the condition's
text, with each member of `x` written as its field name.

### Other members

| Member | Effect |
| --- | --- |
| `For<TValue>(TValue value, string? field = null)` | Starts a chain for a value without a rule of its own. |
| `As<TFacet>(TFacet value)` | Runs the rules declared for an interface or base type of `x`, at the current level, including the constraint attributes on its properties. The type's own checks leave those attributes out. The argument must be `x`. |
| `Apply(RuleAction<T> rule)` | Runs a hand-written rule after every other rule on the type. Top level of `Describe` only. |
| `Context` | An `IValidationContextReporter` for reporting errors from code. See below. |

`RuleAction<T>` is a delegate: `ValidationFlow RuleAction<in T>(ref ValidationContext context, T
value)`. Pass a static method group, `internal` or `public`. A lambda whose whole body calls one
such method with its own parameters is read as that method, and any other lambda is `VM3008`.

## PropertyRules&lt;T, TValue&gt;

`PropertyRules<T, TValue>` is the type a rule method returns. These extension methods continue a
chain:

| Method | Applies to a chain on |
| --- | --- |
| `Require()` | a string, a reference type, or a nullable value type |
| `RequireAllowingEmpty()` | a string |
| `Length(min, max)` | a string |
| `Pattern(regex)` | a string |
| `Range(min, max)`, `RangeAtLeast(min)`, `RangeAtMost(max)` | a nullable value type |
| `MultipleOf(divisor)` | a `long?`, `decimal?` or `double?` |
| `AllowedValues(params allowed)` | any value |
| `Count(min, max)` | an `IReadOnlyList<T>` |
| `Unique()` | an `IEnumerable<T>` |
| `Each()` | an `IReadOnlyList<T>` |
| `Nested()` | a reference type |

A chain is typed by the method that starts it, so a chain method must accept that type. For example,
`rules.Range(x.Quantity, 1, 100)` on an `int` starts a chain on `int?`, which `MultipleOf(long)`
does not accept. Write such rules as separate statements.

When `Require` or `RequireAllowingEmpty` fails, the rest of its chain is skipped.

After `Each` on a list of strings, chain `Length` or `Pattern` to check each element.
`Require` after `Each` is reported as `VM3001`. Use `Length(1, ...)` to reject empty elements.

## Context

`rules.Context` is an `IValidationContextReporter`:

| Method | Effect |
| --- | --- |
| `Report(field, code, message, severity)` | Reports an error against `field`, below the current path. |
| `Report(field, code, value, messageInfo, severity)` | Reports an error with a structured message. |
| `ReportHere(code, message, severity)` | Reports an error against the current path itself. |

`severity` defaults to `ValidationSeverity.Error`. A `field` built with `nameof(x.Member)` becomes
the member's field name at build time.

### Report helpers

These extension methods report a built-in code with its default message. They work on
`rules.Context` in a rules class and on `ValidationContext` in a hand-written validator. Each takes
the field first, then its own arguments, then optional `severity`, `code` and `value` arguments.
In `ReportRange`, `ReportRangeAtLeast` and `ReportRangeAtMost`, the exclusivity flags come last,
after `value`, so pass them by name. `code` replaces the code and keeps the message. `value` records
the failed value in `ValidationError.Value`.

| Helper | Arguments | Code |
| --- | --- | --- |
| `ReportRequired` | | `required` |
| `ReportStringLength` | `min`, `max` | `string_length` |
| `ReportItemCount` | `min`, `max` | `array_bounds` |
| `ReportRange` | `min`, `max`, `exclusiveMin`, `exclusiveMax` | `range` |
| `ReportRangeAtLeast` | `min`, `exclusive` | `range` |
| `ReportRangeAtMost` | `max`, `exclusive` | `range` |
| `ReportMultipleOf` | `divisor` | `multiple_of` |
| `ReportPattern` | | `pattern` |
| `ReportAllowedValues` | `allowedValues`, the list as text | `enum` |
| `ReportDeniedValues` | `deniedValues`, the list as text | `enum` |
| `ReportEmail` | | `email` |
| `ReportPhone` | | `phone` |
| `ReportUrl` | | `url` |
| `ReportCreditCard` | | `credit_card` |
| `ReportBase64` | | `base64` |
| `ReportFileExtension` | `extensions`, the list as text | `file_extension` |
| `ReportUniqueItems` | | `unique_items` |
| `ReportCustom` | | `custom` |

```csharp
rules.Context.ReportStringLength(nameof(x.Guest), 0, 10, code: "guest_too_long");
```
