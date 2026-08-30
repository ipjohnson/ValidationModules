# Rule builder API

The surface of `ValidationRules<T>`, passed to `IValidationRulesFor<T>.Describe`. See
[Rule classes](/guide/rule-classes) for the model. The body is **read at build time and never
run**. This page covers what that body may contain.

## Two classes to copy

The everyday shape is anchored chains, one rule per line, the whole class:

```csharp
using ValidationModules;

public sealed class PetRules : IValidationRulesFor<Pet> {
    public static void Describe(ValidationRules<Pet> rules, Pet x) {
        rules.Require(x.Name).Length(1, 100);
        rules.Range(x.Age, 0, 30);
        rules.AllowedValues(x.Status, ["available", "pending", "sold"]);
        rules.Count(x.Toys, 1, 10).Each();
        rules.Nested(x.Home);
    }
}
```

And the shape attributes cannot reach: cross-field facts, conditions, computation, your own codes
and messages:

```csharp
using ValidationModules;

public sealed class BookingRules : IValidationRulesFor<Booking> {
    public static void Describe(ValidationRules<Booking> rules, Booking x) {
        rules.Require(x.Reference).Length(8, 8);

        if (x.IsRecurring) {
            rules.RangeAtLeast(x.Occurrences, 2);
        }

        rules.Ensure(x.Start < x.End,
            code: "window_inverted",
            message: "the booking must start before it ends");

        var half = x.Total * 0.5m;
        rules.Ensure(x.Deposit <= half, code: "deposit_too_large");
    }
}
```

Both expand into the same straight-line checks the attributes produce, with the same codes and
messages,
emitted into a companion region the validator calls. The rest of this page is the full surface, for
when you need the exact signature.

## The surface

```csharp
public interface IValidationRulesFor<T> {
    static abstract void Describe(ValidationRules<T> rules, T x);
}

public sealed class ValidationRules<T> {
    public IValidationContextReporter Context { get; }

    public PropertyRules<T, TValue> For<TValue>(TValue value, string? field = null);

    public PropertyRules<T, string?> Require(string? value, string? field = null);
    public PropertyRules<T, TValue?> Require<TValue>(TValue? value, string? field = null);
    public PropertyRules<T, string?> RequireAllowingEmpty(string? value, string? field = null);

    public PropertyRules<T, string?> Length(string? value, int min = 0, int max = int.MaxValue, string? field = null);
    public PropertyRules<T, TValue?> Range<TValue>(TValue? value, TValue min, TValue max, string? field = null);
    public PropertyRules<T, TValue?> RangeAtLeast<TValue>(TValue? value, TValue min, string? field = null);
    public PropertyRules<T, TValue?> RangeAtMost<TValue>(TValue? value, TValue max, string? field = null);
    public PropertyRules<T, string?> Pattern(string? value, Func<Regex> pattern, string? field = null);
    public PropertyRules<T, TValue>  AllowedValues<TValue>(TValue value, TValue[] allowed, string? field = null);
    public PropertyRules<T, IReadOnlyList<TElement>?> Count<TElement>(IReadOnlyList<TElement>? value, int min = 0, int max = int.MaxValue, string? field = null);
    public PropertyRules<T, IEnumerable<TElement>?>   Unique<TElement>(IEnumerable<TElement>? value, string? field = null);

    public PropertyRules<T, long?>    MultipleOf(long? value, long divisor, string? field = null);
    public PropertyRules<T, decimal?> MultipleOf(decimal? value, decimal divisor, string? field = null);
    public PropertyRules<T, double?>  MultipleOf(double? value, double divisor, string? field = null);

    public PropertyRules<T, TValue?> Nested<TValue>(TValue? value, string? field = null);
    public PropertyRules<T, IReadOnlyList<TElement>?> Each<TElement>(IReadOnlyList<TElement>? value, string? field = null);

    public ValidationRules<T> Ensure(bool condition, string? field = null, string? code = null,
        string? message = null, ValidationSeverity severity = ValidationSeverity.Error);

    public ValidationRules<T> As<TFacet>(TFacet value);

    public ValidationRules<T> Apply(RuleAction<T> rule);
}
```

Arguments are **values** rather than selectors, as in `rules.Require(x.Name)`. The generator
resolves the
argument as a symbol; nothing here executes. The builder is inert by construction: its constructor
is internal, its members throw, and nothing ever calls `Describe`.

A class may implement the interface once per type it describes, with one `Describe` overload each,
implicit or explicit. Every target gets its own validator. See
[One class, several targets](/guide/rule-classes#one-class-several-targets).

## Values and field names

An island's value must be a member path on the subject parameter. Nested paths and `?.` are the
same spelling:

```csharp
rules.Require(x.Name);                 // field "name"
rules.Require(x.Home?.PostalCode);     // field "home.postalCode"
rules.Require(x.Name, field: "petName");
```

`[JsonPropertyName]` on the property wins, then the naming policy. An explicit `field:` is a raw
wire name, not put through the namer. Anything that is not a member path is
[VM0071](/reference/diagnostics#vm0071) unless `field:` is given.

## Anchored chaining

The first call carries the value; the rest inherit its anchor via `PropertyRules<T, TValue>`,
which repeats the same vocabulary without the value:

```csharp
rules.Require(x.Name).Length(1, 100);
rules.For(x.Name).Require().Length(1, 100);   // same thing, anchor stated
```

A chain is one statement, and one `if`/`else if` ladder in the region: **a failed `Require`
suppresses the rest of its own chain**. Separate statements against one field report
independently, so rules for one field belong in one chain.

Members that only make sense for particular value types are **extension methods constrained on the
chain's type argument**, which is how `Length` is offered on a string anchor and not on an `int`.

## The vocabulary

| Method | Attribute equivalent | Code |
|---|---|---|
| `Require(x.Name)` | `[Required]` | `required` |
| `RequireAllowingEmpty(x.Note)` | `[Required(AllowEmptyStrings = true)]` | `required` |
| `Length(x.Name, 1, 100)` | `[StringLength(1, 100)]` | `string_length` |
| `Range(x.Age, 0, 30)` | `[Range(0, 30)]` | `range` |
| `Pattern(x.Sku, Patterns.Sku)` | `[Pattern(typeof(Patterns), "Sku")]` | `pattern` |
| `AllowedValues(x.Status, ["a", "b"])` | `[AllowedValues("a", "b")]` | `enum` |
| `RangeAtLeast(x.Qty, 1)` | `[Range(Min = 1)]` | `range` |
| `RangeAtMost(x.Qty, 99)` | `[Range(Max = 99)]` | `range` |
| `Count(x.Toys, 1, 10)` | `[ItemCount(1, 10)]` | `array_bounds` |
| `MultipleOf(x.Qty, 5)` | `[MultipleOf(5)]` | `multiple_of` |
| `Unique(x.Codes)` | `[UniqueItems]` | `unique_items` |
| `Nested(x.Home)` | `[ValidateNested]` | — |
| `Each(x.Toys)` | `[ValidateNested]` on a collection | — |

A rule declared here and the same rule declared as an attribute expand through one check writer,
so codes, messages and check shapes match exactly.

`Range<TValue>` is constrained `where TValue : IComparable<TValue>, IFormattable`, which is what
makes it work for `DateOnly` and `decimal` where the
[`[Range]` string overload does not](/reference/diagnostics#vm0065).

`RangeAtLeast` and `RangeAtMost` are separate methods rather than an optional bound on `Range`. A
nullable bound parameter costs the type inference that lets `Range(x.Age, 0, 120)` be written
without naming `TValue`, and naming it at every call site is worse than one extra method.

`MultipleOf` resolves on the divisor's own type: `5` picks the integral overload, `0.05m` the
decimal one and `0.01` the double one. The double overload checks in the decimal domain, the same
as the attribute path. See [the guide](/guide/constraints#multipleof).

`Unique` takes an `IEnumerable<TElement>` where `Count` takes an `IReadOnlyList<TElement>`, because
uniqueness enumerates rather than reading a count.

`Require` on a non-nullable value type cannot be written bare, because inference will not unwrap
`Nullable`. With an explicit type argument it is [VM0090](/reference/diagnostics#vm0090).

## Conditions are C#

There is no `When`/`Unless`. Write `if`/`else`; conditions evaluate where written, at validation
time, inside the region:

```csharp
if (x.IsExpedited) {
    rules.Require(x.Reason).Length(2, 500);
}

if (x.IsAuto) {
    rules.Require(x.PlateNumber);
} else {
    rules.Require(x.Notes);
}
```

A guarded `Require` that does not run records nothing, so it suppresses nothing.

::: tip Porting from FluentValidation
`.When()` becomes the `if` you would have written anyway. `ApplyConditionTo` has no counterpart
because there is no retroactive default to opt out of. The brace says exactly what is guarded.
`WhenAsync`/`UnlessAsync` and `DependentRules` still have no counterpart; async checks are
`IAsyncValidatorFor<T>`.
:::

## `Ensure`

```csharp
rules.Ensure(x.Start < x.End);
rules.Ensure(x.Discount <= x.Price * 0.5m, code: "discount_too_large");
```

One assertion with no vocabulary name. **The message is the condition, rendered**: the subject
parameter stripped, member accesses wire-named, a period appended: `start < end.` Locals appear
under their own names, so `var total = …; rules.Ensure(total <= x.CreditLimit);` reads
`total <= creditLimit.`

The rule anchors to the first property the condition reads; a condition that reads none needs
`field:`, or it is [VM0075](/reference/diagnostics#vm0075). The code defaults to `predicate` and
does not derive from the expression. See
[Error codes](/reference/codes#why-ensure-does-not-derive-its-code).

## `rules.Context`: the reporter tier

Free-form logic reports through a narrow view of the pass, typed `IValidationContextReporter`:

```csharp
if (!Luhn.Validates(x.AccountNumber)) {
    rules.Context.Report(nameof(x.AccountNumber), "checksum",
        "account number failed its checksum");
}
```

`Report`, `ReportHere`, and every `Report*` extension. Legal anywhere in the body, loops included;
any expression-statement whose type is `ValidationFlow` is checked and propagated automatically.
`nameof` through the subject parameter rewrites to the wire path. See
[the guide](/guide/rule-classes#reporter).

## Fragments

Any `static`, `void`, same-compilation method receiving the builder is followed and expanded,
decomposition and reuse as method extraction, generics included:

```csharp
public static void Standard<T>(ValidationRules<T> rules, T audited) where T : IAudited {
    rules.Require(audited.CreatedBy);
    rules.RangeAtLeast(audited.Version, 1);
}
```

See [the guide](/guide/rule-classes#fragments) for the rules: same compilation
([VM0085](/reference/diagnostics#vm0085)), subject argument, cycles
([VM0086](/reference/diagnostics#vm0086)).

## `As<TFacet>`

```csharp
rules.As<IAudited>(x);   // validate x as its IAudited facet
```

Validates the subject as one of its facets. This is the route when shared rules ship as compiled
IL. A
facet generated in this compilation binds statically (no rules for it is
[VM0091](/reference/diagnostics#vm0091)); a facet from a referenced assembly resolves the closed
`IValidatorFor<TFacet>` through the pass's services, and a missing registration throws naming the
module to compose. The path does not push. Declare facet rules in a rules class targeting the
facet, rather than as attributes on it. See [the guide](/guide/rule-classes#facets).

## `Apply`

```csharp
public delegate ValidationFlow RuleAction<in T>(ref ValidationContext context, T value);
```

```csharp
rules.Apply(PetChecks.SkuChecksum);
```

Taken as a method group rather than a `(Type, string)` pair, and emitted as a direct call. Applied
rules run after everything else, unconditionally, in declaration order, so an `Apply` under an `if`
is [VM0070](/reference/diagnostics#vm0070).
