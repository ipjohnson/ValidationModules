# Nesting and collections

`[ValidateNested]` tells the emitter to descend into a property. What descending means depends on
the property's shape, and the emitter decides which of three readings applies at build time.

```csharp
[ValidateNested] public Address? Home              { get; init; }   // an object
[ValidateNested] public List<Toy> Toys             { get; init; }   // a collection
[ValidateNested] public Dictionary<string, Toy> ByName { get; init; }   // a dictionary
```

## Objects

```csharp
public record Pet {
    [ValidateNested]
    public Address? Home { get; init; }
}
```

```csharp
if (value.Home is { } nestedHome) {
    var ctxHome = ctx.Push("home");
    AddressValidator.Instance.Validate(ref ctxHome, nestedHome);
}
```

Errors from inside `Address` arrive pathed: a failed `[Required]` on `PostalCode` reports
`home.postalCode`.

Two properties of that emitted code carry weight:

- **The nested validator is referenced statically**, never injected. That is what keeps generated
  validators parameterless and registration free of constructor reflection.
- **A null `Home` is skipped rather than reported.** Whether the property may be absent is
  `[Required]`'s question, not `[ValidateNested]`'s. Declare both if it is both:

```csharp
[Required]
[ValidateNested]
public Address? Home { get; init; }
```

With both, a null `Home` reports `home: required` and does *not* also report every required field
inside `Address`. Suppression is a whole-path match, so `home` failing does not silence
`home.postalCode` in general — the emitter simply does not descend into a value that is not there.

## Collections

```csharp
public record Pet {
    [ItemCount(min: 1, max: 10)]
    [ValidateNested]
    public List<Toy> Toys { get; init; } = [];
}
```

```csharp
if (value.Toys is not null && (value.Toys.Count < 1 || value.Toys.Count > 10))
    ctx.AddItemCount("toys", 1, 10);

if (value.Toys is { } itemsToys) {
    for (var iToys = 0; iToys < itemsToys.Count; iToys++) {
        var element = itemsToys[iToys];
        if (element is not null) {
            var elementCtx = ctx.PushIndex("toys", iToys);
            global::Sample.ToyValidator.Instance.Validate(ref elementCtx, element);
        }
    }
}
```

Element errors read `toys[3].name`. The two constraints are independent: `[ItemCount]` checks how
many, `[ValidateNested]` checks each one.

### Indexed rather than enumerated, when it can be

The `for` loop over an indexer is not a micro-optimization. `foreach` over an interface-typed
collection calls `IEnumerable<T>.GetEnumerator()`, which **boxes `List<T>`'s struct enumerator** — so
a clean pass over a collection property would allocate, which is the one thing the runtime promises
it does not do.

The emitter picks a `for` loop when the type is an array, or has both an indexer and a count, or
implements `IList<T>`/`IReadOnlyList<T>`. Otherwise it falls back to `foreach`:

```csharp
[ValidateNested] public IEnumerable<Toy> Toys { get; init; }
```

```csharp
if (value.Toys is { } itemsToys) {
    var iToys = 0;
    foreach (var element in itemsToys) {
        if (element is not null) {
            var elementCtx = ctx.PushIndex("toys", iToys);
            global::Sample.ToyValidator.Instance.Validate(ref elementCtx, element);
        }
        iToys++;
    }
}
```

Correct either way; the indexed form is the one that allocates nothing. Prefer `IReadOnlyList<T>`
over `IEnumerable<T>` on a validated model if you care.

Null elements are skipped rather than reported in both forms — an element's own `[Required]` is
about its properties, and there is no field name to hang "this element was null" on.

## Dictionaries

```csharp
[ValidateNested] public Dictionary<string, Toy> ToysByName { get; init; } = new();
```

```csharp
if (value.ToysByName is { } entriesToysByName) {
    foreach (var pair in entriesToysByName) {
        if (pair.Value is not null) {
            var entryCtx = ctx.PushKey("toysByName", pair.Key?.ToString() ?? "");
            global::Sample.ToyValidator.Instance.Validate(ref entryCtx, pair.Value);
        }
    }
}
```

The **values** are validated and the **key** becomes the path segment, so an error reads
`toysByName[favourite].name`. Keys of any type are stringified; the key is never itself validated.

Dictionaries are checked *before* the collection reading, and that ordering is load-bearing: every
`IDictionary<K,V>` is also an `IEnumerable<KeyValuePair<K,V>>`, and taking that reading emitted a
call to a `KeyValuePairValidator` that does not exist and never could — which broke the consumer's
build inside generated code.

::: warning Dictionary keys are user data
A key goes into the field path verbatim. If the keys come from a request body, they reach your logs
and your error responses, which is both a PII question and an unbounded log-cardinality one. The
redaction policy that addresses this is designed but not yet implemented; until then, treat a
dictionary keyed by user input the way you would treat logging that input.
:::

## Collections of collections

`[ValidateNested]` descends one level per property. A `List<List<Toy>>` validates the outer list's
elements as `List<Toy>` — which has no validator, so nothing happens. Model the inner list as a
property of a type instead:

```csharp
public record Shelf {
    [ValidateNested] public List<Toy> Toys { get; init; } = [];
}

public record Room {
    [ValidateNested] public List<Shelf> Shelves { get; init; } = [];
}
```

Errors then read `shelves[2].toys[0].name` — or rather, they would if the path were kept in full.
Which brings us to the one surprise on this page.

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
descents. Elision may omit; it may not lie — rendering `toys.owner.name` for what is really
`toys[3].owner.name` would assert an object at `toys` that does not exist, so both retained segments
keep their own index or key.

Why: most real data is depth 0–2, which renders in full anyway; long messages need truncating
somewhere, and this controls where; and the alternative designs all existed solely to reconstruct
ancestry that is not reported. Dropping the requirement made `Push` a struct copy with no allocation
and no shared state at any depth or element count.

**What you give up.** An index on an ancestor that is neither the outermost nor the parent —
`body.order.lines[3].address.postalCode` reports `body...address.postalCode`, losing the row number.
That needs three or more descents *and* an object between the element and the failing field. And an
index on the outermost segment itself, when a bare collection is validated at the very top.

There is no root name and nothing is synthesized. `body` is not special — it is an ordinary property
that got pushed like any other, and it appears only because something pushed it. A path or query
parameter validated on its own is depth 0 and renders bare: `id`, `page`.

## Cycles

A model that references itself will recurse until the stack runs out. There is no cycle detector —
it would cost something on every descent to catch a shape that is rare in a request body, which is
what this library validates.

If you model a tree, bound it yourself: leave the recursive property without `[ValidateNested]` and
validate the levels you care about explicitly, or check depth in an
[`IAsyncValidatorFor<T>`](/guide/async).

## Types with no rules of their own

`[ValidateNested]` on a type that declares no constraints validates nothing, because no validator
was generated for it. That is [VM0007](/reference/diagnostics#vm0007) — declared, and one of the
three diagnostics that are **never reported**, so the situation is currently silent.

If the nested type gets its rules from a [rule class](/guide/rule-classes), or you want it walked
for the sake of *its* nested properties, mark it:

```csharp
[GenerateValidator]
public record Address {
    // no constraints here; rules arrive from AddressRules
}
```
