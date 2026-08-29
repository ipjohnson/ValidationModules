# Rule builder API

The surface of `ValidationRules<T>`, passed to `IValidationRulesFor<T>.Describe`. See
[Rule classes](/guide/rule-classes) for how it is used and what the body may contain.

## Two classes to copy

The everyday shape — anchored chains, one rule per line, the whole class:

```csharp
using ValidationModules;

public sealed class PetRules : IValidationRulesFor<Pet> {
    public void Describe(ValidationRules<Pet> rules) {
        rules.Required(x => x.Name).Length(1, 100);
        rules.Range(x => x.Age, 0, 30);
        rules.AllowedValues(x => x.Status, "available", "pending", "sold");
        rules.Count(x => x.Toys, 1, 10).Each();
        rules.Nested(x => x.Home);
    }
}
```

And the shape attributes cannot reach — cross-field facts, conditions, your own codes and
messages:

```csharp
using ValidationModules;

public sealed class BookingRules : IValidationRulesFor<Booking> {
    public void Describe(ValidationRules<Booking> rules) {
        rules.Required(x => x.Reference).Length(8, 8);

        rules.When(x => x.IsRecurring, () => {
            rules.RangeAtLeast(x => x.Occurrences, 2);
        });

        rules.Ensure(x => x.Start < x.End,
            code: "window_inverted",
            message: "the booking must start before it ends");

        rules.Ensure(x => x.Deposit <= x.Total * 0.5m, code: "deposit_too_large");
    }
}
```

Both compile to the same straight-line validator the attributes produce — same codes, same
messages, same ordering. The rest of this page is the full surface, for when you need the exact
signature.

## The surface

```csharp
public sealed class ValidationRules<T> {
    public PropertyRules<T, TValue> For<TValue>(Func<T, TValue> value, string? field = null, …);

    public PropertyRules<T, string?> Required(Func<T, string?> value, …);
    public PropertyRules<T, TValue?> Required<TValue>(Func<T, TValue?> value, …);
    public PropertyRules<T, string?> RequiredAllowingEmpty(Func<T, string?> value, …);

    public PropertyRules<T, string?> Length(Func<T, string?> value, int min = 0, int max = int.MaxValue, …);
    public PropertyRules<T, TValue?> Range<TValue>(Func<T, TValue?> value, TValue min, TValue max, …);
    public PropertyRules<T, TValue?> RangeAtLeast<TValue>(Func<T, TValue?> value, TValue min, …);
    public PropertyRules<T, TValue?> RangeAtMost<TValue>(Func<T, TValue?> value, TValue max, …);
    public PropertyRules<T, string?> Pattern(Func<T, string?> value, Func<Regex> pattern, …);
    public PropertyRules<T, IReadOnlyList<TElement>?> Count<TElement>(Func<T, …> value, int min, int max, …);
    public PropertyRules<T, IEnumerable<TElement>?> Unique<TElement>(Func<T, …> value, …);

    public PropertyRules<T, long?>    MultipleOf(Func<T, long?> value, long divisor, …);
    public PropertyRules<T, decimal?> MultipleOf(Func<T, decimal?> value, decimal divisor, …);
    public PropertyRules<T, double?>  MultipleOf(Func<T, double?> value, double divisor, …);

    public PropertyRules<T, TValue?> Nested<TValue>(Func<T, TValue?> value, …);
    public PropertyRules<T, IReadOnlyList<TElement>?> Each<TElement>(Func<T, …> value, …);

    public ValidationRules<T> Ensure(Func<T, bool> predicate, string? field = null, string? code = null,
        string? message = null, ValidationSeverity severity = ValidationSeverity.Error, …);

    public ValidationRules<T> Apply(RuleAction<T> rule);
}
```

Every method taking a selector also takes `field`, `code`, `message` and `severity`, plus a
compiler-supplied `[CallerArgumentExpression]` parameter you never pass.

## Selectors

`Func<T, TValue>`, never `Expression<Func<T, TValue>>`. An expression tree would need compiling to be
executable, and `Expression.Compile` is banned — so the field name comes from
`CallerArgumentExpression` on the selector's source text instead.

```csharp
rules.Required(x => x.Name);                 // field "name"
rules.Required(x => x.Name, field: "petName");
```

The selector must be a plain property path. Anything else is
[VM0071](/reference/diagnostics#vm0071).

## Anchored chaining

The first call carries the selector; the rest inherit it via `PropertyRules<T, TValue>`.

```csharp
rules.Required(x => x.Name).Length(1, 100);
rules.For(x => x.Name).Required().Length(1, 100);   // same thing, anchor stated
```

`PropertyRules<T, TValue>` repeats the same vocabulary without the selector.

Members that only make sense for particular value types are **extension methods constrained on the
receiver's type argument**, which is how `Length` is offered on `PropertyRules<T, string?>` and not
on `PropertyRules<T, int>`. An instance method cannot be constrained that way, and the alternative
is a run-time check for something the compiler should catch.

## The vocabulary

| Method | Attribute equivalent | Code |
|---|---|---|
| `Required(x => x.Name)` | `[Required]` | `required` |
| `RequiredAllowingEmpty(x => x.Note)` | `[Required(AllowEmptyStrings = true)]` | `required` |
| `Length(x => x.Name, 1, 100)` | `[StringLength(1, 100)]` | `string_length` |
| `Range(x => x.Age, 0, 30)` | `[Range(0, 30)]` | `range` |
| `Pattern(x => x.Sku, Patterns.Sku)` | `[Pattern(typeof(Patterns), "Sku")]` | `pattern` |
| `AllowedValues(x => x.Status, "a", "b")` | `[AllowedValues("a", "b")]` | `enum` |
| `RangeAtLeast(x => x.Qty, 1)` | `[Range(Min = 1)]` | `range` |
| `RangeAtMost(x => x.Qty, 99)` | `[Range(Max = 99)]` | `range` |
| `Count(x => x.Toys, 1, 10)` | `[ItemCount(1, 10)]` | `array_bounds` |
| `MultipleOf(x => x.Qty, 5)` | `[MultipleOf(5)]` | `multiple_of` |
| `Unique(x => x.Codes)` | `[UniqueItems]` | `unique_items` |
| `Nested(x => x.Home)` | `[ValidateNested]` | — |
| `Each(x => x.Toys)` | `[ValidateNested]` on a collection | — |

A rule declared here and the same rule declared as an attribute are the same model before the
emitter sees either, so codes, messages and ordering match exactly.

`Range<TValue>` is constrained `where TValue : IComparable<TValue>, IFormattable` — which is what
makes it work for `DateOnly` and `decimal` where the
[`[Range]` string overload does not](/reference/diagnostics#vm0065).

`RangeAtLeast` and `RangeAtMost` are separate methods rather than an optional bound on `Range`. A
nullable bound parameter costs the type inference that lets `Range(x => x.Age, 0, 120)` be written
without naming `TValue`, and naming it at every call site is worse than one extra method.

`MultipleOf` resolves on the divisor's own type: `5` picks the integral overload, `0.05m` the
decimal one and `0.01` the double one. The double overload checks in the decimal domain, the same
as the emitted path — see [the guide](/guide/constraints#multipleof).

`Unique` takes an `IEnumerable<TElement>` where `Count` takes an `IReadOnlyList<TElement>`, because
uniqueness enumerates rather than reading a count. A set-typed or enumerable-only property is
declarable here where a count is not.

## `Ensure`

```csharp
rules.Ensure(x => x.Start < x.End);
rules.Ensure(x => x.Discount <= x.Price * 0.5m, code: "discount_too_large");
```

The exit from the vocabulary: cross-field comparisons, arithmetic, anything the six constraints
cannot say.

**The message is the predicate, rendered** — the lambda parameter stripped, member accesses given
their wire names, a period appended. `x => x.Start < x.End` produces `start < end.`

Two constraints, both build errors:

- the predicate may capture only its own parameter and static or constant state —
  [VM0072](/reference/diagnostics#vm0072);
- the predicate must read some property of its parameter —
  [VM0075](/reference/diagnostics#vm0075), which `field:` does **not** satisfy.

The code defaults to `predicate` and does not derive from the expression. See
[Error codes](/reference/codes#why-ensure-does-not-derive-its-code).

## `When` and `Unless`

Two shapes: chained onto a statement, and opening a block.

```csharp
// Chained: guards every constraint this statement declared.
rules.Required(x => x.Reason).Length(max: 500).When(x => x.Expedited);

// Block: guards everything its body declares.
rules.When(x => x.IsAuto, () => {
    rules.Required(x => x.PlateNumber);
}).Otherwise(() => {
    rules.Required(x => x.Notes);
});
```

Arity is what separates them — one argument terminates a statement, two open a block.

`Unless` is the negated form of each. `Otherwise` reuses its block's own condition negated rather
than taking a second predicate, so the two halves cannot drift apart.

Nested blocks conjoin, with no depth limit. A chained `When` written inside a block means both.

### Scope is the statement

**A chained `When` conditions every constraint declared in the statement it terminates**, and
nothing past the semicolon. To guard less, write two statements:

```csharp
rules.Required(x => x.Reason);                              // always
rules.For(x => x.Reason).Length(max: 500)
     .When(x => x.Expedited);                               // only when expedited
```

::: tip Porting from FluentValidation
FluentValidation's `.When()` defaults to `ApplyConditionTo.AllValidators` — it applies to every
validator in the chain, including ones written before it — and takes a parameter to opt out of that.
Scoping to the statement gives the same result for the common case without the default, so there is
nothing to opt out of and no parameter.

A validator that used `ApplyConditionTo.CurrentValidator` has to become two statements. That is a
mechanical edit the compiler does not catch, so it is worth grepping for. Every other conditional
spelling ports across unchanged; `WhenAsync`/`UnlessAsync` and `DependentRules` have no counterpart
here.
:::

### Once per pass

A condition is evaluated **once per validation pass**, not once per rule that names it. Conditions
may read live static state, so the two are different results rather than two spellings of one, and
both engines owe the same one:

- the generated validator hoists each distinct condition into a local above the method body;
- `DescribedValidator<T>` evaluates them into a stack-allocated span before testing any rule.

A guarded clean pass allocates what an unguarded one does, which is nothing.

One consequence: hoisting means an inner condition runs even when the block it sits in is false, so
`x => x.Auto.Wheels > 0` nested under `x => x.Auto != null` will throw rather than short-circuit.
Write the null check into the inner condition.

### Rules

- A condition is vetted by the same self-containment check an `Ensure` predicate is —
  [VM0072](/reference/diagnostics#vm0072) — and lifted the same way, so a `private` member of the
  rules class is [VM0078](/reference/diagnostics#vm0078). Non-private members are qualified
  automatically.
- It must be a lambda. A method group has no body to lift and is
  [VM0070](/reference/diagnostics#vm0070) rather than a condition that silently always holds.
- A condition that folds to a constant is [VM0034](/reference/diagnostics#vm0034).
- An empty block is [VM0076](/reference/diagnostics#vm0076); a chained `When` covering no rules is
  [VM0077](/reference/diagnostics#vm0077).

A guarded `Required` suppresses the rest of its field only when it actually runs. With the condition
false it records nothing, so it suppresses nothing.

## `Apply`

```csharp
public delegate void RuleAction<in T>(ref ValidationContext context, T value);
```

```csharp
rules.Apply(PetChecks.SkuChecksum);
```

Taken as a method group rather than a `(Type, string)` pair. An attribute has to spell a member
reference that way because it cannot hold a method group; a method body can, so this gets
compile-time checking, go-to-definition and rename for free. The generator emits a direct call.

## `DescribedValidator<T>`

The runtime engine. Constructed for you by `AddDescribedValidator<T, TRules>()`, or directly:

```csharp
var validator = new DescribedValidator<Pet>(new PetRules(), validatorProvider, fieldNamer);
```

Calls `Describe` once in its constructor and walks the rules it recorded — an interface dispatch and
a delegate call per rule, against a branch for the generated path. Both build the rule graph once.

`validatorProvider` is only needed by a declaration that descends into a nested type; `fieldNamer`
defaults to camelCase. Both are optional.

::: warning Do not register both engines for one type
If this generator compiled the rules class it already registered the validator it emitted. Adding
`AddDescribedValidator` as well gives the type two validators, and `ValidationRunner<T>` merges every
registered one — so every error appears twice.
:::
