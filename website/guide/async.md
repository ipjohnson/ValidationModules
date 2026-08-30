# Async and business rules

Structural validation is generated: is this string present, is this number in range. Business rules
that need I/O are hand-written, and they implement a different interface:

```csharp
namespace ValidationModules;

public interface IAsyncValidatorFor<in T> {
    ValueTask ValidateAsync(
        ValidationContext context,
        T value,
        CancellationToken cancellationToken = default);
}
```

<!-- verify:models -->
```csharp
public sealed class PetUniquenessValidator : IAsyncValidatorFor<Pet> {
    private readonly IPetRepository _pets;

    public PetUniquenessValidator(IPetRepository pets) => _pets = pets;

    public async ValueTask ValidateAsync(
        ValidationContext context, Pet value, CancellationToken cancellationToken = default) {

        if (await _pets.ExistsAsync(value.Sku!, cancellationToken)) {
            context.Report("sku", "duplicate", "sku is already in use.");
        }
    }
}

public interface IPetRepository {
    ValueTask<bool> ExistsAsync(string sku, CancellationToken cancellationToken);
}
```

<!-- verify:models -->
```csharp
var services = new ServiceCollection();

services.AddScoped<IAsyncValidatorFor<Pet>, PetUniquenessValidator>();

public sealed class PetUniquenessValidator : IAsyncValidatorFor<Pet> {
    public ValueTask ValidateAsync(
        ValidationContext context, Pet value, CancellationToken cancellationToken = default) => default;
}
```

Scoped rather than singleton, because unlike generated validators these take dependencies.

## One context, both sides

The sync side takes `ref ValidationContext`; the async side takes it **by value**. It has to:
`ref` parameters are illegal on `async` methods.

That works because `ValidationContext` is a `readonly struct` rather than a `ref struct`, which is
the most consequential decision in the library. A context is seven words: the collector reference plus
the compact path it sits at. Copying it is free, and both copies address the same collector, so a
merged run produces one ordered error list with one path vocabulary regardless of which side found
each error.

The practical consequence is that a context is **valid for the life of its collector**. Across
awaits, inside closures, in any order:

```csharp
public async ValueTask ValidateAsync(
    ValidationContext context, Order value, CancellationToken cancellationToken) {

    var lines = context.Push("lines");                       // fine to hold

    for (var i = 0; i < value.Lines.Count; i++) {
        var line = context.PushIndex("lines", i);
        var stock = await _inventory.LevelAsync(value.Lines[i].Sku, cancellationToken);

        if (stock < value.Lines[i].Quantity) {               // still correct after the await
            line.Report("quantity", "insufficient_stock", "not enough stock.");
        }
    }
}
```

An earlier design indexed into shared storage by depth. It was wrong in a way that only showed up
under concurrency: two sibling contexts at the same depth overwrote each other's segment, so a
context parked on an `await` reported whichever sibling wrote last, which is precisely what a
`Task.WhenAll` over collection elements does. The path now lives entirely inside the copied struct,
so `Push` writes nothing any other context can observe.

## Fanning out

A validation pass is single-threaded. Its contexts share one depth-indexed path buffer, so two
branches descending at the same time would overwrite each other's segments.

To validate in parallel, give each branch its own collector and merge the results:

```csharp
var results = await Task.WhenAll(batch.Select(item => Task.Run(() => validator.Validate(item))));
```

That is also faster than sharing one collector under a lock, which is why the synchronized collector
was removed rather than kept.

::: tip Getting it wrong fails loudly
Reusing a context after another descent has overwritten its path throws
`InvalidOperationException` rather than reporting the error under the wrong field. Each path segment
carries a monotonic stamp, checked when an error is recorded, so a clean pass pays nothing for it.
:::

Note also that a validator which fans out internally gives up deterministic error ordering for its
own errors, which land in completion order. That is your choice to make, and the ordering guarantee
across *validators* still holds.

## Running both together

`ValidationRunner<T>` composes them:

```csharp
public class PetService {
    private readonly ValidationRunner<Pet> _validation;

    public PetService(ValidationRunner<Pet> validation) => _validation = validation;

    public async Task CreateAsync(Pet pet, CancellationToken cancellationToken) {
        var result = await _validation.ValidateAsync(pet, cancellationToken: cancellationToken);

        if (!result.IsValid) {
            throw new ValidationException(result);
        }

        // …
    }
}
```

Two policies live there so that consumers do not each reimplement them slightly differently:

**Structural first, and async only if structural passed.** A uniqueness check should not reach the
database for a field that was null. This is a gate on the whole pass, not per field.

**All results merge; nothing replaces anything.** Structural constraints must not silently disappear
because someone added a business rule, and merging removes the precedence question entirely.

Business rules are awaited **sequentially**, so error ordering across validators stays deterministic.

## Reporting on the object itself

Not every rule belongs to a field. `ReportHere` reports against the current object:

```csharp
if (value.Start > value.End) {
    context.ReportHere("date_order", "start must not be after end.");
}
```

At the top level that produces an error with an empty field; inside a nested object it carries the
path that got you there.

For a cross-field rule that does *not* need I/O, prefer
[`rules.Ensure`](/guide/rule-classes#ensure). It compiles to a branch, and its message is the
predicate itself.

## Choosing where a rule goes

| Rule | Where |
|---|---|
| present, length, range, pattern, membership, element count | a [constraint attribute](/guide/constraints) |
| a comparison between two properties of the same object | [`rules.Ensure`](/guide/rule-classes#ensure) |
| anything needing the model's own methods, no I/O | [`rules.Apply`](/guide/rule-classes) |
| anything needing a database, an HTTP call, or a scoped service | `IAsyncValidatorFor<T>` |

The gradient is deliberate. Everything above the last row is compiled to straight-line code with no
allocation on the success path; the last row is where you accept a cost because the rule genuinely
requires it.
