# Rule classes

A third way to declare rules, alongside [constraint attributes](/guide/constraints) and
[DataAnnotations](/guide/data-annotations): a class that describes them in a method body.

```csharp
using ValidationModules;

public sealed class PetRules : IValidationRulesFor<Pet> {
    public void Describe(ValidationRules<Pet> rules) {
        rules.Required(x => x.Name).Length(1, 100);
        rules.Range(x => x.Age, 0, 30);
        rules.Pattern(x => x.Sku, PetPatterns.Sku);
        rules.Count(x => x.Toys, 1, 10).Each();

        rules.Ensure(x => x.Start < x.End);
        rules.Apply(PetChecks.SkuChecksum);
    }
}
```

A version of that you can paste, against the guide's `Pet`:

<!-- verify:models -->
```csharp
public sealed class PetRules : IValidationRulesFor<Pet> {
    public void Describe(ValidationRules<Pet> rules) {
        rules.Required(x => x.Name).Length(1, 100);
        rules.Range(x => x.Age, 0, 30);
        rules.Count(x => x.Toys, 1, 10).Each();
    }
}
```

It exists for two cases attributes cannot reach:

- **`Pet` comes from a package nobody here owns.** You cannot edit the model to add an attribute.
- **A rule spans two properties.** `x.Start < x.End` is not a per-property fact, so no per-property
  attribute can say it.

Nothing needs registering if this generator compiles your project — it finds the class and folds
its rules into the same validator the attributes produce.

## The design: two consumers, one declaration

`Describe` is both **read at build time** and **run at run time**, and the two must agree.

| | reads or runs | cost per rule | needs this generator |
|---|---|---|---|
| `ValidatorEmitter` via `RulesFrontEnd` | reads the syntax, flattens to straight-line code | a branch | yes |
| `DescribedValidator<T>` | runs `Describe` once in its constructor | an interface dispatch and a delegate call | no |

So the interface is the portable contract and the generator is an optimizer that erases its cost.
That is what makes it usable by a *different* source generator: emit a rules class, register it, and
validation works with none of this package's build-time machinery present.

Either way, `Describe` runs at most once per process, in a singleton's constructor. "Rule graphs are
built once, never per validation call" holds on both paths by construction.

::: tip Selectors are `Func<T, TValue>`, never `Expression<Func<T, TValue>>`
An expression tree would need compiling to be executable, and `Expression.Compile` is banned. What
replaces it is `CallerArgumentExpression` — see [field inference](#field-names-are-inferred).
:::

## The body is a whitelisted DSL

This is the part most likely to surprise, and it is deliberate.

A `Describe` body is **not** general C#. A local, a loop, a condition, or a call to anything that is
not on the builder is a build error — [VM0070](/reference/diagnostics#vm0070) — rather than
something quietly dropped:

```csharp
public void Describe(ValidationRules<Pet> rules) {
    var minimum = 2;                                   // VM0070
    if (DateTime.Now.Year > 2000) { … }                // VM0070
    foreach (var name in Names) { … }                  // VM0070
    Helper();                                          // VM0070
}
```

Why it has to be an error rather than a warning: the body compiles and it *runs*. Under
`DescribedValidator<T>` that loop would work perfectly. If the generator quietly skipped what it
could not read, the same rules class would validate differently depending on which engine happened
to be running it — which is the one thing this design exists to prevent.

## Field names are inferred

The selector's source text supplies the field name, via `CallerArgumentExpression`:

```csharp
rules.Required(x => x.Name);      // field "name"
rules.Required(x => x.Name, field: "petName");
```

The selector must be a plain property path. Anything else is
[VM0071](/reference/diagnostics#vm0071), because there is no field to hang the error on:

```csharp
rules.Required(x => x.Name!.Trim());   // VM0071
rules.Range(x => x.Nights + 1, 1, 30); // VM0071
```

## Anchored chaining

The first call carries the selector; the rest inherit it.

<!-- verify:models -->
```csharp
public sealed class AnchoredRules : IValidationRulesFor<Pet> {
    public void Describe(ValidationRules<Pet> rules) {
        rules.Required(x => x.Name).Length(1, 100);
    }
}
```

`For` exists for when the anchor reads better stated on its own:

<!-- verify:models -->
```csharp
public sealed class ForRules : IValidationRulesFor<Pet> {
    public void Describe(ValidationRules<Pet> rules) {
        rules.For(x => x.Name).Required().Length(1, 100);
    }
}
```

The vocabulary mirrors the attributes, and produces the same codes and messages — a rule declared
here and the same rule declared as an attribute are the same model before the emitter sees either:

| Builder | Attribute | Code |
|---|---|---|
| `rules.Required(x => x.Name)` | `[Required]` | `required` |
| `.Length(1, 100)` | `[StringLength(1, 100)]` | `string_length` |
| `.Range(x => x.Age, 0, 30)` | `[Range(0, 30)]` | `range` |
| `.Pattern(x => x.Sku, Patterns.Sku)` | `[Pattern(…)]` | `pattern` |
| `.AllowedValues(x => x.Status, "a", "b")` | `[AllowedValues("a", "b")]` | `enum` |
| `.Count(x => x.Toys, 1, 10)` | `[ItemCount(1, 10)]` | `array_bounds` |
| `.Nested(x => x.Home)` | `[ValidateNested]` | — |
| `.Each()` | `[ValidateNested]` on a collection | — |

Members that only make sense for particular value types are extension methods constrained on the
receiver's type argument — which is how `Length` is offered on `PropertyRules<T, string?>` and not on
`PropertyRules<T, int>`. The compiler catches the mistake rather than a runtime check.

::: tip `Pattern` takes a method group
`rules.Pattern(x => x.Sku, PetPatterns.Sku)` — not a `(Type, string)` pair. An attribute has to spell
a member reference that way because it cannot hold a method group; a method body can, so you get
compile-time checking, go-to-definition and rename for free.
:::

## `Ensure` {#ensure}

The exit from the vocabulary: cross-field comparisons, arithmetic, anything the six constraints
cannot say.

```csharp
rules.Ensure(x => x.Start < x.End);
rules.Ensure(x => x.Discount <= x.Price * 0.5m, code: "discount_too_large");
```

**The message is the predicate, rendered.** `CallerArgumentExpression` supplies the source text, the
lambda parameter is stripped, and member accesses take their wire names:

| Written | Message |
|---|---|
| `x => x.Start < x.End` | `start < end.` |
| `x => x.Age is >= 0 and <= 30` | `age is >= 0 and <= 30.` |
| `x => !string.IsNullOrWhiteSpace(x.Name)` | `!string.IsNullOrWhiteSpace(name).` |

That message cannot drift, because it *is* the rule — where a composed message repeats a bound
someone can edit without editing the text. Both engines produce it identically, because both start
from the same string: the generator bakes a literal, the runtime renders once at rule-build time.

It is also redaction-safe by construction. The text is compile-time source, so no runtime value can
reach it.

::: tip An ugly render means the wrong tool
The last row above is a case the vocabulary has a word for — `Required` — and it is shorter. You do
not need to read a diagnostic to notice; the message tells you.
:::

### The code does not derive from the predicate

`Ensure` reports `predicate` unless you pass `code:`. Deriving a code from the expression — slug or
hash — was rejected because message and code have opposite churn requirements. The message is
human-facing and *should* track the rule. The code is a wire contract, so deriving it would make
widening `30` to `35` a breaking change for every client switching on it.

Two `Ensure`s on one field both report `predicate`, told apart by their messages. Pass `code:` when a
client needs to tell them apart programmatically, which promotes that one rule into your contract
deliberately rather than by accident.

### Two constraints on `Ensure`

**The predicate may capture only its own parameter, and static or constant state.**

```csharp
private readonly int _limit = 7;

rules.Ensure(x => x.Nights <= _limit);      // VM0072 — captures the rules instance
rules.Ensure(x => x.Nights <= Limit);       // fine, if Limit is const or static
```

The generator lifts a predicate into a static method; the runtime holds it as a delegate. A delegate
can close over the rules class instance and a static method cannot, so a capture is the one
construct that would genuinely compile on one path and not the other.

**The predicate must read some property of its parameter**, so the rule has somewhere to live:

```csharp
rules.Ensure(x => true);                    // VM0075
rules.Ensure(x => true, field: "nights");   // VM0075 as well
```

::: warning `field:` renames; it does not anchor
Passing `field:` does **not** silence [VM0075](/reference/diagnostics#vm0075). A rule is emitted
inside its anchored property's chain so both engines agree on ordering, and a rule belonging to no
property has nowhere to go.

This is also the one place the two engines legitimately diverge: `DescribedValidator<T>` accepts
`rules.Ensure(x => true, field: "nights")` and runs it; the generator rejects it. The generated path
is the stricter of the two, which is the safe direction — the build fails rather than two
deployments disagreeing.
:::

## `Apply` — a hand-written rule

For anything the DSL cannot express at all:

```csharp
public static class PetChecks {
    public static void SkuChecksum(ref ValidationContext context, Pet value) {
        if (value.Sku is { } sku && !ChecksumIsValid(sku)) {
            context.Add("sku", "checksum", "sku checksum does not match.");
        }
    }
}
```

```csharp
rules.Apply(PetChecks.SkuChecksum);
```

Taken as a method group rather than a `(Type, string)` pair, for the same reason `Pattern` is. The
generator emits a direct call to it.

## Ordering

Rules from a rules class land **after** the attributes on each property, and both live in one
validator rather than two.

For a type that carries no attributes of its own, properties report in the order the `Describe` body
first mentioned them — a body constraining `Notes` before `Start` reports in that order. It has to:
`DescribedValidator<T>` has only the body to go on and cannot see source order without reflection.

Where the type *does* carry attributes, source order stays authoritative. Mixing the two orderings on
one type would be worse than either.

## Running without this generator

If your project does not run `ValidationModules.SourceGenerator` — the rules class came from a
referenced assembly, or another generator emitted it — register it to be run:

```csharp
services.AddDescribedValidator<Pet, PetRules>();
```

`DescribedValidator<Pet>` calls `Describe` once in its constructor and walks the rules it recorded.
Costs an interface dispatch and a delegate call per rule instead of a branch, and needs no build-time
machinery at all.

::: danger Never do both for one type
If this generator compiled the class it also registered the validator it emitted. Calling
`AddDescribedValidator` as well gives the type two validators, and `ValidationRunner<T>` merges every
registered one — so every error appears twice.
:::

## Where the two engines diverge

They agree on codes, messages, ordering and field paths. Two exceptions, both known:

1. **`[JsonPropertyName]` field naming.** The generator reads it and bakes the JSON name in;
   `DescribedValidator<T>` cannot, because reading an attribute at run time is reflection.
2. **`Ensure` with no property access**, above.

If both matter to you, avoid `[JsonPropertyName]` on types validated by a rules class that might run
un-compiled, and set
[`ValidationModules_FieldNaming`](/reference/msbuild#validationmodules-fieldnaming) to match the
namer you register.
