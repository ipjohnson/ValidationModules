# Nesting and collections

`[ValidateNested]` tells the emitter to descend into a property. What descending means depends on
the property's shape, and the emitter decides which of three readings applies at build time.

```csharp
[ValidateNested]
public Address? Home { get; init; } // an object

[ValidateNested]
public List<Toy> Toys { get; init; } // a collection

[ValidateNested]
public Dictionary<string, Toy> ByName { get; init; } // a dictionary
```

## Objects

```csharp
public sealed record Pet {
    [ValidateNested]
    public Address? Home { get; init; }
}
```

```csharp
if (value.Home is { } nestedHome) {
    var ctxHome = ctx.Push("home");
    validatorsHome[vi].Validate(ref ctxHome, nestedHome);
}
```

Errors from inside `Address` arrive pathed: a failed `[Required]` on `PostalCode` reports
`home.postalCode`.

Two properties of that emitted code carry weight:

- **The generated nested validator is referenced statically**, never injected. That is what keeps
  generated validators parameterless and registration free of constructor reflection. Any *other*
  validator registered for `Address` is picked up separately. See
  [nested types compose too](/guide/registration#nested-types-compose-too).
- **A null `Home` is skipped rather than reported.** Whether the property may be absent is
  `[Required]`'s question, not `[ValidateNested]`'s. Declare both if it is both:

```csharp
[Required]
[ValidateNested]
public Address? Home { get; init; }
```

With both, a null `Home` reports `home: required` and does *not* also report every required field
inside `Address`. Suppression is a whole-path match, so `home` failing does not silence
`home.postalCode` in general. The emitter simply does not descend into a value that is not there.

## Collections

```csharp
public sealed record Pet {
    [ItemCount(min: 1, max: 10)]
    [ValidateNested]
    public List<Toy> Toys { get; init; } = [];
}
```

```csharp
if (value.Toys is not null && (value.Toys.Count < 1 || value.Toys.Count > 10))
    ctx.ReportItemCount("toys", 1, 10);

if (value.Toys is { } itemsToys) {
    for (var iToys = 0; iToys < itemsToys.Count; iToys++) {
        var element = itemsToys[iToys];
        if (element is not null) {
            var elementCtx = ctx.PushIndex("toys", iToys);
            elementValidators[vi].Validate(ref elementCtx, element);
        }
    }
}
```

Element errors read `toys[3].name`. The two constraints are independent: `[ItemCount]` checks how
many, `[ValidateNested]` checks each one.

### Indexed rather than enumerated, when it can be

The `for` loop over an indexer is not a micro-optimization. `foreach` over an interface-typed
collection calls `IEnumerable<T>.GetEnumerator()`, which **boxes `List<T>`'s struct enumerator**. A
clean pass over a collection property would then allocate, which is the one thing the runtime
promises it does not do.

The emitter picks a `for` loop when the type is an array, or has both an indexer and a count, or
implements `IList<T>`/`IReadOnlyList<T>`. Otherwise it falls back to `foreach`:

```csharp
[ValidateNested]
public IEnumerable<Toy> Toys { get; init; }
```

```csharp
if (value.Toys is { } itemsToys) {
    var iToys = 0;
    foreach (var element in itemsToys) {
        if (element is not null) {
            var elementCtx = ctx.PushIndex("toys", iToys);
            elementValidators[vi].Validate(ref elementCtx, element);
        }
        iToys++;
    }
}
```

Both forms are correct, and the indexed one allocates nothing. Prefer `IReadOnlyList<T>` over
`IEnumerable<T>` on a validated model if you care.

Null elements are skipped rather than reported in both forms. An element's own `[Required]` is about
its properties, and there is no field name to hang "this element was null" on.

## Dictionaries

```csharp
[ValidateNested]
public Dictionary<string, Toy> ToysByName { get; init; } = new();
```

```csharp
if (value.ToysByName is { } entriesToysByName) {
    foreach (var pair in entriesToysByName) {
        if (pair.Value is not null) {
            var entryCtx = ctx.PushKey("toysByName", pair.Key?.ToString() ?? "");
            entryValidators[vi].Validate(ref entryCtx, pair.Value);
        }
    }
}
```

The **values** are validated and the **key** becomes the path segment, so an error reads
`toysByName[favourite].name`. Keys of any type are stringified; the key is never itself validated.

Dictionaries are checked *before* the collection reading, and that ordering is load-bearing. Every
`IDictionary<K,V>` is also an `IEnumerable<KeyValuePair<K,V>>`. Taking that reading emitted a call
to a `KeyValuePairValidator` that does not exist and never could, which broke the consumer's build
inside generated code.

::: warning Dictionary keys are user data
A key goes into the field path verbatim. If the keys come from a request body, they reach your logs
and your error responses, which is both a PII question and an unbounded log-cardinality one. The
redaction policy that addresses this is designed but not yet implemented; until then, treat a
dictionary keyed by user input the way you would treat logging that input.
:::

## Collections of collections

`[ValidateNested]` descends one level per property. A `List<List<Toy>>` validates the outer list's
elements as `List<Toy>`, which has no validator, so nothing happens. Model the inner list as a
property of a type instead:

```csharp
public sealed record Shelf {
    [ValidateNested]
    public List<Toy> Toys { get; init; } = [];
}

public sealed record Room {
    [ValidateNested]
    public List<Shelf> Shelves { get; init; } = [];
}
```

Errors then read `shelves[2].toys[0].name`, at least when the path is kept in full. The next
section is the exception.

## Field paths are compact

A path keeps **the outermost segment, the immediate parent, and the field**. Anything between the
first two is elided:

| Real path | Descents | Reported |
|---|---|---|
| `id` | 0 | `id` |
| `body.email` | 1 | `body.email` |
| `body.lines[3].sku` | 2 | `body.lines[3].sku` |
| `body.order.address.postalCode` | 3 | `body...address.postalCode` |

The `...` marker appears **only when a segment was really dropped**, which takes three or more
descents. Elision may omit a segment but never misreport one. Rendering `toys.owner.name` for what
is really `toys[3].owner.name` would assert an object at `toys` that does not exist, so both
retained segments keep their own index or key.

There are three reasons for the trade. Most real data is depth 0 to 2, which renders in full anyway.
Long messages need truncating somewhere, and this controls where. The alternative designs all
existed to reconstruct ancestry that is never reported. Dropping that requirement made `Push` a
struct copy with no allocation and no shared state at any depth or element count.

**What you give up.** You lose an index on an ancestor that is neither the outermost nor the parent.
`body.order.lines[3].address.postalCode` reports `body...address.postalCode`, losing the row number.
That needs three or more descents *and* an object between the element and the failing field. You
also lose an index on the outermost segment itself, when a bare collection is validated at the very
top.

## Asking for the whole path

Bounded rendering is the default because it keeps a failing pass proportional to its failures rather
than its depth. Ask for the full path where that trade is wrong, which covers a manifest linter, a
config validator, a batch importer, and anything else where documents are deep and the reader needs
the exact row that failed:

```csharp
var collector = new ValidationErrorCollector(ValidationPathMode.Full);
validator.ValidateInto(collector, manifest);
```

| mode | `body.order.lines[3].address.postalCode` |
|---|---|
| `Bounded` (default) | `body...address.postalCode` |
| `Full` | `body.order.lines[3].address.postalCode` |

Every segment keeps its index or key either way, and `Full` simply stops dropping the middle ones.
It costs a longer string per error and nothing at all on a clean pass, because the path is only
rendered when an error is recorded.

There is no root name and nothing is synthesized. `body` is not special. It is an ordinary property
that got pushed like any other, and it appears only because something pushed it. A path or query
parameter validated on its own is depth 0 and renders bare, as `id` or `page`.

## Cycles and depth

A self-referential model is fine, and generation terminates either way. The emitter walks types
rather than values, so `Node` containing a `Node?` produces one validator that calls itself.

Validation of an actual cycle is guarded. Descending past **64 levels** throws
`InvalidOperationException` naming the path it got to:

```
Validation nested more than 64 levels deep at 'child...child.child'. That is the length of the
path buffer this pass was given. Either the object graph contains a cycle, or a deeper buffer
is needed.
```

This is a guard rather than a detector, on purpose. Tracking visited instances would cost an
allocation and a lookup on every descent, to catch a shape that is rare in a request body. The depth
counter is already in the context, so the check is a comparison against a field.

It throws rather than reporting an error for two reasons. A cycle is a bug in the caller's object
graph rather than invalid data. The alternative is a `StackOverflowException`, which cannot be
caught and takes the process down with it.

```csharp
var head = new MutableNode { Label = "head" };
head.Child = head;

new MutableNodeValidator().Validate(head);   // InvalidOperationException
```

Note that a `record` cannot hold a cycle, because `a with { Child = b }` copies, so this needs a
mutable type to reproduce. If you genuinely model a deep tree, leave the recursive property without
`[ValidateNested]` and validate the levels you care about explicitly.

## Types with no rules of their own

`[ValidateNested]` on a type that declares no constraints validates nothing, because no validator
was generated for it. That is [VM0007](/reference/diagnostics#vm0007), which is declared but is one
of the three diagnostics that are **never reported**, so the situation is currently silent.

If the nested type gets its rules from a [rule class](/guide/rule-classes), or you want it walked
for the sake of *its* nested properties, mark it:

```csharp
[GenerateValidator]
public sealed record Address {
    // no constraints here; rules arrive from AddressRules
}
```
