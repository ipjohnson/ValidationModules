# Custom constraint attributes

Your own attribute, compiled like a built-in. Derive from `CustomConstraintAttribute`, declare a
static check, and the generator writes the branch:

```csharp
using ValidationModules.Constraints;

public sealed class SkuAttribute : CustomConstraintAttribute {
    public const string DefaultMessage = "sku must look like SKU-XXXXXXXX";

    public static bool IsValid(string value) =>
        value.StartsWith("SKU-", StringComparison.Ordinal);
}

public record Product {
    [Required]
    [Sku]
    public string? Sku { get; init; }
}
```

What comes out the other side:

```csharp
if (value.Sku is not null && !global::SkuAttribute.IsValid(value.Sku) &&
    ctx.Report("sku", ValidationCodes.Custom, "sku must look like SKU-XXXXXXXX").ShouldStop)
{
    return ValidationFlow.Stop;
}
```

The attribute carries its own default message, so every use site gets it for free. Any one of them
can still override:

```csharp
[Sku(Message = "warehouse skus start with SKU-")]
public string? Sku { get; init; }
```

No instance, no context, no boxing, and nothing allocated on a passing value. It is the check you
would have written by hand, behind the attribute you wanted to write.

## Parameterize through the constructor

Constructor arguments flow into the check: each extra `IsValid` parameter matches the
constructor's parameters positionally, and the generator passes the constant the declaration
supplied.

```csharp
public sealed class DivisibleAttribute : CustomConstraintAttribute {
    public DivisibleAttribute(int divisor) { }

    public static bool IsValid(int value, int divisor) => value % divisor == 0;
}
```

```csharp
[Divisible(4)]
public int Count { get; init; }
```

```csharp
if (!global::DivisibleAttribute.IsValid(value.Count, 4) && …)
```

One attribute class, a family of checks, everything resolved before the program runs. The
constructor body can stay empty. Nothing constructs the attribute, so the parameters exist for the
generator to read rather than for an instance to hold.

## The contract

- **`public static bool IsValid(TMember value, …ctorArgs)`** on the attribute class itself. The
  first parameter takes the member type the attribute applies to (or a base type, an interface, or
  `object`); the rest mirror the constructor, positionally and by type.
- **Null passes.** The generated guard skips the check for a null member, like every constraint
  except `[Required]`. Declare `[Required]` beside it when absence should fail. A nullable value
  type arrives unwrapped: `int?` member, `int` parameter.
- **`Code`, `Message`, `When` and `Unless`** work exactly as they do on the built-ins, because they
  live on the shared base. Bake the defaults on the attribute itself as constants, meaning
  `public const string DefaultMessage` and `DefaultCode` on the class or a shared base, and every
  use site gets them. A use-site `Message` or `Code` still wins. Constants rather than constructor
  assignments, because nothing ever constructs the attribute: the generator reads it, and a
  constant is exactly as readable as the constructor arguments already are. With no default and no
  override, the message is a terse `"{field} is invalid."` and the code is
  [`custom`](/reference/codes).
- **Anything else arrives through the constructor.** A custom init-only property has no path into
  a static method, and setting one is [VM1601](/reference/diagnostics#vm1601), an error naming the
  fix, rather than an argument that silently never arrives. The same error covers every other
  wrong shape: a missing or non-static `IsValid`, a first parameter that cannot accept the member,
  parameters that do not line up with the constructor.

## When the check needs an instance

A static method cannot hold anything. Implement `IConstraintFor<T>` instead when the check wants
its own error codes and messages, or state built once from its arguments such as a lookup table, a
`SearchValues`, or a precompiled matcher. The generator constructs the attribute once from the
declaration, holds it in a static field on the validator, and calls it directly:

```csharp
using ValidationModules;

public sealed class ChannelAttribute : Attribute, IConstraintFor<string> {
    private readonly string[] _allowed;

    public ChannelAttribute(params string[] allowed) { _allowed = allowed; }

    public bool IsValid(string value) => Array.IndexOf(_allowed, value) >= 0;

    public ValidationFlow Validate(ref ValidationContext context, string value, string field) =>
        IsValid(value)
            ? ValidationFlow.Continue
            : context.Report(field, "channel", $"{field} must be one of: {string.Join(", ", _allowed)}.");
}
```

```csharp
[Channel("email", "sms")]
public string? Channel { get; init; }
```

```csharp
private static readonly ChannelAttribute ChannelConstraint0 = new ChannelAttribute(new string[] { "email", "sms" });
…
if (value.Channel is not null && ChannelConstraint0.Validate(ref ctx, value.Channel, "channel").ShouldStop)
```

The two members are the two engine paths. `IsValid` is the verdict. It serves the validator's
allocation-free boolean path, so it must return false exactly when a blocking error would be
reported. `Validate` is the reporting path, and it is optional: the interface's default
implementation asks `IsValid` and reports code [`custom`](/reference/codes), honouring a `Code`
or `Message` set at the declaration site when the attribute also derives from
`ValidationConstraintAttribute` (which is what also buys `When` and `Unless`). Override it to own
the code, the message, the severity, or to report more than one error.

The rest of the contract:

- **Null passes**, like every constraint except `[Required]`, and a nullable value type arrives
  unwrapped, so an `int?` member matches `IConstraintFor<int>`.
- **One instance, shared and called concurrently**, so be immutable after construction. A class
  that cannot be marks itself `[PerValidationInstance]` and is constructed at every check
  instead; [VM1603](/reference/diagnostics#vm1603) states the allocation at each site that pays
  it.
- **Several instantiations are fine.** The member's own type wins outright. Otherwise exactly one
  implemented instantiation must accept the member, or the build fails asking you to say
  which ([VM1602](/reference/diagnostics#vm1602), like every other wrong shape here).
- **A DataAnnotations attribute can adopt it.** A class deriving `ValidationAttribute` *and*
  implementing `IConstraintFor<T>` takes the fast path here and keeps working under MVC and
  `Validator.TryValidateObject`, so one class covers both. (In such a file, qualify
  `ValidationModules.ValidationContext` in the `Validate` signature: DataAnnotations declares a
  `ValidationContext` of its own.)

## Against the alternatives

| | declaration | cost per check | mistakes surface |
|---|---|---|---|
| `CustomConstraintAttribute` | attribute on the model | a branch | at build time (VM1601) |
| `IConstraintFor<T>` | attribute on the model | a call on a shared instance | at build time (VM1602) |
| [Rule class `Ensure`](/guide/rule-classes) | predicate beside the model | a branch | at build time |
| [Custom `ValidationAttribute`](/guide/data-annotations#custom-rules-are-invoked) | attribute on the model | a `ValidationContext` per check, boxing | at run time |

A rule class is the answer when the logic belongs to one model or spans two properties. This is
the answer when the check is *reusable vocabulary* that many models declare and one place defines,
such as a SKU, a slug, or an IBAN. A custom DataAnnotations attribute remains the migration path for
attributes you already have; this is what you write when you get to choose.

Between the two native shapes, reach for the static check first, because it is a branch and nothing
more. Reach for `IConstraintFor<T>` when the check genuinely wants an instance. Both are declared as
constants and verified at build time. A check needing services or I/O was never a structural
constraint, and belongs in an [`IAsyncValidatorFor<T>`](/guide/async).
