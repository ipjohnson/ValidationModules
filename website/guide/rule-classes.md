# Rule classes

A third way to declare rules, alongside [constraint attributes](/guide/constraints) and
[DataAnnotations](/guide/data-annotations): a class whose body is **read at build time and never
run**.

```csharp
using ValidationModules;

public sealed class PetRules : IValidationRulesFor<Pet> {
    public static void Describe(ValidationRules<Pet> rules, Pet x) {
        rules.Require(x.Name).Length(1, 100);
        rules.Range(x.Age, 0, 30);
        rules.Pattern(x.Sku, PetPatterns.Sku);
        rules.Count(x.Toys, 1, 10).Each();

        rules.Ensure(x.Age is not 13, code: "unlucky");
    }
}
```

A version of that you can paste, against the guide's `Pet`:

<!-- verify:models -->
```csharp
public sealed class PetRules : IValidationRulesFor<Pet> {
    public static void Describe(ValidationRules<Pet> rules, Pet x) {
        rules.Require(x.Name).Length(1, 100);
        rules.Range(x.Age, 0, 30);
        rules.Count(x.Toys, 1, 10);
    }
}
```

It exists for two cases attributes cannot reach:

- **`Pet` comes from a package nobody here owns.** You cannot edit the model to add an attribute.
- **A rule is not a per-property fact.** A cross-field comparison, a computed total, a checksum —
  no per-property attribute can say them.

Nothing needs registering. The generator finds the class and emits its checks into the same
validator the attributes produce.

## Read, never run

The source generator **transcribes** `Describe` into the generated validator. Vocabulary calls —
`Require`, `Length`, `Ensure` — are recognized islands it expands into check-and-report code.
Every other statement — locals, arithmetic, `if`/`else` — is carried through and runs at validation
time *inside the generated validator*. There is no runtime engine and no interpretation.

Three consequences worth knowing up front:

- **Nothing instantiates a rules class.** `Describe` is `static`, so `this` does not compile, and
  the builder it takes cannot be constructed — the method exists to be read. Under trimming and
  Native AOT the class disappears entirely: what ships is the generated validator.
- **`x` is symbolic.** It never holds a value at declaration time; it exists so member access
  typechecks, renames propagate, and go-to-definition works.
- **A breakpoint in `Describe` never hits.** The method is read, not run. To step through your
  rules, step through the generated validator and its `{RulesClass}_Rules` companion under
  `obj/…/generated` — readable, straight-line code.

## Control flow is just C#

There is no `When`/`Unless`. Conditions are `if`/`else`, evaluated where written, at validation
time:

<!-- verify:models -->
```csharp
public sealed class SeniorPetRules : IValidationRulesFor<Pet> {
    public static void Describe(ValidationRules<Pet> rules, Pet x) {
        if (x.Age > 20) {
            rules.Require(x.Home);
        } else {
            rules.Range(x.Age, 0, 20);
        }
    }
}
```

Computation is the feature, not a violation. Declare locals, call helpers, and feed the results
into rules:

```csharp
var total = x.Lines?.Sum(l => l.Price * l.Qty) ?? 0m;
rules.Ensure(total <= x.CreditLimit);          // message: "total <= creditLimit."
```

::: warning Computation runs unguarded
Your statements execute exactly as written, on exactly the malformed inputs validation exists to
reject. `x.Lines.Sum(…)` throws when `Lines` is null even if a `Count` rule above it just reported
the problem — write `x.Lines?.Sum(…) ?? 0m`, the way you would anywhere else.

The same goes for I/O: a database call in `Describe` compiles and runs on every validation pass.
The line is convention now, not a build error — structural rules here, I/O in
`IAsyncValidatorFor<T>`.
:::

## The vocabulary

Arguments are values, not selectors — `rules.Require(x.Name)`, not `rules.Required(x => x.Name)`.
The first call in a chain carries the value; the rest inherit its anchor, and a failed `Require`
suppresses the checks chained after it:

<!-- verify:models -->
```csharp
public sealed class AnchoredRules : IValidationRulesFor<Pet> {
    public static void Describe(ValidationRules<Pet> rules, Pet x) {
        rules.Require(x.Name).Length(1, 100);
    }
}
```

The vocabulary mirrors the attributes and produces the same codes and messages:

| Builder | Attribute | Code |
|---|---|---|
| `rules.Require(x.Name)` | `[Required]` | `required` |
| `.Length(1, 100)` | `[StringLength(1, 100)]` | `string_length` |
| `rules.Range(x.Age, 0, 30)` | `[Range(0, 30)]` | `range` |
| `rules.Pattern(x.Sku, Patterns.Sku)` | `[Pattern(…)]` | `pattern` |
| `rules.AllowedValues(x.Status, ["a", "b"])` | `[AllowedValues("a", "b")]` | `enum` |
| `rules.Count(x.Toys, 1, 10)` | `[ItemCount(1, 10)]` | `array_bounds` |
| `rules.Nested(x.Home)` | `[ValidateNested]` | — |
| `rules.Each(x.Toys)` | `[ValidateNested]` on a collection | — |

Members that only make sense for particular value types are extension methods constrained on the
chain's type argument — which is how `Length` is offered on a string anchor and not on an `int`.
The compiler catches the mistake rather than a runtime check.

::: tip `Pattern` takes a method group
`rules.Pattern(x.Sku, PetPatterns.Sku)` — the accessor for a `[GeneratedRegex]` partial method,
never an inline string. There is no inline form to leak the regex engine into an AOT publish.
:::

## Field names

An island's value must be a member path on the subject — nested paths and `?.` included:

```csharp
rules.Require(x.Home?.PostalCode);   // field "home.postalCode"
rules.Require(x.Name, field: "petName");
```

`[JsonPropertyName]` on the property wins, then the
[naming policy](/reference/msbuild#validationmodules-fieldnaming). An explicit `field:` is a raw
wire name on your head — it is not put through the namer. Anything that is not a member path needs
one, or it is [VM0071](/reference/diagnostics#vm0071).

Where free-form code needs a field name, `nameof` through the subject parameter rewrites to the
wire path — including inside interpolated strings:

```csharp
rules.Context.Report(nameof(x.AccountNumber), "checksum",   // → "accountNumber"
    $"{nameof(x.AccountNumber)} failed its checksum");
```

`nameof(Pet.Name)` — through the type — stays ordinary C# and yields the CLR name; that is the
deliberate escape hatch.

## `Ensure` {#ensure}

One assertion with no vocabulary name:

```csharp
rules.Ensure(x.Start < x.End);
rules.Ensure(x.Discount <= x.Price * 0.5m, code: "discount_too_large");
```

**The message is the condition, rendered**: the subject parameter stripped, member accesses
wire-named, a period appended — `start < end.` It cannot drift, because it *is* the rule, and it is
redaction-safe by construction: the text is compile-time source, so no runtime value can reach it.
Locals appear under their own names (`total <= creditLimit.`), which makes local naming part of
your user-facing text — a feature, once you know.

The rule anchors to the first member access off the subject; a condition that reads none needs
`field:`, or it is [VM0075](/reference/diagnostics#vm0075). The code defaults to `predicate` and
never derives from the expression — the message should track the rule, while the code is a wire
contract that widening a bound must not break.

## The reporter tier {#reporter}

When free-form logic finds something, report it through `rules.Context` — a narrow view of the
validation pass with exactly the members that work here:

```csharp
if (!Luhn.Validates(x.AccountNumber)) {
    rules.Context.Report(nameof(x.AccountNumber), "checksum",
        "account number failed its checksum");
}
```

- Reporter calls are transcription, not islands — legal anywhere, **loops included**. Per-element
  reporting uses a computed field string: `rules.Context.Report($"lines[{i}].sku", …)`.
- Any expression-statement whose type is `ValidationFlow` is checked and propagated automatically —
  the built-in `Report*` helpers, future ones, and your own helpers alike. Assign the result to
  opt into manual control.
- Values may reach messages here. The composed vocabulary and `Ensure` are redaction-safe by
  construction; `Report` deliberately reopens that, at an explicit call site, on your head.

## Fragments {#fragments}

Decomposition and reuse are method extraction, read by the generator: any `static`, `void`,
same-compilation method that receives the builder is followed.

```csharp
public static class AuditRules {
    // The mixin the attributes never had: every audited type gets these rules, said once.
    public static void Standard<T>(ValidationRules<T> rules, T audited) where T : IAudited {
        rules.Require(audited.CreatedBy);
        rules.RangeAtLeast(audited.Version, 1);
    }
}

public sealed class OrderRules : IValidationRulesFor<Order> {
    public static void Describe(ValidationRules<Order> rules, Order x) {
        rules.Require(x.Number);
        AuditRules.Standard(rules, x);          // expanded here, in body order
    }
}
```

- **Generic fragments are stamped out per concrete type**, and members resolve against it —
  `[JsonPropertyName]` on the implementing property wins for field naming.
- **Extra parameters bind at the call site**: `CustomsRules.Declare(rules, x, strict: x.Tier > 2)`.
- The parameter typed as the subject must be passed the `Describe` subject — a facet of a child is
  `Nested`'s territory. Fragments may call fragments; a cycle is
  [VM0086](/reference/diagnostics#vm0086), not a hang.
- Each fragment expands into a companion method carrying **its own file's** using directives, so it
  compiles exactly as written where it was written.

::: warning Fragments travel as source
A fragment is read from syntax, and a referenced assembly ships IL — there is no body to read, so a
plain `ProjectReference` is on the wrong side of the line
([VM0085](/reference/diagnostics#vm0085)). Share fragments through a shared project or a
source-only package.
:::

## `Apply` — a hand-written rule

For a shared opaque check that wants the raw context:

```csharp
rules.Apply(PetChecks.SkuChecksum);
```

Taken as a method group and emitted as a direct call. Applied rules own no position: they run after
everything else, unconditionally, in declaration order — which is why an `Apply` under an `if` is
an error rather than a promise the ordering cannot keep.

## Ordering

The generated validator runs its regions in a fixed order: the attribute-declared checks first (in
source order), then one region per rules class — classes ordered by name, statements in body order,
because the body *is* the validator. `Apply` rules run last.

Rules for one field belong in one chain. Two separate statements against the same field report
independently — a failed `Require` suppresses the rest of *its own* chain, nothing more.

## What is rejected

Almost everything transcribes. What does not, and why:

- **The builder flowing where the reader cannot follow** —
  [VM0087](/reference/diagnostics#vm0087): storing `rules` or a chain in a local, capturing it in a
  lambda, passing it to anything that is not a fragment. A rule call the generator cannot see would
  validate nothing, so it is an error instead.
- **A member the companion file cannot reach** — [VM0088](/reference/diagnostics#vm0088):
  `private` members of the rules class. Make them `internal`; a `private const` is carried across
  by value.
- **Islands in loops, lambdas, or local functions** — [VM0089](/reference/diagnostics#vm0089).
  Collections are `Each`'s job; the reporter tier covers the exotic per-element case.
- **The v1-rejected exotica** — [VM0070](/reference/diagnostics#vm0070): `goto`, `try`/`catch`,
  `lock`, `using` statements, assignment to the subject.
