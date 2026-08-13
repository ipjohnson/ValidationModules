# Rule builder API

The surface of `ValidationRules<T>`, passed to `IValidationRulesFor<T>.Describe`. See
[Rule classes](/guide/rule-classes) for how it is used and what the body may contain.

```csharp
public sealed class ValidationRules<T> {
    public PropertyRules<T, TValue> For<TValue>(Func<T, TValue> value, string? field = null, …);

    public PropertyRules<T, string?> Required(Func<T, string?> value, …);
    public PropertyRules<T, TValue?> Required<TValue>(Func<T, TValue?> value, …);
    public PropertyRules<T, string?> RequiredAllowingEmpty(Func<T, string?> value, …);

    public PropertyRules<T, string?> Length(Func<T, string?> value, int min = 0, int max = int.MaxValue, …);
    public PropertyRules<T, TValue?> Range<TValue>(Func<T, TValue?> value, TValue min, TValue max, …);
    public PropertyRules<T, string?> Pattern(Func<T, string?> value, Func<Regex> pattern, …);
    public PropertyRules<T, IReadOnlyList<TElement>?> Count<TElement>(Func<T, …> value, int min, int max, …);

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
| `Count(x => x.Toys, 1, 10)` | `[ItemCount(1, 10)]` | `array_bounds` |
| `Nested(x => x.Home)` | `[ValidateNested]` | — |
| `Each(x => x.Toys)` | `[ValidateNested]` on a collection | — |

A rule declared here and the same rule declared as an attribute are the same model before the
emitter sees either, so codes, messages and ordering match exactly.

`Range<TValue>` is constrained `where TValue : IComparable<TValue>, IFormattable` — which is what
makes it work for `DateOnly` and `decimal` where the
[`[Range]` string overload does not](/reference/diagnostics#vm0065).

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
