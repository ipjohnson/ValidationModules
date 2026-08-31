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
- **A rule is not a per-property fact.** A cross-field comparison, a computed total, or a checksum.
  No per-property attribute can say them.

Nothing needs registering. The generator finds the class and emits its checks into the same
validator the attributes produce.

## Read, never run

The source generator **transcribes** `Describe` into the generated validator. Vocabulary calls such
as `Require`, `Length`, and `Ensure` are recognized islands, and it expands them into
check-and-report code. Every other statement, including locals, arithmetic, and `if`/`else`, is
carried through and runs at validation time *inside the generated validator*. There is no runtime
engine and no interpretation.

Three consequences worth knowing up front:

- **Nothing instantiates a rules class.** `Describe` is `static`, so `this` does not compile, and
  the builder it takes cannot be constructed. The method exists to be read. Under trimming and
  Native AOT the class disappears entirely, and what ships is the generated validator.
- **`x` is symbolic.** It never holds a value at declaration time; it exists so member access
  typechecks, renames propagate, and go-to-definition works.
- **A breakpoint in `Describe` never hits.** The method is read, not run. To step through your
  rules, step through the generated validator and its `{RulesClass}_Rules` companion under
  `obj/…/generated`. It is readable, straight-line code.

## One class, several targets

A rules class may implement `IValidationRulesFor<T>` once per type it describes, with one
`Describe` overload each. Every target still gets its own validator. What the class shares is its
members:

```csharp
public sealed class LedgerRules :
    IValidationRulesFor<Invoice>,
    IValidationRulesFor<CreditNote> {

    private const int NumberLength = 10;                  // one declaration, both regions

    public static void Describe(ValidationRules<Invoice> rules, Invoice x) {
        rules.Require(x.Number).Length(NumberLength, NumberLength);
    }

    public static void Describe(ValidationRules<CreditNote> rules, CreditNote x) {
        rules.Require(x.Number).Length(NumberLength, NumberLength);
        rules.Ensure(x.Amount > 0m, code: "positive");
    }
}
```

Each `Describe` is paired with its interface through the implementation rather than by position, so
declaration order never matters. An explicitly implemented
`static void IValidationRulesFor<Invoice>.Describe(…)` works too.

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
reject. `x.Lines.Sum(…)` throws when `Lines` is null, even if a `Count` rule above it just reported
the problem. Write `x.Lines?.Sum(…) ?? 0m`, the way you would anywhere else.

The same goes for I/O. A database call in `Describe` compiles and runs on every validation pass. The
line is convention rather than a build error: structural rules here, I/O in
`IAsyncValidatorFor<T>`.
:::

## The vocabulary

Arguments are values rather than selectors. Write `rules.Require(x.Name)`, not
`rules.Required(x => x.Name)`. The first call in a chain carries the value and the rest inherit its
anchor. A failed `Require` suppresses the checks chained after it:

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
| `rules.Each(x.Steps).Length(5, 500)` | — | `string_length`, per element |

Members that only make sense for particular value types are extension methods constrained on the
chain's type argument. That is how `Length` is offered on a string anchor and not on an `int`, and
it is why the compiler catches the mistake instead of a runtime check.

`Each` has two shapes, resolved by the element type. On a collection of objects it descends into
the element type's own validator, the way `[ValidateNested]` does. On a collection of **strings**
there is no element validator to descend into, so `Each` anchors the element itself and the rules
chained after it expand into an indexed loop:

<!-- verify -->
```csharp
public sealed record Procedure {
    public List<string> Steps { get; init; } = [];
}

public sealed class ProcedureRules : IValidationRulesFor<Procedure> {
    public static void Describe(ValidationRules<Procedure> rules, Procedure x) {
        rules.Count(x.Steps, 1, 30).Each().Length(5, 500);
    }
}
```

A two-character step fails at `steps[0]` with code `string_length`, exactly as `[StringLength]`
reports - the collection rule and the element rules are one statement and one suppression unit.
Null elements are skipped, as a nested walk skips them. Elements of other primitive types still
go through the [reporter tier](/guide/rule-classes#reporter).

A nullable member is passed as itself, and plain literal bounds convert to the member's type:

<!-- verify -->
```csharp
public sealed record Vehicle {
    public double Latitude { get; init; }
    public decimal? BatteryKwh { get; init; }
}

public sealed class VehicleRules : IValidationRulesFor<Vehicle> {
    public static void Describe(ValidationRules<Vehicle> rules, Vehicle x) {
        rules.Range(x.Latitude, -90, 90);      // infers double from the member
        rules.Range(x.BatteryKwh, 10, 300);    // null passes; Require is the presence check
    }
}
```

Every rule takes the nullable directly, so `x.BatteryKwh.Value` is never needed - writing it is
[VM0093](/reference/diagnostics#vm0093), and the reader compiles the rule against the member
itself. The one literal rule is C#'s own: fractional bounds on a `decimal` member need the `m`
suffix (`0.5m`), because `double` does not convert implicitly to `decimal`. See
[the rules API](/reference/rules-api#the-vocabulary) for the overload pair behind this.

::: tip `Pattern` takes a method group
`rules.Pattern(x.Sku, PetPatterns.Sku)` takes the accessor for a `[GeneratedRegex]` partial method,
never an inline string. There is no inline form to leak the regex engine into an AOT publish.
:::

## Field names

An island's value must be a member path on the subject. Nested paths and `?.` are included:

```csharp
rules.Require(x.Home?.PostalCode);   // field "home.postalCode"
rules.Require(x.Name, field: "petName");
```

`[JsonPropertyName]` on the property wins, then the
[naming policy](/reference/msbuild#validationmodules-fieldnaming). An explicit `field:` is a raw
wire name and is not put through the namer, so it is yours to get right. Anything that is not a
member path needs one, or it is [VM0071](/reference/diagnostics#vm0071).

Where free-form code needs a field name, `nameof` through the subject parameter rewrites to the wire
path, including inside interpolated strings:

```csharp
rules.Context.Report(nameof(x.AccountNumber), "checksum",   // → "accountNumber"
    $"{nameof(x.AccountNumber)} failed its checksum");
```

`field: nameof(x.AccountNumber)` follows the same rule: it names a member, so the error takes
that member's wire name, and one property cannot reach a client under two casings depending on
which spelling reported it. `nameof(Pet.Name)`, through the type rather than the subject, stays
ordinary C# and yields the CLR name. That is the escape hatch.

## `Ensure` {#ensure}

One assertion with no vocabulary name:

```csharp
rules.Ensure(x.Start < x.End);
rules.Ensure(x.Discount <= x.Price * 0.5m, code: "discount_too_large");
```

**The message is the condition, rendered.** The subject parameter is stripped, member accesses are
wire-named, and a period is appended, which gives `start < end.` It cannot drift from the rule,
because it is the rule. It is also redaction-safe by construction: the text is compile-time source,
so no runtime value can reach it. Locals appear under their own names, as in `total <= creditLimit.`,
which makes local naming part of your user-facing text.

The rule anchors to the first member access off the subject. A condition that reads none needs
`field:`, or it is [VM0075](/reference/diagnostics#vm0075).

**The code is derived from the same render**, so `x.Start < x.End` reports
`start_less_than_end` and two `Ensure`s on one field are told apart by a client as well as by a
reader. [VM0092](/reference/diagnostics#vm0092) states the derived code at the rule, since it is the
one part of a rules class you cannot read off the source. Pass `code:` to pin one rule against a
later change to its condition. [Error codes](/reference/codes#why-ensure-derives-its-code) has the
reasoning and the operator spellings.

## The reporter tier {#reporter}

When free-form logic finds something, report it through `rules.Context`, a narrow view of the
validation pass carrying exactly the members that work here:

```csharp
if (!Luhn.Validates(x.AccountNumber)) {
    rules.Context.Report(nameof(x.AccountNumber), "checksum",
        "account number failed its checksum");
}
```

- Reporter calls are transcription rather than islands, so they are legal anywhere, **loops
  included**. Per-element reporting uses a computed field string:
  `rules.Context.Report($"lines[{i}].sku", …)`.
- Any expression-statement whose type is `ValidationFlow` is checked and propagated automatically.
  That covers the built-in `Report*` helpers, future ones, and your own alike. Assign the result to
  opt into manual control.
- Values may reach messages here. The composed vocabulary and `Ensure` are redaction-safe by
  construction. `Report` reopens that on purpose, at an explicit call site.

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

- **Generic fragments are stamped out per concrete type**, and members resolve against it, so
  `[JsonPropertyName]` on the implementing property wins for field naming.
- **Extra parameters bind at the call site**: `CustomsRules.Declare(rules, x, strict: x.Tier > 2)`.
- The parameter typed as the subject must be passed the `Describe` subject. A facet of a child is
  `Nested`'s territory. Fragments may call fragments, and a cycle is
  [VM0086](/reference/diagnostics#vm0086) rather than a hang.
- Each fragment expands into a companion method carrying **its own file's** using directives, so it
  compiles exactly as written where it was written.

::: warning Fragments travel as source
A fragment is read from syntax, and a referenced assembly ships IL. There is no body to read, so a
plain `ProjectReference` is on the wrong side of the line
([VM0085](/reference/diagnostics#vm0085)). Share fragments through a shared project or a source-only
package, or ship the rules as a compiled facet, below.
:::

## Facets: `rules.As<TFacet>` {#facets}

The route when shared rules ship as IL, and the general spelling for "validate `x` as one of its
facets":

```csharp
// The shared assembly declares the facet and its rules, and runs the generator itself:
public interface IAudited {
    string? CreatedBy { get; }
}

public sealed class AuditRules : IValidationRulesFor<IAudited> {
    public static void Describe(ValidationRules<IAudited> rules, IAudited x) {
        rules.Require(x.CreatedBy);
    }
}

// Consumers opt in, in the body:
rules.As<IAudited>(x);
```

One spelling, two bindings. A facet whose validator is generated in **this** compilation binds
statically, with no DI involved, and a facet with no rules here is
[VM0091](/reference/diagnostics#vm0091) rather than a silent no-op. A facet from a **referenced**
assembly resolves the closed `IValidatorFor<TFacet>` through the pass's services. Compose the
facet's own `Add…Validators()` at your root. A missing registration throws and names exactly that,
rather than silently skipping.

The argument must be the subject. A facet of a child is `Nested`'s territory, where the path pushes.
Here it does not, so facet fields report at the current level: `createdBy`, not
`audited.createdBy`.

::: tip Declare facet rules in a rules class, not as attributes on the interface
An interface's *attribute* constraints already reach every implementer through
[constraint inheritance](/guide/constraints), so an `As` on top of those reports every facet error
twice. `As` exists for the rules inheritance cannot see, which is a rules class targeting the
facet.
:::

## `Apply`: a hand-written rule

For a shared opaque check that wants the raw context:

```csharp
rules.Apply(PetChecks.SkuChecksum);
```

Taken as a method group and emitted as a direct call. Applied rules own no position. They run after
everything else, unconditionally, in declaration order, which is why an `Apply` under an `if` is an
error rather than a promise the ordering cannot keep.

## Ordering

The generated validator runs its regions in a fixed order. The attribute-declared checks come first,
in source order, then one region per rules class. Classes are ordered by name and their statements
run in body order, because the body *is* the validator. `Apply` rules run last.

Rules for one field belong in one chain. Two separate statements against the same field report
independently, and a failed `Require` suppresses the rest of *its own* chain and nothing more.

## What is rejected

Almost everything transcribes. What does not, and why:

- **The builder flowing where the reader cannot follow**
  ([VM0087](/reference/diagnostics#vm0087)): storing `rules` or a chain in a local, capturing it in
  a lambda, or passing it to anything that is not a fragment. A rule call the generator cannot see
  would validate nothing, so it is an error instead.
- **A member the companion file cannot reach**
  ([VM0088](/reference/diagnostics#vm0088)): `private` members of the rules class. Make them
  `internal`. A `private const` is carried across by value.
- **Islands in loops, lambdas, or local functions**
  ([VM0089](/reference/diagnostics#vm0089)). Collections are `Each`'s job, and the reporter tier
  covers the unusual per-element case.
- **Statements with no sensible transcription**
  ([VM0070](/reference/diagnostics#vm0070)): `goto`, `try`/`catch`, `lock`, `using` statements, and
  assignment to the subject.
