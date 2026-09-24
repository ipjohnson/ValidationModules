# Custom constraints

When no built-in attribute fits a rule, write your own. There are three ways, from the simplest:

| Approach | Use it for |
| --- | --- |
| A `CustomConstraintAttribute` with a static `IsValid` | A check on one value, with constant arguments. |
| An attribute that implements `IConstraintFor<T>` | A check that needs its own state or its own error report. |
| A hand-written `IValidatorFor<T>` | A rule about the whole object, or one that needs services. |

A rules class covers many of the same cases without a new type. See [Rules classes](./rule-classes).

## A static check

Derive from `CustomConstraintAttribute` and declare a `public static bool IsValid` method. Its first
parameter receives the property's value. The remaining parameters receive the constructor's
arguments, matched by position:

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed class DivisibleByAttribute : CustomConstraintAttribute
{
    public DivisibleByAttribute(int divisor) { }

    public static bool IsValid(int value, int divisor) => value % divisor == 0;
}

public sealed class Pallet
{
    [DivisibleBy(4)]
    public int Boxes { get; init; }
}
```

The generator calls `DivisibleByAttribute.IsValid(value.Boxes, 4)` directly and never creates the
attribute. A `null` value passes. A failure reports the code `custom` with the message
`boxes is invalid.`

To give the constraint its own code and message, declare `DefaultCode` and `DefaultMessage`
constants. `Code` and `Message` at the point of use still take precedence:

```csharp
public sealed class LabelAttribute : CustomConstraintAttribute
{
    public const string DefaultCode = "label_format";
    public const string DefaultMessage = "{field} must start with PAL-.";

    public static bool IsValid(string value) => value.StartsWith("PAL-", StringComparison.Ordinal);
}
```

The first parameter of `IsValid` can be the property's type, a base type, an interface it
implements, or `object`. The constructor arguments must be constants. A class that does not fit
this shape is reported as `VM1601`, with the reason in the message.

## A check with state

An attribute that implements `IConstraintFor<T>` is created once for each property that uses it,
and the instance is shared by every validation. It can hold state built from its arguments and
report its own errors:

<!-- verify -->
```csharp
using ValidationModules;

public sealed class ChannelAttribute : Attribute, IConstraintFor<string>
{
    private readonly string[] _allowed;

    public ChannelAttribute(params string[] allowed) => _allowed = allowed;

    public bool IsValid(string value) => Array.IndexOf(_allowed, value) >= 0;

    public ValidationFlow Validate(ref ValidationContext context, string value, string field) =>
        IsValid(value)
            ? ValidationFlow.Continue
            : context.Report(
                field,
                "channel",
                $"{field} must be one of: {string.Join(", ", _allowed)}."
            );
}

public sealed class Notification
{
    [Channel("email", "sms")]
    public string? Channel { get; init; }
}
```

`IsValid` must return `false` exactly when `Validate` would report an error, because the
generated `IsValid` method of the validator calls it. Because the instance is shared, it must not
change after construction.

`Validate` has a default implementation. When the attribute also derives from
`ValidationConstraintAttribute`, the default reports the `Code` and `Message` set at the point of
use, and the attribute gains `When` and `Unless`:

<!-- verify -->
```csharp
using ValidationModules;
using ValidationModules.Constraints;

public sealed class EvenAttribute : ValidationConstraintAttribute, IConstraintFor<int>
{
    public bool IsValid(int value) => value % 2 == 0;
}

public sealed class Batch
{
    [Even(Code = "pair", Message = "{field} must come in pairs.")]
    public int? Size { get; init; }
}
```

The default `Validate` reports the `Message` with `Report`, so the text is not authored, and a
language pack with an entry for the code replaces it. To keep the text, implement `Validate` and
report with `ReportAuthored`.

When a class implements `IConstraintFor<T>` for several types, the one for the property's exact type
is used. A class that cannot be used this way is reported as `VM1602`, for example when two of its
`IConstraintFor<T>` interfaces accept the property and neither is for its exact type.

One attribute class can serve DataAnnotations and this library at once. Derive it from the
DataAnnotations `ValidationAttribute` and override `IsValid(object?)` for DataAnnotations, then
implement `IConstraintFor<T>` for the generator. The generator calls the `IConstraintFor<T>` members
directly and does not treat the class as a DataAnnotations attribute. `ValidationContext` exists in
both namespaces, so qualify it or use an alias in that file.

`[PerValidationInstance]` on the attribute class makes the generator create a new instance for every
check instead of sharing one. Use it only for an attribute that cannot be made immutable. Each use
is reported as `VM1603`, an informational diagnostic about the extra allocation.

## A hand-written validator

A class that implements `IValidatorFor<T>` can check anything about the object. Register it next to
the generated validator:

<!-- verify -->
```csharp
using ValidationModules;

public sealed class Pallet
{
    public int Boxes { get; init; }
    public int WeightKg { get; init; }
    public string? Contact { get; init; }
}

public sealed class PalletRulesValidator : IValidatorFor<Pallet>
{
    public ValidationFlow Validate(ref ValidationContext context, Pallet value)
    {
        if (
            value.WeightKg > 25 * value.Boxes
            && context.ReportRangeAtMost("weightKg", 25 * value.Boxes).ShouldStop
        )
        {
            return ValidationFlow.Stop;
        }

        if (
            value.Contact is { } contact
            && !ConstraintChecks.IsEmail(contact)
            && context.ReportEmail("contact", value: contact).ShouldStop
        )
        {
            return ValidationFlow.Stop;
        }

        return ValidationFlow.Continue;
    }
}
```

```csharp
services.AddShopValidators();
services.AddSingleton<IValidatorFor<Pallet>, PalletRulesValidator>();
```

Validate through `ValidationRunner<Pallet>` so that both validators run.
[Registration](./registration#hand-written-validators) explains why.

`Validate` is the only member to implement. `IsValid` has a default implementation that runs
`Validate` and discards the errors.

The `ValidationContext` records errors and tracks the current path:

| Member | Use |
| --- | --- |
| `Report(field, code, message)` | Report an error against a field below the current path. |
| `ReportHere(code, message)` | Report an error against the current path itself. At the top level the field is empty. |
| `ReportAuthored(field, code, message)` | Report text that language packs must not replace. |
| `Push(segment)`, `PushIndex(segment, index)`, `PushKey(segment, key)` | Return a context one level deeper, for validating a child object. |
| `Services` | The pass's `IServiceProvider`, when it has one. |
| `HasErrors`, `ErrorCount`, `StopMode` | The state of the pass so far. |
| `Mark()`, `HasBlockingErrorsSince(mark)` | `Mark()` returns a `ValidationMark` for the current point in the pass. `HasBlockingErrorsSince` then says whether an `Error` was reported after it. Warnings, and errors reported before the mark, do not count. Generated validators use this to decide whether to run `IValidatableObject.Validate`. |

To validate a child object with another validator, push a context for it and pass that context by
reference. The child's errors then carry the prefix:

```csharp
if (value.Home is { } home)
{
    var child = context.Push("home");
    if (AddressValidator.Validate(ref child, home).ShouldStop)
    {
        return ValidationFlow.Stop;
    }
}
```

The contexts of one pass share one path buffer, so use them depth first. Finish with one child
before pushing the next from the same parent. Reporting through a context after a sibling was pushed
throws an `InvalidOperationException`. For work that runs concurrently, give each branch its own
`ValidationErrorCollector` and merge the results.

Every `Report` method takes an optional `severity`. Each returns a `ValidationFlow`. Return it when
it is `ValidationFlow.Stop`, as the example does, so that a pass that [stops at the first
error](./errors#stop-at-the-first-error) can end there.

`ConstraintChecks` exposes the checks the generated code uses: `IsEmail`, `IsPhone`, `IsUrl`,
`IsCreditCard`, `IsBase64`, `HasFileExtension`, `IsMultipleOf`, `AllUnique`, and `IsMatch`, which
counts a regular expression match that times out as a failure. The `Report` helpers, such as
`ReportRequired`, `ReportStringLength`, `ReportRange`, `ReportRangeAtMost` and `ReportEmail`,
report the same codes and messages as the generated checks. Each helper takes an
optional `code:` that replaces the code and keeps the message. The [rules API
reference](../reference/rules-api#report-helpers) lists them.

A hand-written validator can also be asynchronous. See [Async validation](./async).
