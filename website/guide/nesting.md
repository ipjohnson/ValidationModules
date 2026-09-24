# Nested objects and collections

`[ValidateNested]` validates the object that a property holds, or every element of a collection,
with the validators for that object's type. Their errors are reported under the property's path.

## Objects

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed class Order
{
    [Required]
    public string? Reference { get; init; }

    [ValidateNested]
    public Address? ShipTo { get; init; }
}

public sealed class Address
{
    [Required]
    public string? Postcode { get; init; }
}
```

An order whose address has no postcode reports the field `shipTo.postcode` with the message
`postcode is required.` The message names the last segment of the path, and `Field` holds the
whole path.

A `null` property is skipped. Add `[Required]` to the property when the object must be present.

The nested type needs rules of its own: constraint attributes, a rules class, or
`[GenerateValidator]`. When it has none, the generator reports `VM1501` and drops the descent.

## Collections

On a list, an array, or any other `IEnumerable<T>`, `[ValidateNested]` validates every element. The
path includes the element's index:

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed class Order
{
    [ItemCount(1, 50), ValidateNested]
    public List<Line> Lines { get; init; } = [];
}

public sealed class Line
{
    [Required]
    public string? Sku { get; init; }

    [Range(1, 99)]
    public int Quantity { get; init; }
}
```

The second line without a SKU reports `lines[1].sku`. `null` elements are skipped. `[ItemCount]` and
`[UniqueItems]` check the collection itself, so they combine with `[ValidateNested]` on one
property.

Attributes cannot constrain the elements of a collection of simple values. `[StringLength]` on a
`List<string>` is reported as `VM1001`. Use `Each` in a [rules
class](./rule-classes#nested-objects-and-collections) instead:

```csharp
rules.Each(x.Tags).Length(2, 20);
```

## Dictionaries

On a dictionary, `[ValidateNested]` validates every value, and the path includes the key:

```csharp
[ValidateNested]
public Dictionary<string, Address> Addresses { get; init; } = [];
```

A value under the key `work` with no postcode reports `addresses[work].postcode`. Keys are not
validated.

## Subtypes

When the nested type can have subtypes, a property of that type may hold a more derived object at
run time. The generator reports `VM1503` for a nested type that is not sealed until you say how to
handle this, with an argument to `[ValidateNested]`:

| `Polymorphism` | Validators that run |
| --- | --- |
| `DeclaredOnly` | Only the validators for the declared type. Rules declared on a subtype do not run. |
| `CompileTime` | The validators for the object's actual type, chosen from the subtypes declared in the same project. |
| `Runtime` | The validators registered in the container for the object's actual type, looked up during validation. |

Sealing the nested type also removes the warning.

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public abstract class Payment
{
    [Required]
    public string? Currency { get; init; }
}

public sealed class Card : Payment
{
    [StringLength(16, 16)]
    public string? Number { get; init; }
}

public sealed class Checkout
{
    [ValidateNested(Polymorphism.CompileTime)]
    public Payment? Payment { get; init; }
}
```

A `Checkout` whose payment is a `Card` with a three-digit number reports `payment.number`, because
`CompileTime` runs the `Card` validator. With `DeclaredOnly` it would run only the `Payment`
validator and report nothing.

`CompileTime` writes a `switch` over the subtypes it can see, so it needs nothing at run time. It
cannot see subtypes declared in other assemblies, and it uses the generated validators only.

`Runtime` resolves the validators through the container for every value it visits. It sees subtypes
from any assembly and runs every validator registered for them, including hand-written ones. The
validation pass must carry an `IServiceProvider`, so validate through `ValidationRunner<T>`
resolved from a scope. `validator.Validate(value)` has no services and throws an
`InvalidOperationException` that says so. The generated registration method registers what
`Runtime` needs: an `IDynamicValidator` for each validated type and a `DynamicValidatorRegistry`
that finds them by type. The generated code calls `DynamicValidation` to do the lookup. You do not
write or call any of these types yourself.

`Runtime` on a sealed type or a value type is reported as `VM1504`, because the actual type can
never differ from the declared type.

## Conditions

`When` and `Unless` work on `[ValidateNested]` as on any constraint, and decide whether the
descent happens:

```csharp
public bool IsAuto { get; init; }

[ValidateNested(When = nameof(IsAuto))]
public AutoDetail? Auto { get; init; }
```

## Recursive types

A type can contain itself, directly or through other types, as in a tree of categories. The
generator handles the cycle in the type graph. An object graph that contains a cycle at run time,
such as a node that is its own child, stops at the depth limit of 64 levels with an
`InvalidOperationException` rather than a stack overflow.

## Hand-written validators for nested types

A validator resolved from the container runs every `IValidatorFor<T>` registered for a nested type,
including hand-written ones. A validator created with `new` runs only the generated validators for
its nested types. [Registration](./registration#hand-written-validators) shows how to add a
hand-written validator.

## Paths

Paths through more than two nested objects are shortened by default, as in
`lines[1]...location.latitude`. Pass `ValidationPathMode.Full` to keep every segment.
[Results and errors](./errors#paths) describes both modes.
