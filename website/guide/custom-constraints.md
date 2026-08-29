# Custom constraint attributes

Your own attribute, compiled like a built-in. Derive from `CustomConstraintAttribute`, declare a
static check, and the generator writes the branch:

```csharp
using ValidationModules.Constraints;

public sealed class SkuAttribute : CustomConstraintAttribute {
    public static bool IsValid(string value) =>
        value.StartsWith("SKU-", StringComparison.Ordinal);
}

public record Product {
    [Required]
    [Sku(Message = "sku must look like SKU-XXXXXXXX")]
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

No instance, no context, no boxing, nothing allocated on a passing value — the check you would
have written by hand, wearing the attribute you wanted to write.

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
constructor body can stay empty — nothing constructs the attribute, so the parameters exist for
the generator to read, not for an instance to hold.

## The contract

- **`public static bool IsValid(TMember value, …ctorArgs)`** on the attribute class itself. The
  first parameter takes the member type the attribute applies to (or a base type, an interface, or
  `object`); the rest mirror the constructor, positionally and by type.
- **Null passes.** The generated guard skips the check for a null member, like every constraint
  except `[Required]` — declare `[Required]` beside it when absence should fail. A nullable value
  type arrives unwrapped: `int?` member, `int` parameter.
- **`Code`, `Message`, `When` and `Unless`** work exactly as they do on the built-ins — they live
  on the shared base. The default message is a terse `"{field} is invalid."` and the default code
  is [`custom`](/reference/codes); setting `Message` is the recommendation, not an edge case.
- **Anything else arrives through the constructor.** A custom init-only property has no path into
  a static method, and setting one is [VM0082](/reference/diagnostics#vm0082) — an error naming
  the fix — rather than an argument that silently never arrives. The same error covers every other
  wrong shape: a missing or non-static `IsValid`, a first parameter that cannot accept the member,
  parameters that do not line up with the constructor.

## Against the alternatives

| | declaration | cost per check | mistakes surface |
|---|---|---|---|
| `CustomConstraintAttribute` | attribute on the model | a branch | at build time (VM0082) |
| [Rule class `Ensure`](/guide/rule-classes) | predicate beside the model | a branch | at build time |
| [Custom `ValidationAttribute`](/guide/data-annotations#custom-rules-are-invoked) | attribute on the model | a `ValidationContext` per check, boxing | at run time |

A rule class is the answer when the logic belongs to one model or spans two properties. This is
the answer when the check is *reusable vocabulary* — a SKU, a slug, an IBAN — that many models
declare and one place defines. A custom DataAnnotations attribute remains the migration path for
attributes you already have; this is what you write when you get to choose.

The check must be expressible as a static method over statically renderable arguments. That is
the trade that buys build-time verification, and it is not much of one: a check needing services
or I/O was never a structural constraint — it is an
[`IAsyncValidatorFor<T>`](/guide/async).
