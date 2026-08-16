# ValidationModules — API surface

**Written:** 2026-08-12
**Status:** proposed; resolves §4, §5, §6 and every open question in §12 of `IMPLEMENTATION-PLAN.md`
**Method:** every signature below was compiled — together, under `IsAotCompatible` with the IL*
warnings escalated to errors, on net8.0 and net10.0 — and then published with Native AOT and run,
including the concurrent async case. §14 is the log; §13 is what changed from the plan and why.

**Plan §4's contracts are kept as written**, including `IAsyncValidatorFor<T>` taking a
`ValidationContext` by value. The one change is that `ValidationContext` is a `readonly struct`
rather than a `ref struct` — see §13.1, which is the decision the rest of this document rests on.

This document is the contract. The plan says *what* and *why*; this says *exactly what the user
types and exactly what they get back*.

---

::: warning What this document is, as of 1.0.0
**The surface itself is pinned by a test, not by this file.**
`tests/ValidationModules.Runtime.Tests/Snapshots/PublicApiTests.RuntimeApi.verified.txt` lists every
public type and member and fails the build when it changes. Two documents claiming to be the exact
surface is how one of them ends up lying, and this one did: it described `GeneratedValidators`, a
static `Instance` field and a profile surface for some time after all three were gone.

So read this for the **reasoning** — why each decision was taken, what was rejected, and the
verification behind the claims — and read the snapshot for what is actually there. Where the two
disagree, the snapshot is right and this file has a bug.

Sections describing deferred features are marked as such at their head. `docs/deferred-features.md`
records what was withdrawn before 1.0.0 and how each part comes back additively.
:::

## 1. Namespace layout

| Namespace | Contents | Imported by |
|---|---|---|
| `ValidationModules` | contracts, context, result, error, exception, profiles | service code, hand-written validators |
| `ValidationModules.Constraints` | every constraint attribute | model files, and only model files |
| `ValidationModules.Naming` | `IValidationFieldNamer` + built-ins | rarely; adapters and custom naming |
| `ValidationModules.FluentValidation` | the adapter | the adapter's consumer |
| `ValidationModules.Testing` | conformance suite, assertions | test projects |
| `Microsoft.Extensions.DependencyInjection` | `AddValidationModules` | composition root |

**Constraints get their own namespace, and this is load-bearing.** Five of the nine attribute
names in plan §5 collide with `System.ComponentModel.DataAnnotations`: `Required`, `StringLength`,
`Range`, `AllowedValues` and (via `Length`) the item-count family. A model file that imports both
namespaces gets `error CS0104: 'Required' is an ambiguous reference` — verified, §14.4. Keeping
constraints out of `ValidationModules` means the ambiguity is only reachable by a file that
explicitly asks for both, and DI/service code never trips it. See §11 (`VM0010`) for the diagnostic that
catches it and §18 for the DataAnnotations front-end, which removes the need to import both at all.

---

## 2. Core contracts

```csharp
namespace ValidationModules;

/// <summary>
/// Structural validation. Generated, stateless, singleton, allocation-free on the success path.
/// </summary>
public interface IValidatorFor<in T> {
    void Validate(ref ValidationContext context, T value);
}

/// <summary>
/// Business rules that need I/O. Hand-written, scoped, takes dependencies.
/// Same context type as the sync side, by value.
/// </summary>
public interface IAsyncValidatorFor<in T> {
    ValueTask ValidateAsync(
        ValidationContext context,
        T value,
        CancellationToken cancellationToken = default);
}
```

One context type, both sides. `ref` on the sync side is kept from the plan — it costs nothing (the
struct is a reference plus an int) and blocks defensive copies through the interface. The async side
takes it by value, which it must: `ref` parameters are illegal on `async` methods. Both forms
address the same collector, so a merged run produces one ordered error list with one path
vocabulary regardless of which side produced each error.

### Convenience surface

The interfaces stay minimal; everything ergonomic is an extension so that implementing
`IValidatorFor<T>` by hand never means implementing five members.

```csharp
namespace ValidationModules;

public static class ValidatorForExtensions {
    /// <summary>Runs the validator against a fresh collector and returns an immutable result.</summary>
    public static ValidationResult Validate<T>(this IValidatorFor<T> validator, T value);

    /// <summary>Allocation-free predicate; stops building paths once it knows the answer.</summary>
    public static bool IsValid<T>(this IValidatorFor<T> validator, T value);

    /// <summary>Throws <see cref="ValidationException"/> if invalid. Never throws on success.</summary>
    public static void ValidateAndThrow<T>(this IValidatorFor<T> validator, T value);

    /// <summary>Runs into a caller-owned collector, so the caller can pool it. Hot paths use this.</summary>
    public static void ValidateInto<T>(
        this IValidatorFor<T> validator, ValidationErrorCollector collector, T value);
}
```

---

## 3. The context

One context type, used by both interfaces. It is a **`readonly struct`, not a `ref struct`** — the
single decision that lets `IAsyncValidatorFor<T>` keep the plan's signature and lets async
validators be written the obvious way. §13.1 has the reasoning; §3.2 has what makes it safe.

### 3.1 `ValidationContext`

```csharp
namespace ValidationModules;

/// <summary>
/// The collector, plus the compact path this context sits at: the outermost segment, the
/// immediate parent, and each one's index or key. Seven words, copied freely. Push allocates
/// nothing on the heap and nothing is concatenated until an error is added.
/// </summary>
public readonly struct ValidationContext {
    public ValidationContext(ValidationErrorCollector collector);

    /// <summary>Descends into a nested object. `ctx.Push("home")` → errors read `home.postalCode`.</summary>
    public ValidationContext Push(string segment);

    /// <summary>Descends into a collection element. → `toys[3].name`.</summary>
    public ValidationContext PushIndex(string segment, int index);

    /// <summary>Records an error on a field of the current object. The 95% call.</summary>
    public void Add(string field, string code, string message,
                    ValidationSeverity severity = ValidationSeverity.Error);

    /// <summary>Records an error on the current object itself — type-level and cross-field rules.</summary>
    public void AddHere(string code, string message,
                        ValidationSeverity severity = ValidationSeverity.Error);

    /// <summary>True if anything in this pass has failed. Pass-wide, not subtree-scoped.</summary>
    public bool HasErrors { get; }

    /// <summary>Pass-wide error count. Snapshot before and after to detect local failure.</summary>
    public int ErrorCount { get; }

    /// <summary>The profile this pass is running under, or null for the default profile.</summary>
    public Type? Profile { get; }
}
```

Argument order on `Add` is `(field, code, message)`, matching `ValidationError`'s member order so
the two never have to be mentally transposed.

### 3.2 Why the path lives in the struct

*Superseded the append-only path log, 2026-08-13. HANDOFF.md §3.1 has the decision; this is the
resulting shape.*

The obvious zero-allocation path representation is a stack the contexts index into by depth, where
`Push` writes at `_depth` and returns `_depth + 1`. It is wrong, and it is wrong in a way that only
shows up under concurrency: two sibling contexts at the same depth overwrite each other's segment,
so a context that is pushed, parked on an `await`, and used later reports whichever sibling wrote
last. That is precisely what an async validator doing `Task.WhenAll` over collection elements does.

The earlier answer was an append-only log in the collector, with each context holding a node index.
That was correct, but it existed solely to reconstruct full ancestry — and full ancestry is not
what gets reported. A context keeps the **outermost** segment and the **immediate parent**, so a
failure four levels down reads `body...address.postalCode`. Nothing between them is needed, so
nothing between them is stored, and the log has no remaining job.

Both retained segments carry their own index or key. Rendering `toys.owner.name` for what is really
`toys[3].owner.name` would not be a shortened path but a false one — it asserts an object at `toys`
that does not exist. Elision may omit; it may not lie. The `...` marker appears only when a segment
really was dropped, which is three or more descents.

Consequences:

- A context is valid for the life of its collector. Across awaits, inside closures, in any order.
- Concurrent fan-out is correct by construction, and now trivially so: `Push` writes nothing any
  other context can observe, because the path is entirely inside the copied struct.
- No heap allocation per `Push` at any depth or element count, and no shared buffer to grow, reset
  or size. `Reset()` has no path state left to clear.
- The collector's lock guards only `Add`. Descending never contends for it, synchronized or not.

Because correctness no longer depends on the caller's discipline, `ref struct` was only buying a
restriction — and one that cost the async interface its natural shape.

**What this gives up.** An index on an ancestor that is neither the outermost nor the parent
(`body.order.lines[3].address.postalCode` → `body...address.postalCode`), and an index on the
outermost segment when a bare collection is validated at the very top. Both need three or more
descents; neither is a request-body shape.

### 3.3 `ValidationErrorCollector` — the shared accumulator

Public because pooling it is the point, and because hand-written validators and the adapter write
into it.

It also owns one semantic rule rather than only storage: a field that has failed `required` accepts
no further errors for the rest of the pass. §4.3 has the reasoning; the short version is that it has
to live somewhere every engine reaches, and this is the only such place.

```csharp
namespace ValidationModules;

public sealed class ValidationErrorCollector {
    public ValidationErrorCollector();
    public ValidationErrorCollector(Type? profile);

    /// <summary>
    /// A collector that tolerates concurrent Add. For async validators that genuinely fan out.
    /// The default collector does not synchronise, because generated straight-line code never
    /// needs it and the lock would sit on the hot path. Push needs no synchronisation either way.
    /// </summary>
    public static ValidationErrorCollector CreateSynchronized(Type? profile = null);

    public bool HasErrors { get; }
    public int Count { get; }
    public Type? Profile { get; }

    /// <summary>Adds a pre-pathed error. Used by adapters that already have a flat field name.</summary>
    public void Add(in ValidationError error);

    /// <summary>Snapshots into an immutable result. Returns ValidationResult.Valid when empty.</summary>
    public ValidationResult ToResult();

    /// <summary>Clears errors, keeping the buffer. For pooled reuse.</summary>
    public void Reset();
}
```

**The one concurrency rule.** §3.2 makes a *context* safe to hand to concurrent tasks; it does not
make the *collector* safe to mutate from them. Handing contexts to parallel branches that all add
errors needs `CreateSynchronized()`. The default collector is unsynchronised, which is correct for
every generated validator and for any async validator that awaits sequentially — the overwhelming
majority. Since the path moved into the struct, the lock covers only `Add`; descending into a
nested object no longer touches the collector at all.

Getting this wrong is silent rather than loud, so `Reset()` and the mutators carry a DEBUG-only
overlap detector: an `Interlocked` in-use flag that throws `InvalidOperationException` naming the
offending path when two threads mutate an unsynchronised collector at once. Costs nothing in
Release, and turns the one remaining footgun into a failing test.

---

## 4. Error model

```csharp
namespace ValidationModules;

public readonly record struct ValidationError(string Field, string Code, string Message) {
    /// <summary>Defaults to Error, which is `default`, so an uninitialised severity is never silently benign.</summary>
    public ValidationSeverity Severity { get; init; }
}

/// <summary>Values and ordering match FluentValidation.Severity exactly, so the adapter is a cast.</summary>
public enum ValidationSeverity {
    Error = 0,
    Warning = 1,
    Info = 2,
}
```

Severity is an `init` property rather than a constructor parameter: the positional form stays
source-compatible with Hardened's existing `ValidationError(Field, Code, Message)`, and adding it
now rather than later avoids a binary break on the primary constructor (plan §7.5 wants the emitted
surface additive-only).

```csharp
namespace ValidationModules;

/// <summary>
/// Immutable. Because it is immutable, a shared `Valid` instance is safe — which is the fix for
/// the mutable process-wide singleton at Hardened's ValidationResult.cs:4 (plan §10.5).
/// There is no AddError.
/// </summary>
public sealed class ValidationResult {
    public static ValidationResult Valid { get; }

    /// <summary>True when no error has Severity == Error. Warnings and Info do not invalidate.</summary>
    public bool IsValid { get; }

    /// <summary>True when Errors is non-empty at any severity.</summary>
    public bool HasErrors { get; }

    public IReadOnlyList<ValidationError> Errors { get; }

    public static ValidationResult FromErrors(IEnumerable<ValidationError> errors);

    /// <summary>Returns a new result; does not mutate either operand.</summary>
    public ValidationResult Merge(ValidationResult other);
}

public sealed class ValidationException : Exception {
    public ValidationException(ValidationResult result);
    public ValidationResult Result { get; }
}
```

### 4.1 Error code vocabulary — fixed

| Constraint | Code |
|---|---|
| `[Required]` | `required` |
| `[StringLength]` | `string_length` |
| `[Range]` | `range` |
| `[Pattern]` | `pattern` |
| `[AllowedValues]` | `enum` |
| `[ItemCount]` | `array_bounds` |
| `[MultipleOf]` | `multiple_of` |
| `[UniqueItems]` | `unique_items` |
| `rules.Ensure(…)` | `predicate` |

These are Hardened's existing wire codes verbatim (grepped from
`Hardened.Requests.Runtime/Validation/Rules/`), so retargeting Hardened onto this emitter in Stage 5
changes no 400-response body. `enum` for `[AllowedValues]` is the one that reads oddly; it is kept
because it is already on the wire and renaming it would break existing API consumers for cosmetics.
`Code` on any constraint attribute overrides it per rule.

### 4.2 Ordering and short-circuit — the semantics the conformance suite pins

- Errors emit in **declaration order** — properties in source order, constraints in attribute order
  within a property, nested objects at the point of their property, collection elements ascending.
  This binds every structural validator and is what the conformance suite pins. Registered
  validators run in registration order, and `ValidationRunner<T>` awaits async ones sequentially, so
  the guarantee holds across validators too. The one exception is an async validator that fans out
  internally: its own errors land in completion order, which is the author's choice to make.
- Within a property, **`[Required]` is evaluated first**, whatever order the attributes are written
  in. Every other constraint follows attribute order. This is the one place declaration order is
  overridden, and the next rule is why.
- A failed `[Required]` **suppresses** every other error on the same field for the rest of the pass.
  This is enforced by `ValidationErrorCollector`, not by generated control flow — see §4.3.
- Nothing else short-circuits. All errors are collected; there is no first-failure exit.
- `[ValidateNested]` does not recurse into a value that failed `[Required]`. That is the emitter's
  job, not the collector's: suppression matches whole field paths and is deliberately not a prefix
  match, so a failed `[Required]` on `home` does not silence `home.postalCode`.
- Field paths are dotted with bracketed indices: `home.postalCode`, `toys[3].name`. Chosen over JSON
  Pointer because FluentValidation already produces this shape, which keeps the adapter's job to a
  case conversion.

### 4.3 Suppression lives in the collector

The obvious home for "a failed `[Required]` suppresses the rest of the field" is the emitter, as an
`else if` chain — and that is where the plan puts it. It only works for engines that generate code.

The FluentValidation adapter maps `ValidationFailure`s that FluentValidation has already produced.
It has no control flow to put an `else` in, so `RuleFor(x => x.Name).NotNull().Length(1, 100)`
against a null name hands it two failures and it must forward both. If suppression is a shape in
emitted source, the adapter cannot honour it, and §16's conformance suite has to exclude the rule —
at which point the two engines are not substitutable on the one semantic §4.2 exists to pin.

So `ValidationErrorCollector` enforces it, at the single point every error passes through. Every
engine reaches the collector, so every engine gets the rule, and the `else if` in generated code
becomes an optimization — skip work whose result would be discarded — rather than the mechanism.
Correctness stops depending on the emitter getting the chain right.

Three properties, each chosen against a plausible alternative:

- **Forward-only.** A field is suppressed from the moment it fails `[Required]`; errors already
  recorded are not removed. Retroactive removal would report a different result depending on the
  order two independent validators happened to run in, which is worse to reason about than an
  occasional duplicate. This is what makes the evaluation-order rule in §4.2 load-bearing rather
  than cosmetic.
- **Exact path match, not prefix.** `home.postalCode` and `work.postalCode` are different fields.
- **Error severity only.** A `required` reported as a warning is advisory; silencing the field on
  the strength of it would drop a real failure.

The cost is on the failure path and gated: the collector tracks whether any `required` has been seen
at all, so an ordinary length-or-range failure never runs the check. The list it scans holds only
fields that are actually missing, which is short in any realistic pass.

---

## 5. Constraint attributes

```csharp
namespace ValidationModules.Constraints;

/// <summary>
/// Base for every constraint. Carries profile attribution (§6) and per-rule message overrides.
/// Named arguments are `init` properties — verified legal in attribute usage, §14.2.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = true, Inherited = true)]
public abstract class ValidationConstraintAttribute : Attribute {
    /// <summary>First profile on the chain this rule applies to, inclusive.</summary>
    public Type? FromProfile { get; init; }

    /// <summary>First profile on the chain this rule stops applying to, exclusive.</summary>
    public Type? UntilProfile { get; init; }

    /// <summary>Explicit profile set, for profiles that are not on a chain.</summary>
    public Type[]? Profiles { get; init; }

    /// <summary>Overrides the code from §4.1.</summary>
    public string? Code { get; init; }

    /// <summary>Overrides the generated message. `{field}` is substituted at generation time.</summary>
    public string? Message { get; init; }
}
```

**`AllowMultiple = true` is required, not incidental.** It is what lets a property carry a different
bound per profile:

```csharp
[StringLength(min: 1, max: 100)]
[StringLength(min: 1, max: 200, FromProfile = typeof(V2))]
public string Name { get; init; }
```

### The nine

```csharp
namespace ValidationModules.Constraints;

public sealed class RequiredAttribute : ValidationConstraintAttribute {
    /// <summary>
    /// When false (default) a string of only whitespace counts as missing, matching both
    /// DataAnnotations' trim behaviour and Hardened's RequiredRule. Set true for a pure null check.
    /// </summary>
    public bool AllowEmptyStrings { get; init; }
}

public sealed class StringLengthAttribute : ValidationConstraintAttribute {
    public StringLengthAttribute() { }
    public StringLengthAttribute(int min, int max);
    public int Min { get; init; }
    public int Max { get; init; } = int.MaxValue;
}

public sealed class RangeAttribute : ValidationConstraintAttribute {
    public RangeAttribute(int min, int max);
    public RangeAttribute(long min, long max);
    public RangeAttribute(double min, double max);
    /// <summary>Parsed at generation time against the property's type. For decimal, DateTime, DateOnly, TimeSpan.</summary>
    public RangeAttribute(string min, string max);

    /// <summary>OpenAPI exclusiveMinimum / exclusiveMaximum.</summary>
    public bool ExclusiveMin { get; init; }
    public bool ExclusiveMax { get; init; }
}

public sealed class PatternAttribute : ValidationConstraintAttribute {
    /// <summary>Inline. Rejected in an AOT-facing project — see §18.8.</summary>
    public PatternAttribute(string pattern);

    /// <summary>Reference a [GeneratedRegex] the consumer declared. Always accepted.</summary>
    public PatternAttribute(Type regexProvider, string regexMember);

    public string? Pattern { get; }
    public Type? RegexProvider { get; }
    public string? RegexMember { get; }
    public RegexOptions Options { get; init; }
    public int MatchTimeoutMilliseconds { get; init; }
}

public sealed class AllowedValuesAttribute : ValidationConstraintAttribute {
    public AllowedValuesAttribute(params object[] values);
    public object[] Values { get; }
    public StringComparison Comparison { get; init; } = StringComparison.Ordinal;
}

public sealed class MultipleOfAttribute : ValidationConstraintAttribute {
    public MultipleOfAttribute(int divisor);
    public MultipleOfAttribute(long divisor);
    public MultipleOfAttribute(double divisor);
    public MultipleOfAttribute(string divisor);   // decimal, which has no constant form
    public object Divisor { get; }
}

// No arguments; presence is the constraint. The only constraint that does not compile to a
// comparison - it calls ConstraintChecks.AllUnique.
public sealed class UniqueItemsAttribute : ValidationConstraintAttribute;

public sealed class ItemCountAttribute : ValidationConstraintAttribute {
    public ItemCountAttribute() { }
    public ItemCountAttribute(int min, int max);
    public int Min { get; init; }
    public int Max { get; init; } = int.MaxValue;
}

public sealed class ValidateNestedAttribute : ValidationConstraintAttribute {
    /// <summary>
    /// On an object, recurses into it. On a collection, recurses into each element.
    /// On a dictionary, recurses into values, pathed as `map[key]`.
    /// </summary>
}
```

`[Pattern]` compiles to a `[GeneratedRegex]` partial method on the validator, per plan §2. There is
no code path that constructs a `Regex` at runtime.

### Type-level attributes

```csharp
namespace ValidationModules.Constraints;

/// <summary>
/// Opt a type into validator generation when it carries no constraints of its own — because its
/// rules live in an overlay (§6.4), because it is only a [ValidateNested] target, or because the
/// caller wants IValidatorFor&lt;T&gt; injectable regardless. Any constraint on any member implies this.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
public sealed class GenerateValidatorAttribute : Attribute {
    /// <summary>Restricts generation to these profiles. Default is every profile the rules mention.</summary>
    public Type[]? Profiles { get; init; }
}
```

### Declaring constraints without these attributes

`System.ComponentModel.DataAnnotations` constraints are compiled too, so a model already
annotated for EF Core or Swashbuckle needs no edits. §18 has the mapping and the two places the
semantics deliberately differ.

### Positional records

Constraints go through `[property:]`, which is C#'s general rule for record parameters, not
something this library can change. Verified compiling, §14.5:

```csharp
public record Pet(
    [property: Required] string Name,
    [property: Required(FromProfile = typeof(V2))] string? Tag);
```

The generator emits `VM0051` on a record parameter carrying a bare constraint attribute, because
without `[property:]` the attribute lands on the parameter and is silently unreachable at the
property — the single most likely way to write a rule that does nothing.

---

## 6. Profiles — deferred, design retained

::: danger Not in 1.0.0
Profiles were specified here and never built, and the declaration surface shipped ahead of them —
so setting `FromProfile` was `VM0019`, an error, rather than a restriction. The whole surface was
withdrawn before 1.0.0 pinned it: `IValidationProfile`, `[DefaultValidationProfile]`, the three
attribute properties, `IValidatorProvider`'s profile members, `ValidationErrorCollector.Profile`,
`ValidationContext.Profile`, and the `Type? profile` parameters.

**This section is kept as the design, not as a description of the library.** Nothing below is
implemented. `docs/deferred-features.md` records the reversibility analysis, including the one
member set that has to return as default interface members to avoid breaking implementers, and the
Native AOT trap waiting for whatever resolves per-profile registrations.
:::

### 6.1 Declaring

```csharp
namespace ValidationModules;

public interface IValidationProfile;

public interface IValidationProfile<TPredecessor> : IValidationProfile
    where TPredecessor : IValidationProfile;
```

```csharp
public sealed class V1 : IValidationProfile;
public sealed class V2 : IValidationProfile<V1>;
public sealed class V3 : IValidationProfile<V2>;

public sealed class Strict : IValidationProfile;      // orthogonal, not on the chain
```

Profile types are never instantiated. `interface V1 : IValidationProfile` is equally accepted and
has the small advantage of being uninstantiable; the plan's `sealed class` form is the documented
default so examples stay uniform.

### 6.2 Attribution — all four forms

```csharp
public record Pet {
    [Required]                                        public string  Name   { get; init; }
    [Required(FromProfile = typeof(V2))]              public string? Tag    { get; init; }
    [Required(UntilProfile = typeof(V2))]             public string? Legacy { get; init; }
    [Pattern("^[A-Z]{3}$", Profiles = [typeof(Strict)])] public string Sku  { get; init; }
}
```

**Collection expressions are legal in attribute arguments** — `Profiles = [typeof(Strict)]`
compiles on net8.0 and later, verified §14.3. The plan flagged this as needing a check before
committing; it does not need the `new[] { … }` fallback. `new[]` remains valid for LangVersion 10
consumers.

Resolution rules, in order:

1. No attribution → the rule applies in **every** profile, including the default.
2. `Profiles` set → applies in exactly those profiles, and **not** in the default.
3. `FromProfile` and/or `UntilProfile` → applies to the half-open chain interval
   `[FromProfile, UntilProfile)`, and **not** in the default.
4. `Profiles` combined with `FromProfile`/`UntilProfile` → union. Diagnosed as `VM0015` (info),
   because it is almost always a mistake rather than an intent.

### 6.3 The default profile — resolved (§12 Q2)

**Default is the absence of a profile, and it means the rules that apply in every profile** — the
unattributed ones, per rule 1. Not a `Default` marker type: a marker type would appear in generated
validator names and in `IValidatorFor<T>` resolution for codebases that declared no profiles, which
violates the "profiles are opt-in and invisible" non-negotiable.

The footgun this creates is real and is surfaced rather than papered over: on a type that has *any*
profiled rule, `IValidatorFor<Pet>` validates only the common core, so injecting it after someone
adds `[Required(FromProfile = typeof(V2))]` silently validates less than the author expects. Two
mitigations, both in the surface:

```csharp
namespace ValidationModules;

/// <summary>
/// Redirects the unprofiled IValidatorFor&lt;T&gt; registration for the whole assembly to a
/// named profile. The common-core validator is still emitted and still reachable by name.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class DefaultValidationProfileAttribute : Attribute {
    public DefaultValidationProfileAttribute(Type profile);
    public Type Profile { get; }
}
```

and diagnostic `VM0020`, emitted once per type that has profiled rules while the assembly declares
no default profile.

### 6.4 Overlays — deferred, design retained

::: danger Not in 1.0.0
`[ValidationOverlayFor<T>]` was read by no front end at all — applying it compiled and did nothing.
Withdrawn with the profile surface. Rule classes (§19) already cover most of what this was for; the
part worth building if overlays return is the per-member mirroring described below, which checks
that each declared member exists on the target.
:::

The escape hatch for types you do not own. Deliberately more verbose than attributes, because plan
§6 is explicit that overlays as the primary path made the design worse.

A declaration-only partial class whose members mirror the target's properties by name and type. The
members are never invoked; the class exists to hold attributes. Generic attributes apply cleanly,
verified §14.6.

```csharp
namespace ValidationModules;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ValidationOverlayForAttribute<TTarget> : Attribute {
    /// <summary>Restricts this overlay to specific profiles. Default is all.</summary>
    public Type[]? Profiles { get; init; }
}
```

```csharp
[ValidationOverlayFor<Pet>]                       // Pet comes from a package we do not own
public sealed partial class PetOverlay {
    [Required(FromProfile = typeof(V2))]
    public string? Tag { get; }                   // must match Pet.Tag by name and type

    [ValidateNested]
    public Address? Home { get; }
}
```

Name and type are checked against the target at generation time (`VM0030`, `VM0031`), so renaming
`Pet.Tag` upstream breaks the build with a clear message instead of silently dropping a rule — which
is the property a reflection-based overlay could not have.

Overlay rules and in-type rules for the same property **union**. Two overlays targeting the same
type are legal and also union; conflicting bounds are diagnosed (`VM0032`).

### 6.5 Runtime profile selection

Per type, the generator emits the dispatch table from plan §6:

```csharp
public static class PetValidators {
    public static IValidatorFor<Pet>? For(Type profile);
    public static IReadOnlyList<Type> Profiles { get; }
    public static IValidatorFor<Pet> Default { get; }
}
```

and per assembly, a generic front door. The cast is over a closed type, so there is no
`MakeGenericType` anywhere — verified compiling, §14.7:

```csharp
namespace ValidationModules;

public interface IValidatorProvider {
    IValidatorFor<T>? GetValidator<T>();
    IValidatorFor<T>? GetValidator<T>(Type profile);
    IReadOnlyList<Type> GetProfiles<T>();
}
```

The generated implementation is a `typeof(T) ==` ladder over the assembly's validated types.
`GetValidator<T>` is a generic interface method, so under AOT each `T` must be statically visible at
some call site — it always is, because call sites are in user code.

---

## 7. Composition — running structural and business rules together

Plan §8 requires that all registered validators for a type run and merge, and that async validators
run only if structural validation passed. That policy needs one owner, or every consumer
reimplements it slightly differently.

```csharp
namespace ValidationModules;

/// <summary>
/// Registered closed, per validated type, by the generator. Also directly constructible, which is
/// how Hardened's ValidationFilter&lt;TBody&gt; resolves its validators once at handler construction.
/// </summary>
public sealed class ValidationRunner<T> {
    public ValidationRunner(
        IEnumerable<IValidatorFor<T>> structural,
        IEnumerable<IAsyncValidatorFor<T>> business);

    /// <summary>Structural only. Allocation-free when valid.</summary>
    public ValidationResult Validate(T value, Type? profile = null);

    /// <summary>
    /// Structural first; business rules run only if structural validation produced no Error.
    /// Results merge — no precedence, nothing replaces anything.
    /// </summary>
    public ValueTask<ValidationResult> ValidateAsync(
        T value, Type? profile = null, CancellationToken cancellationToken = default);
}
```

Registering it closed rather than as an open generic keeps MS.DI's reflection-based open-generic
activation out of the picture, same reasoning as the adapter registration in plan §8.

### 7.1 `ValidateAsync` is an ordinary `async` method

Because `ValidationContext` is a plain `readonly struct`, sync validators run by `ref` inside an
`async` method, the context survives awaits, and no split into a sync core plus an async tail is
needed:

```csharp
public async ValueTask<ValidationResult> ValidateAsync(
    T value, Type? profile = null, CancellationToken cancellationToken = default) {

    var collector = new ValidationErrorCollector(profile);

    foreach (var v in _structural) {
        var ctx = new ValidationContext(collector);
        v.Validate(ref ctx, value);
    }

    if (!collector.HasErrors) {
        var ctx = new ValidationContext(collector);
        foreach (var v in _business) {
            await v.ValidateAsync(ctx, value, cancellationToken);
        }
    }

    return collector.ToResult();
}
```

Had the context stayed a `ref struct`, none of this would compile on net8.0: a `ref struct` local is
illegal *anywhere* in an `async` method under C# 12, not merely across an `await` (`CS9202`,
§14.12). That restriction would have propagated to every async consumer, Hardened's request filter
included. It is the second reason §13.1 went the way it did.

Business validators are awaited **sequentially**, so cross-validator error ordering stays
deterministic. Fan-out inside a single async validator is the author's own choice and appends in
completion order; §4.2 covers what that means for the ordering guarantee.

---

## 8. Field naming

```csharp
namespace ValidationModules.Naming;

public interface IValidationFieldNamer {
    /// <summary>"PostalCode" → "postalCode".</summary>
    string ToFieldName(string clrPropertyName);

    string Combine(string parentPath, string fieldName);
    string CombineIndex(string parentPath, string fieldName, int index);
}

public sealed class CamelCaseFieldNamer  : IValidationFieldNamer { public static readonly CamelCaseFieldNamer Instance; }
public sealed class PascalCaseFieldNamer : IValidationFieldNamer { public static readonly PascalCaseFieldNamer Instance; }
public sealed class SnakeCaseFieldNamer  : IValidationFieldNamer { public static readonly SnakeCaseFieldNamer Instance; }
```

**Naming is a generation-time decision that the runtime can read back.** The generated engine bakes
field names in as string literals — it never calls a namer per validation. To stop the
FluentValidation adapter emitting `Home.PostalCode` where the generator emits `home.postalCode`
(plan §4), the generator also emits which policy it used:

```csharp
public static class GeneratedValidatorMetadata {
    public static IValidationFieldNamer FieldNamer { get; }
    public static string RuntimeVersion { get; }
}
```

and the adapter resolves `IValidationFieldNamer` from DI, defaulting to that.

Precedence when baking a literal, highest first:

1. `[JsonPropertyName("…")]` on the property.
2. `[DataMember(Name = "…")]`.
3. The MSBuild policy `ValidationModules_FieldNaming` = `CamelCase` (default) `| PascalCase |
   SnakeCase | AsDeclared`.

For Hardened's OpenAPI front-end the spec's own property name wins over all three, matching what
`ValidationFilterEmitter` does today (it emits `prop.Name`, the spec name, not the PascalCase CLR
name).

---

## 9. Generated surface

What the user sees in `obj/…/generated/`. The profile suffixes this section originally carried are
gone with §6; one validator is emitted per type.

```csharp
// <auto-generated/>
public sealed partial class PetValidator : IValidatorFor<Pet> {

    private IValidatorFor<Address>[]? _homeValidators;
    private IValidatorFor<Toy>[]? _toysValidators;

    /// <summary>Resolved from the container: the full set for each nested type.</summary>
    public PetValidator(
        IEnumerable<IValidatorFor<Address>> home,
        IEnumerable<IValidatorFor<Toy>> toys) {
        _homeValidators = System.Linq.Enumerable.ToArray(home);
        _toysValidators = System.Linq.Enumerable.ToArray(toys);
    }

    /// <summary>Standalone: nested types fall back to their own generated validators.</summary>
    public PetValidator() { }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex SkuPattern();

    public void Validate(ref ValidationContext ctx, Pet value) {
        if (string.IsNullOrWhiteSpace(value.Name))  ctx.Add("name", "required", "name is required.");
        else if (value.Name.Length > 100)           ctx.Add("name", "string_length", "name must be at most 100 characters.");

        if (value.Tag is null)                      ctx.Add("tag", "required", "tag is required.");

        if (!SkuPattern().IsMatch(value.Sku))       ctx.Add("sku", "pattern", "sku is not in the required format.");

        if (value.Home is { } home) {
            var nested = ctx.Push("home");
            var validatorsHome = HomeValidators;
            for (var vi = 0; vi < validatorsHome.Length; vi++) {
                validatorsHome[vi].Validate(ref nested, home);
            }
        }

        for (var i = 0; i < value.Toys.Count; i++) {
            var item = ctx.PushIndex("toys", i);
            var elementValidators = ToysValidators;
            for (var vi = 0; vi < elementValidators.Length; vi++) {
                elementValidators[vi].Validate(ref item, value.Toys[i]);
            }
        }
    }
}
```

Guarantees the surface makes:

- The `else if` after a `required` check is an **optimization**, not the suppression mechanism —
  §4.3. It skips work whose result the collector would discard anyway, and a validator hand-written
  without it still produces one error rather than two.
- No attributes on generated types (plan §7.2 — a second generator would not see them anyway).
- **Two public constructors, and no static `Instance`.** The injected one takes the nested types'
  validators so a hand-written `IValidatorFor<Address>` composes when an `Address` is reached
  *through* a `Pet`, not only when validated directly; MS.DI picks it, being the greediest it can
  satisfy. The parameterless one is what `new PetValidator()` and a unit test get, and falls back to
  each nested type's own generated validator.
- **Held as arrays, not as the injected `IEnumerable<>`.** Enumerating an interface-typed sequence
  boxes its enumerator, which would allocate on a clean pass at every nested property. The same
  reasoning applies to `ValidationRunner<T>`, which materialises for the same reason.
- **The nested fields fill in lazily**, and that is not a micro-optimization: building them eagerly
  would recurse forever on a self-referential model, and a `StackOverflowException` cannot be
  caught. Demand-driven initialisation terminates because nothing is built until a value descends,
  at which point the depth guard bounds it.
- **`internal` rather than `public` when the model is**, since a public method cannot take a less
  accessible parameter (CS0051).

---

## 10. Registration

**One body, two wrappers.** The emitter always writes an `IServiceCollection` extension named after
the assembly; when DependencyModules is referenced it adds a module that calls it, rather than a
second copy of the same registrations.

```csharp
namespace Microsoft.Extensions.DependencyInjection {
    public static class MyAppValidationExtensions {
        public static IServiceCollection AddMyAppValidators(this IServiceCollection services) {
            services.AddSingleton<IValidatorFor<global::MyApp.Address>, global::MyApp.AddressValidator>();
            services.AddSingleton<IValidatorFor<global::MyApp.Pet>, global::MyApp.PetValidator>();

            services.AddValidationRunner<global::MyApp.Address>();
            services.AddValidationRunner<global::MyApp.Pet>();

            services.TryAddSingleton<IValidationFieldNamer>(CamelCaseFieldNamer.Instance);

            return services;
        }
    }
}
```

### With DependencyModules present

```csharp
namespace MyApp {
    public sealed class ValidationModule : IDependencyModule {
        public void PopulateServiceCollection(IServiceCollection services) =>
            services.AddMyAppValidators();
    }
}
```

Emitted whole rather than as a partial for DM's own generator to finish: generators cannot see each
other's output, so an attribute this one wrote would never reach DM's. `IDependencyModule` has
exactly one member without a default implementation, so there is nothing left to complete.

Selected by `ValidationModules_Registration` = `DependencyModules | ServiceCollection | None`,
defaulting to auto-detection on whether `IDependencyModule` resolves.

### Why the method name carries the assembly

Each assembly registers its own validators — there is deliberately no cross-assembly scanning — so
two of them emitting `AddValidationModules()` on `IServiceCollection` would be CS0121 at the
composition root. `AddMyAppValidators()` and `AddMyLibValidators()` compose without ceremony.

### Why not the table

An earlier design emitted `GeneratedValidators.All`, a static array of
`ValidatorRegistration(Type, Func<IServiceProvider, object>)`. It erased the generic — nothing
checked that the factory for `typeof(IValidatorFor<Pet>)` returned one — allocated an array of
closures at static init only to iterate it once, and lived in a class the consumer had to already
know the name of. `ValidatorRegistration` and `AddValidationModules(IReadOnlyList<…>)` remain in the
runtime for anyone hand-building a table; nothing generates one.

### Lifetimes

Validators are singletons, always: generated ones are stateless, and building the rule graph once
rather than per call is a hard requirement. `ValidationRunner<T>` is scoped, because the async
validators it composes may take scoped dependencies. It is registered through an explicit factory
rather than by type — see `AddValidationRunner<T>`'s remarks for why that is load-bearing under
Native AOT.

---

## 11. Diagnostics

`AnalyzerReleases.Shipped.md` / `.Unshipped.md` from the first commit — RS2008 demands them as soon
as one diagnostic is declared (plan §13).

| ID | Severity | |
|---|---|---|
| VM0001 | Error | `[StringLength]` on a non-string |
| VM0002 | Error | `[ItemCount]` on a non-collection |
| VM0003 | Error | `[Range]` on a type with no ordering |
| VM0004 | Warning | `[Required]` on a non-nullable value type — no effect |
| VM0005 | Error | `[Pattern]` on a non-string |
| VM0006 | Error | `[Pattern]` argument is not a valid regex |
| VM0007 | Warning | `[ValidateNested]` on a type with no rules and no `[GenerateValidator]` |
| VM0008 | Error | `Min` exceeds `Max` |
| VM0009 | Error | constraint on a property with no accessible getter |
| VM0010 | Warning | a DataAnnotations constraint was skipped because the front-end is off — §18.1 |
| VM0011 | Error | profile argument does not implement `IValidationProfile` |
| VM0012 | Error | `FromProfile`/`UntilProfile` names a profile not on a chain |
| VM0013 | Warning | `UntilProfile` precedes `FromProfile` — rule never applies |
| VM0014 | Error | cyclic profile chain |
| VM0015 | Info | `Profiles` combined with `FromProfile`/`UntilProfile` |
| VM0020 | Warning | type has profiled rules, assembly declares no default profile — §6.3 |
| VM0021 | Error | `[MultipleOf]` on a type with no arithmetic |
| VM0022 | Error | a `[MultipleOf]` divisor that is zero or negative |
| VM0023 | Error | a `[MultipleOf]` divisor that does not parse as the member's type |
| VM0024 | Error | `[UniqueItems]` on a non-collection |
| VM0025 | Warning | `[UniqueItems]` over elements with no equality of their own |
| VM0026 | Warning | `[Range]` declares neither bound |
| VM0030 | Error | overlay member matches no property on the target |
| VM0031 | Error | overlay member type differs from the target property |
| VM0032 | Warning | two overlays declare conflicting bounds for one property |
| VM0040 | Error | `ValidationModules.Runtime` older than the generator requires — plan §7.5 |
| VM0051 | Warning | constraint on a record parameter without `[property:]` — §5 |

---

## 12. Open questions from plan §12 — resolved

**Q1 — overlay syntax.** §6.4. Mirror-property partial class, `[ValidationOverlayFor<T>]`,
compile-time name and type checking. Prototype before Stage 3, as the plan asks.

**Q1, revisited 2026-08-13 — §19 is the answer, and §6.4 is not.** A declarative rule class gets the
same compile-time name and type checking from the C# compiler rather than from `VM0030`/`VM0031`,
without restating the target's property list, and it can express rules spanning two properties, which
no attribute form can. It also runs without our generator, which the mirror-property overlay cannot —
that is why it sits beside the generated path rather than replacing it. §6.4 stays specced and
unimplemented; §19 is the recommended form.

**Q2 — default profile.** §6.3. Absence, meaning the common core, plus an assembly-level redirect
and `VM0020` to surface the silent-weakening footgun.

**Q3 — Severity.** Included, not dropped. `ValidationSeverity` matching FluentValidation's values,
as an `init` property so the positional constructor stays compatible. Adding it later would be a
binary break on a record struct's primary constructor, which plan §7.5 rules out.

**Q4 — throw or write the response.** The library never throws on failure. `Validate` accumulates
and returns; `ValidationException` exists and `ValidateAndThrow` is the only thing that raises it.
Response shape belongs to the framework. This collapses the duplication in Hardened: one
`ValidationResult` → problem-details mapper, reached both by the filter and by
`ExceptionToModelConverter`, instead of two shapes agreeing by coincidence.

**Q5 — whitespace and `[Required]`.** Whitespace-only counts as missing by default, with
`AllowEmptyStrings = true` to opt out. This matches DataAnnotations (which trims before testing) and
Hardened's `RequiredRule` exactly, so neither incumbent's users are surprised.

---

## 13. Changes from IMPLEMENTATION-PLAN.md §4

**13.1 `ValidationContext` is a `readonly struct`, not a `ref struct`.** This is the only real
change, and everything else in this document follows from it.

The plan specifies `ref struct`, which is the right instinct — the context is a short-lived cursor
and `ref struct` is how you say so. But it is unimplementable alongside plan §4's own async
contract, and the collision is not marginal:

- A `ref struct` may not be a parameter of an `async` method (`CS4012`, §14.1), so
  `IAsyncValidatorFor<T>.ValidateAsync(ValidationContext, …)` cannot be implemented by any `async`
  method — which is every implementation anyone would write.
- A `ref struct` local is illegal *anywhere* in an `async` method under C# 12 (`CS9202`, §14.12),
  net8.0's default, so even *calling* a sync validator from an async method would have forced every
  consumer — Hardened's request filter included — into a sync-core/async-tail split.
- A `ref struct` may not be a type argument (`CS9244`, §14.9), permanently barring
  `Action<ValidationContext>`, `Task<ValidationContext>` and similar from the surface.

The earlier draft of this document resolved that by adding a second `AsyncValidationContext` type.
That was the wrong trade: two context types, two sets of docs, a conversion between them, and an
async path that allocated per nesting level — all to preserve a modifier whose only remaining
benefit was preventing a misuse that §3.2 designs out anyway. Dropping `ref struct` costs nothing
real, keeps plan §4's contracts verbatim, and makes async a first-class shape rather than a
workaround.

What replaces the safety `ref struct` was providing: §3.2's path-in-the-struct. A stack indexed by
depth would have made a stored context unsafe, which is what `ref struct` existed to prevent;
a context that carries its own path makes a stored context *correct*, verified at 500-way
concurrency in §14. (The append-only log this originally cited did the same job and was replaced
by the compact path, which does it with no shared state at all.)

**13.2 The path cannot be a stack-linked parent chain.** The other natural zero-allocation reading
of plan §4's "keep a small path stack" is `ref readonly ValidationContext _parent`, which is
`CS9050: a ref field cannot refer to a ref struct` (§14.8). Moot now that the context is not a ref
struct, and recorded so it is not rediscovered.

**13.3 `Add(code, message)` cannot name the field.** Plan §7.1's generated body calls
`ctx.Add("required", "name is required.")` while the error must come out on field `name`. The
surface takes `Add(field, code, message)`, with `AddHere(code, message)` for object-level rules.

**13.5 `[Required]` suppression is enforced by the collector, not the emitter.** Plan §4 specifies
the rule and describes it as the `else if` shape generated code takes. The rule is kept exactly; the
enforcement point moved, because an emitted shape is unavailable to any engine that maps failures
produced elsewhere — which is the FluentValidation adapter, the one thing §8 asks to be proven
substitutable. §4.3 has the full reasoning.

**13.6 Patterns compile to a `static readonly Regex`, not `[GeneratedRegex]`.** Plan §2 makes
`[GeneratedRegex]` a non-negotiable. It is not available to us, but the reason is narrower than
"generators cannot see each other's output" and worth stating precisely, because the loose version
invites an attempt that looks like it should work.

**Post-initialization output *is* visible to other generators; regular source output is not.**
Sources added through `RegisterPostInitializationOutput` are put into the compilation before any
generator's pipeline runs, so every generator sees them as if a human had written them — a
`[GeneratedRegex]` emitted that way is implemented by the regex generator and runs (§14.34). Sources
added through `RegisterSourceOutput` are collected afterwards and only reach the final compilation,
which is why *your* code can call generated code while another generator cannot see it — the same
declaration emitted that way fails with CS8795 (§14.35).

There is no fixed-point loop, no differential check and no re-running of generators against an
augmented compilation. One pass, with post-initialization simply earlier in it.

That does not rescue this case. `IncrementalGeneratorPostInitializationContext` exposes `AddSource`
and a `CancellationToken` and nothing else — no compilation, no syntax trees, no analyzer config, no
additional files — because it runs before anything has been examined. It is the hook for marker
attributes, whose text is fixed. A pattern is user data, from an attribute argument or a `pattern:`
in a spec, so the only hook that would work is the one that cannot see what we would need to emit.

What §2 was protecting against is intact. The defect it was written against (plan §10.2) was a
`Regex` constructed **per request** with `RegexOptions.Compiled`, which emits IL through
`Reflection.Emit` on every call. The emitted field is built once at type initialization and never
passes `Compiled`, so nothing reaches `Reflection.Emit` and the AOT publish stays clean. The cost is
an interpreted match rather than a source-generated one, which is a throughput difference on a path
that already allocates nothing.

Plan §7.2 states the same rule flatly. It is worth reading as applying to every other generator, not
only DependencyModules' — and with the post-initialization exception above, which DM's own marker
attributes rely on.

Unchanged from the plan and worth stating: `IValidatorFor<T>`, `IAsyncValidatorFor<T>`,
`IValidationProfile`, `IValidationProfile<TPredecessor>` and `ValidationError`'s shape are exactly
as §4 specifies. `ValidationResult` is immutable, which is the "make it immutable" branch of §4's
instruction, so keeping a shared `Valid` instance is safe.

---

## 14. Verification log

Compiled against net8.0, and net9.0/net10.0 where the answer could differ by TFM.

::: tip A record, not a description
These are captured results from probes run at the time, so several exercise the profile and overlay
surfaces §6 describes and 1.0.0 does not ship. They are left exactly as they were rather than
rewritten: what a probe found is a fact about the language, and it stays true whether or not the
feature that prompted it shipped. Anything naming `PetValidator_V2`, `FromProfile` or
`[ValidationOverlayFor<T>]` is answering "would C# allow this", not "does this exist".
:::

| | Probe | Result |
|---|---|---|
| 14.1 | `async ValueTask` implementing a method with a `ref struct` parameter | **CS4012** |
| 14.2 | `init`-only properties as attribute named arguments | compiles |
| 14.3 | collection expressions in attribute arguments — `Profiles = [typeof(V1)]` | compiles, net8.0+ |
| 14.4 | `using DataAnnotations` + `using` our namespace, bare `[Required]` | **CS0104** ambiguous |
| 14.5 | `[property: Required(FromProfile = typeof(V2))]` on a record parameter | compiles |
| 14.6 | generic attribute application — `[ValidationOverlayFor<Pet>]` | compiles |
| 14.7 | `(IValidatorFor<T>)(object)PetValidators.For(profile)` dispatch ladder | compiles |
| 14.8 | `ref readonly ValidationContext` field inside a ref struct | **CS9050 / CS0523** |
| 14.9 | `ref struct` as a generic type argument | **CS9244** |
| 14.10 | `readonly struct` context: `async` impl, `ref` call from async, awaits | compiles |
| 14.11 | `AllowedValues`, `Length`, `Base64String` in DataAnnotations | present on net8.0+ |
| 14.12 | `ref struct` **local** inside an `async` method | **CS9202** on net8.0/C#12; legal C#13 |
| 14.13 | whole surface, `IsAotCompatible` + IL* as errors, net8.0 and net10.0 | 0 errors, 0 warnings |
| 14.14 | `PublishAot` of a console app exercising the whole surface | published, 0 IL warnings |
| 14.15 | 20 rounds x 500 concurrent branches, released together, paths compared exactly | all exact |
| 14.16 | allocation per clean pass, reused collector, 1 nested object + 50 elements | **0 bytes** |
| 14.17 | `[RegularExpression("[A-Z]{3}")]` against `"xABCx"` | **rejected** — DA anchors |
| 14.18 | `[Required]` against `""` and `"   "` | both **fail** — DA trims |
| 14.19 | `[Required]` against `0` and an empty `List<int>` | both **pass** |
| 14.20 | `[Range(0, 10)]` against the string `"5"` | **passes** — DA converts at runtime |
| 14.21 | `[MinLength(2)]` against `int[1]`; `[StringLength(3)]` against `null` | fails; **passes** |
| 14.22 | `[EmailAddress]` against `"a@b"` | **passes** — DA is lenient |
| 14.23 | emission shape, success path, 12 constraints (BenchmarkDotNet) | AOT 19.9 / 19.9 / 20.2 ns |
| 14.24 | emission shape, success path, JIT | 10.3 / 10.0 / **16.2** ns |
| 14.25 | message composition, per error | **56 bytes**; 0 for an emitted literal |
| 14.26 | suppression across two validators, adapter-style pre-pathed adds, and a pooled reset | enforced in all three |
| 14.27 | `[GeneratedRegex]` emitted from `RegisterSourceOutput` | **CS8795** — never implemented |
| 14.28 | the identical `[GeneratedRegex]` declaration hand-written in the same project | compiles |
| 14.34 | `[GeneratedRegex]` emitted from `RegisterPostInitializationOutput` | **compiles and runs** |
| 14.35 | the same text moved to `RegisterSourceOutput`, everything else unchanged | **CS8795** |
| 14.29 | `foreach` over an `IReadOnlyList<T>` property in a generated validator | 40 bytes/pass — boxed enumerator |
| 14.30 | AOT binary: no pattern / `[GeneratedRegex]` / `new Regex(pattern)` | 1.103 / 1.119 / **1.550** MB |
| 14.32 | the same at 5 patterns — is the cost per-pattern? | 1.136 / **1.550** MB — a threshold, not per-pattern |
| 14.33 | `new Regex(p)` vs `new Regex(p, RegexOptions.None)` | +448 KB vs **+1,161 KB** |
| 14.31 | pattern match, AOT: specialized / `[GeneratedRegex]` / interpreted / `Compiled` | 1.3 / 14.3 / 38.8 / **38.7** ns |

14.17 through 14.22 are the DataAnnotations semantics §18 is specified against — read from the
runtime attributes rather than from the documentation, because two of them (anchoring, and `[Range]`
converting strings) are the kind of behaviour that is easier to reproduce by accident than to
discover by reading. 14.23 through 14.25 are the emission-shape measurements behind §9 and the
`ctx.Add*` helpers; the benchmark that produced them is at `benchmarks/ValidationModules.Benchmarks`
and the three shapes are, in order: message inlined, message composed on failure, predicate and
message both in the runtime.

14.13 through 14.15 are the ones that matter. Every signature in this document was compiled
together under the same csproj posture `DependencyModules.Runtime.csproj` uses, published with
Native AOT, and run. Output below, since it is the semantics of §3.2 and §4.2 rather than a smoke
test:

```
== generated sync validator ==
  name                   required       Error      ← declaration order
  tag                    required       Error
  sku                    pattern        Error
  home.postalCode        required       Error      ← nested path
  toys[0].name           required       Error      ← indexed path
  toys[1].name           required       Error
== runtime profile dispatch ==
  PetValidator_V2                                  ← Type → validator, no MakeGenericType
== sync structural + async business, merged ==
  valid=False errors=2
    tag                  required
    toys                 array_bounds
== async, default collector: context survives awaits ==
    name                 duplicate
    home.postalCode      unknown                   ← Push, then await, then Add
== async, synchronized collector: 500-way parallel fan-out, released together ==
  20 rounds x 500 concurrent branches, all paths exact: True
== pooled collector reuse ==
  after Reset: home.postalCode
```

On allocation, measured with `GC.GetAllocatedBytesForCurrentThread` over a published AOT binary:

```
1000 clean passes over 1 object + 1 nested + 50 elements
  total allocated : 0 bytes
  per pass        : 0 bytes
  failing pass    : 392 bytes/pass (5 errors each)
```

Zero, across 52 `Push`/`PushIndex` calls per pass, with the collector reused via `Reset()`. Plan
§4's "a validation pass that finds nothing must allocate nothing" holds under the append-only log,
which was the property most at risk from the change in §13.1. The failing path allocates only the
materialised path strings and the error records themselves.

The fan-out case is the one that earns §3.2. Each of the 500 branches captures a context from
`PushIndex`, blocks on a semaphore, and is released simultaneously, so the adds genuinely contend.
A depth-indexed path stack reports whichever sibling wrote last; the append-only log reports all
500 correctly, twenty times running.

---

## 15. FluentValidation adapter

```csharp
namespace ValidationModules.FluentValidation;

/// <summary>
/// Lets an application keep a hand-written AbstractValidator&lt;T&gt; for the handful of types that
/// need When/Must/cross-field rules while everything else stays generated. Implements the async
/// side because FluentValidation's own pipeline is async and because business rules are what it is
/// being kept for.
/// </summary>
public sealed class FluentValidatorAdapter<T> : IAsyncValidatorFor<T> {
    public FluentValidatorAdapter(
        IEnumerable<global::FluentValidation.IValidator<T>> validators,
        IValidationFieldNamer fieldNamer);

    public ValueTask ValidateAsync(
        ValidationContext context, T value, CancellationToken cancellationToken = default);
}

/// <summary>The finite mapping the adapter owns. Public so a consumer can see and extend it.</summary>
public static class FluentValidationCodeMap {
    public static IReadOnlyDictionary<string, string> Default { get; }

    /// <summary>Unmapped FluentValidation codes pass through as snake_case rather than being dropped.</summary>
    public static string ToCode(string fluentValidationErrorCode);
}
```

| FluentValidation validator | Code |
|---|---|
| `NotNullValidator`, `NotEmptyValidator` | `required` |
| `LengthValidator`, `MinimumLengthValidator`, `MaximumLengthValidator`, `ExactLengthValidator` | `string_length` |
| `RegularExpressionValidator` | `pattern` |
| `InclusiveBetweenValidator`, `ExclusiveBetweenValidator`, `GreaterThan*`, `LessThan*` | `range` |
| `EnumValidator` | `enum` |

`Severity` maps straight across, which is why `ValidationSeverity`'s values match FluentValidation's
(§4). `PropertyName` goes through the injected `IValidationFieldNamer` so the adapter cannot emit
`Home.PostalCode` where the generator emits `home.postalCode`.

The adapter forwards every failure FluentValidation produces and does **not** try to implement
`[Required]` suppression itself — it cannot, having no control over the rules it is mapping.
Writing into the collector is what gives it the rule, which is the reason §4.3 put it there. So
`RuleFor(x => x.Name).NotNull().Length(1, 100)` against a null name yields one error here, matching
the generated engine, without the adapter knowing that is the rule.

Registered **closed, per type, by the generator** — `AddScoped<IAsyncValidatorFor<Pet>,
FluentValidatorAdapter<Pet>>()` — never as an open generic, so nothing depends on MS.DI's
reflection-based open-generic activation.

---

## 16. Testing surface

Modelled on `Hardened.Requests.Testing/Conformance/`, which is the existing working example of this
pattern in the same author's code: a spec type, an adapter interface carrying a name for assertion
messages, and one shared suite that every engine runs.

```csharp
namespace ValidationModules.Testing;

/// <summary>
/// Implemented once per engine. Do the least work possible beyond invoking that engine — the point
/// of the suite is to test the engine, so anything it gets wrong should reach the assertions
/// rather than being smoothed over here.
/// </summary>
public interface IValidationEngineConformanceAdapter {
    /// <summary>Named in assertion messages so a failure identifies the engine.</summary>
    string EngineName { get; }

    ValidationResult Validate<T>(T value, Type? profile = null);
}

/// <summary>
/// The shared suite. Pins §4.2 - declaration order, Required suppression, no first-failure exit,
/// nested and indexed path shapes, code vocabulary, severity. If the generated engine and the
/// FluentValidation adapter both pass, substitutability is a fact rather than a claim.
///
/// Suppression is assertable here only because §4.3 moved it into the collector. As an emitted
/// else-if it was a property of one engine's code generation, which an adapter over a third-party
/// engine could not have reproduced - the suite would have had to carve out an exception for the
/// exact rule it most needed to check.
/// </summary>
public abstract class ValidationEngineConformanceTests<TAdapter>
    where TAdapter : IValidationEngineConformanceAdapter, new();

/// <summary>Built-in xUnit assertions underneath; these only remove the boilerplate.</summary>
public static class ValidationAssert {
    public static void Valid(ValidationResult result);
    public static void HasError(ValidationResult result, string field, string code);
    public static void NoError(ValidationResult result, string field);
    public static void ErrorCount(ValidationResult result, int expected);

    /// <summary>Asserts the exact field sequence, which is how declaration order gets pinned.</summary>
    public static void FieldsInOrder(ValidationResult result, params string[] fields);
}
```

The conformance models ship from `ValidationModules.Testing` carrying constraint attributes, so the
generated engine validates them directly and the FluentValidation adapter's test project
hand-writes `AbstractValidator<T>`s mirroring them. Both then run the same suite.

---

## 17. Deferred, deliberately

Named so they are decisions rather than oversights.

- **`[Compare]`, `[When]`, cross-field constraints.** Not expressible as a per-property attribute
  without a predicate language. Cross-field rules are what `IAsyncValidatorFor<T>` and the
  FluentValidation adapter are for. Revisit only with a concrete case.

  **Amended 2026-08-13 — §19 is that predicate language.** `rules.Ensure(x => x.Start < x.End)` in a
  declarative rule class covers the cross-field case, compiled to straight-line code. It stays out of
  the *attribute* surface, which is what this entry was about; a per-property attribute still cannot
  express it and none is being added.
- **`AttemptedValue` on `ValidationError`.** FluentValidation carries it; it forces boxing and
  retains the validated graph past the pass. Documented as dropped.
- **Localised messages / `MessageResource`.** `Message` takes a literal today. Resource lookup wants
  a story about which assembly owns the resource and how AOT sees it; not in v1.
- **Profiles (§6) and overlays (§6.4).** Specified here, never built, and their declaration surfaces
  withdrawn before 1.0.0 pinned them — a release that promises a surface should not promise members
  whose only behaviour is a build error, or none at all. Both sections are retained above as the
  design. `docs/deferred-features.md` carries the reversibility analysis.

---

## 18. DataAnnotations front-end

`System.ComponentModel.DataAnnotations` constraints are **compiled**, not invoked. The generator
reads the attribute's arguments at build time and emits the same straight-line code it emits for a
native constraint. No `ValidationAttribute` instance exists at runtime, nothing calls `IsValid`, and
`Validator.TryValidateObject` — which walks properties through `TypeDescriptor` — is never on any
path.

Structurally this costs almost nothing. Plan §7.4 already splits the generator into front-ends
feeding one IR:

```
AttributeFrontEnd        ValidationModules.Constraints  ─┐
DataAnnotationsFrontEnd  System.ComponentModel.DataAnnotations ─┼─→ ValidatedTypeModel → ValidatorEmitter
OpenApiFrontEnd          spec files, inside Hardened     ─┘
```

DataAnnotations is a third reader into `ValidatedTypeModel`. Profiles, aliasing, nesting,
registration and the emitter are untouched, and a DataAnnotations-declared rule is indistinguishable
from a native one downstream — same code, same field path, same message.

The payoff is that models already annotated for EF Core, ASP.NET model binding or Swashbuckle get
AOT-clean validators without being edited. It also dissolves the namespace collision in §1: if you
are not importing `ValidationModules.Constraints`, nothing is ambiguous.

### 18.1 Opting out

```xml
<PropertyGroup>
    <ValidationModules_DataAnnotations>Compile</ValidationModules_DataAnnotations>
</PropertyGroup>
```

`Compile` (default) | `Ignore`.

Default-on is the deliberate choice. This only ever applies to a type the generator is already
producing a validator for, so DataAnnotations attributes on an EF entity nobody validates are never
looked at. For a type that *is* validated, a `[StringLength(100)]` sitting on it is a declaration of
intent, and silently ignoring it is the worse failure. `Ignore` exists for a codebase whose
DataAnnotations attributes mean something other than validation; setting it turns every skipped
constraint into `VM0010` so the situation is visible rather than silent.

### 18.2 The mapping

| DataAnnotations | IR constraint | Notes |
|---|---|---|
| `[Required]` | `Required` | DA trims before testing, so whitespace-only fails — the same rule §12 Q5 already settled for the native attribute |
| `[Required(AllowEmptyStrings = true)]` | `Required { AllowEmptyStrings = true }` | null check only |
| `[StringLength(max)]`, `.MinimumLength` | `StringLength` | null **passes** — length rules skip null |
| `[Length(min, max)]` | `StringLength` or `ItemCount` | by member type; .NET 8+ |
| `[MinLength(n)]` / `[MaxLength(n)]` | `StringLength` or `ItemCount` | by member type — both apply to strings *and* collections in DA |
| `[Range(min, max)]` | `Range` | `MinimumIsExclusive` / `MaximumIsExclusive` map onto `ExclusiveMin` / `ExclusiveMax` |
| `[RegularExpression(p)]` | `Pattern { Anchored = true }` | **not** the same as the native `[Pattern]` — see §18.3 |
| `[AllowedValues(...)]` | `AllowedValues` | .NET 8+ |
| `[DeniedValues(...)]` | `AllowedValues { Negated = true }` | .NET 8+ |
| `[Display(Name = "x")]` | field name override | ranks with `[JsonPropertyName]` in §8's precedence |
| `ErrorMessage = "..."` | `Message` override | the emitter falls back to a literal `ctx.Add`, as it does for a native `Message` |
| `[EmailAddress]`, `[Phone]`, `[Url]`, `[CreditCard]` | — | `VM0063`, see §18.5 |
| `[Compare]`, `[CustomValidation]` | — | `VM0061`, `VM0062` |
| `IValidatableObject` | — | `VM0067` |
| any other `ValidationAttribute` subclass | — | `VM0060` |

### 18.3 `[RegularExpression]` is anchored and `[Pattern]` is not

The one divergence that would otherwise pass review. DataAnnotations requires the pattern to match
the **whole** value: `RegularExpressionAttribute` matches and then checks `match.Index == 0 &&
match.Length == value.Length`. Verified in §14.17 — `[A-Z]{3}` rejects `"xABCx"`.

The native `[Pattern]` is unanchored, and that is correct for it: JSON Schema and OpenAPI `pattern`
are unanchored, and Hardened's spec front-end depends on that reading. Both behaviours are right for
their own source.

So they are **two IR states, not one**. The IR's `Pattern` constraint carries `Anchored`, and the
emitter wraps the expression when it is set:

```csharp
// from [Pattern("[A-Z]{3}")]                → unanchored
[GeneratedRegex("[A-Z]{3}")]
private static partial Regex SkuPattern();

// from [RegularExpression("[A-Z]{3}")]      → anchored
[GeneratedRegex(@"\A(?:[A-Z]{3})\z")]
private static partial Regex SkuPattern();
```

`\A` and `\z` rather than `^` and `$`, and a non-capturing group rather than bare concatenation.
Both matter: `$` matches before a trailing newline even without `RegexOptions.Multiline`, so
`^[A-Z]{3}$` accepts `"ABC\n"`; and a top-level alternation in the user's pattern — `a|b` — would
bind wrongly without the group.

That newline hole exists for anyone hand-writing `^…$` in a native `[Pattern]` too, so
`PatternAttribute` gains the same switch:

```csharp
public sealed class PatternAttribute : ValidationConstraintAttribute {
    /// <summary>
    /// Require the whole value to match, via \A(?:…)\z. Off by default, matching JSON Schema and
    /// OpenAPI. Prefer this to writing ^…$, which still admits a trailing newline.
    /// </summary>
    public bool Anchored { get; init; }
}
```

### 18.4 Deliberate divergences

Compiling an attribute means reproducing its semantics, and two of DataAnnotations' are not worth
reproducing. Both are documented rather than silently different.

**`[Range]` does not parse strings.** DA converts at runtime, so `[Range(0, 10)]` accepts the string
`"5"` (§14.20). That behaviour exists because DA validates late-bound `object` values; we have the
member's type at build time. A `[Range]` on a member with no ordering is `VM0003`, and one whose
bounds do not parse as the member's type is `VM0065` — both build errors rather than a runtime
conversion.

**`[Required]` on non-strings keeps DA's semantics.** It passes on `0` and on an empty collection
(§14.19). That surprises people, but it is what compatibility means, and `[ItemCount(min: 1)]` is
the constraint that expresses "not empty".

### 18.5 What is not compiled

A custom `ValidationAttribute` subclass carries arbitrary C# in a method body. The only way to honour
it is to invoke it, which is the thing this front-end exists to avoid — so there is no
inheritance-based extensibility here, and that is a limitation rather than an oversight. `VM0060`
names the specific attribute and the specific property, so it is visible at the build that
introduced it. The migration path is a native constraint, or `IAsyncValidatorFor<T>` for anything
genuinely custom.

The format validators — `[EmailAddress]`, `[Phone]`, `[Url]`, `[CreditCard]` — are a closed set and
*could* be compiled, by baking in the expression each one uses. They are diagnosed instead
(`VM0063`, pointing at `[Pattern]`). Reproducing them means committing to bug-compatibility with
implementations that are lenient in ways nobody wants: DA's `EmailAddressAttribute` accepts `"a@b"`
(§14.22). A user who wants email validation is better served by a pattern whose behaviour is written
down in their own source than by inheriting ours.

### 18.6 Mixing with native constraints, and profiles

Both sets can sit on one type, and on one property. They **union**, the same rule overlays follow
(§6.4). Contradictory bounds for one property are `VM0066`.

DataAnnotations attributes have nowhere to put `FromProfile`, `UntilProfile` or `Profiles`, so every
constraint they contribute is unattributed — which by rule 1 of §6.2 means it applies in every
profile, including the default. That is the consistent reading rather than a special case: a
DataAnnotations-only model gets exactly one validator, and native attributes are what you reach for
on the properties that need to vary by profile.

### 18.8 Patterns and Native AOT

Plan §2 mandates `[GeneratedRegex]`. It is unavailable to a source generator — generators cannot see
each other's output, so a partial method we declare is never implemented and the consumer's build
fails with CS8795 (§14.27, and §14.28 shows the identical declaration hand-written compiles). The
plan's §7.2 note about generators not seeing each other applies to *every* generator, not only
DependencyModules'.

The alternative, a `static readonly Regex` built once, is correct and publishes AOT-clean — zero IL
warnings. What it costs is size:

| | AOT binary | vs no pattern |
|---|---|---|
| no pattern | 1.103 MB | — |
| `[GeneratedRegex]` × 1 | 1.119 MB | +16 KB |
| `[GeneratedRegex]` × 5 | 1.136 MB | +33 KB |
| `new Regex(pattern)` × 1 | 1.550 MB | **+448 KB** |
| `new Regex(pattern)` × 5 | 1.550 MB | **+448 KB** |

Two properties matter more than the headline number.

**It is a threshold, not a per-pattern cost.** One runtime-constructed `Regex` and five cost exactly
the same, because what is being paid for is rooting the parser and the interpreter — the pattern
strings themselves are noise. `[GeneratedRegex]` compiles each pattern to code and needs neither, so
it starts 28× cheaper and grows at about 4 KB per pattern. A corollary worth stating: if the
application already constructs a `Regex` at run time anywhere else, an inline pattern here adds
nothing further.

**Never pass `RegexOptions` when there is nothing to say.** `new Regex(pattern,
RegexOptions.None)` measures **713 KB larger** than `new Regex(pattern)` — more than the engine
itself costs. The single-argument constructor lets ILC prove `RegexOptions.Compiled` is never set
and trim the `RegexCompiler` path with it; passing the enum defeats that analysis. The emitter omits
the argument unless a constraint actually sets options. This one is easy to reintroduce by tidying,
so it carries a comment at the emission site.

So the inline form is **rejected in an AOT-facing project**, and the reference form points at a
`[GeneratedRegex]` the consumer declares. Their declaration is in the original compilation, so the
regex generator implements it and the generated validator calls it:

```csharp
public static partial class PetPatterns {
    [GeneratedRegex("^[A-Z]{3}$")] public static partial Regex Sku();
}

[Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
public string? Sku { get; init; }
```

The member is resolved at generation time — it must exist, be static, take no parameters, be
accessible, and return `Regex` — so a typo is `VM0018` rather than something discovered later.

`ValidationModules_PatternPolicy` = `Auto` (default) `| Error | Warn | Allow`. `Auto` rejects the
inline form when `PublishAot` **or** `IsAotCompatible` is set. Gating on `IsAotCompatible` too is
deliberate: `PublishAot` is only ever true in the executable, so a class library holding the models
would never see it, and the failure would land on someone else's publish instead of its own build.
A library shipping to AOT consumers should set `Error` outright.

**The limit of this.** It is transitive-blind. A library that compiled an inline pattern into itself
ships an interpreted `Regex`; when an application AOT-publishes, our generator never runs there and
nothing fires — the 1.1 MB lands anyway. Nothing short of an analyzer inspecting references can
catch that, which is why the reference form is documented as the default rather than as an escape
hatch.

| ID | Severity | |
|---|---|---|
| VM0017 | Error under `Auto` + AOT; otherwise as configured | inline pattern roots the regex engine |
| VM0018 | Error | referenced regex member is missing, not static, inaccessible, parameterised, or not a `Regex` |

---

### 18.7 Diagnostics

Added to the table in §11:

| ID | Severity | |
|---|---|---|
| VM0060 | Warning | custom `ValidationAttribute` subclass — cannot be compiled, not applied |
| VM0061 | Warning | `[Compare]` — cross-field, not expressible as a per-property constraint |
| VM0062 | Warning | `[CustomValidation]` — dispatches reflectively, not applied |
| VM0063 | Warning | `[EmailAddress]`/`[Phone]`/`[Url]`/`[CreditCard]` — not applied; use `[Pattern]` |
| VM0064 | Error | `[MinLength]`/`[MaxLength]` on a member that is neither a string nor a collection |
| VM0065 | Error | `[Range]` bounds do not parse as the member's type |
| VM0066 | Warning | a DataAnnotations and a native constraint conflict on one property |
| VM0067 | Warning | type implements `IValidatableObject` — not compiled |

Warnings rather than errors throughout, except where the attribute is simply wrong for the member.
A build should not break because a model picked up `[EmailAddress]` for some other consumer's
benefit; it should tell you the constraint is not being enforced.

---

## 19. Declarative rule classes

**Written:** 2026-08-13. Resolves §12 Q1 a second time — see §19.10 for what this does to §6.4 — and
closes the cross-field deferral in §17.

A third way to declare rules, alongside native constraint attributes (§5) and DataAnnotations (§18):
a class that describes them in a method body.

```csharp
public sealed class PetRules : IValidationRulesFor<Pet> {
    public void Describe(ValidationRules<Pet> rules) {
        rules.Required(x => x.Name).Length(1, 100);
        rules.Pattern(x => x.Sku, Patterns.Sku);
        rules.Range(x => x.Age, 0, 30);
        rules.Nested(x => x.Home);
        rules.Count(x => x.Toys, 1, 10).Each();

        rules.Ensure(x => x.Start < x.End);
        rules.Ensure(x => x.Discount <= x.Price * 0.5m, code: "discount_too_large");

        rules.Apply(PetChecks.SkuChecksum);
    }
}
```

It exists for the case attributes cannot reach — `Pet` comes from a package nobody here owns — and
it happens to be the only declaration form that can express a rule spanning two properties.

### 19.1 The declaration has two consumers, and that is the whole design

`Describe` is both **read at build time** and **run at runtime**, and the two must agree:

| | reads or runs | cost per rule | needs our generator |
|---|---|---|---|
| `ValidatorEmitter` via `RulesFrontEnd` | reads the syntax, flattens to straight-line code | a branch | yes |
| `DescribedValidator<T>` | runs `Describe` once in its constructor | an interface dispatch and a delegate call | no |

So the interface is the portable contract and the generator is an optimizer that erases its cost.
That is what makes it usable by a *different* source generator: emit a rules class, register it,
and validation works with none of our build-time machinery present. Plan §7.2 already settled that
cross-assembly convention matching is unavailable — each assembly emits and registers its own
validators — so a rules class arriving from a referenced assembly is precisely the case our
generator cannot serve and this one can.

`Describe` runs exactly once, in a singleton's constructor. Plan §2's "rule graphs are built once,
never per validation call" holds by construction on both paths.

**Selectors are `Func<T, TValue>`, never `Expression<Func<T, TValue>>`.** Plan §2 bans
`Expression.Compile`, and an expression tree would need compiling to be executable. What replaces it
is §19.3.

### 19.2 Discovery

```csharp
namespace ValidationModules;

public interface IValidationRulesFor<T> {
    void Describe(ValidationRules<T> rules);
}
```

The interface is the marker; there is no attribute. The generator's candidate provider already walks
every `TypeDeclarationSyntax` (`ValidationSourceGenerator.cs:68`), so there is no
`ForAttributeWithMetadataName` fast path being given up, and an attribute would be one more thing to
forget.

**The rules class is not the validator.** The generator still emits `PetValidator`, because `Pet` may
also carry attributes and both must fold into one class — two models with one `ValidatorName` collide
on hint name and `AddSource` throws, which fails the whole generator rather than one type
(`ValidationSourceGenerator.cs:134`). Two rules classes for one type union the same way. The rules
class therefore needs no `partial`, and its own name is free.

**A rules class dissolves the §1 namespace collision.** `Required`, `Range` and `Length` are methods
here, not attribute types, so a file declaring rules never imports `ValidationModules.Constraints`
and can never hit `CS0104`.

### 19.3 Field inference — `CallerArgumentExpression`

Every selector-taking method carries a `[CallerArgumentExpression]` parameter, so the compiler hands
the runtime the selector's **source text** at no cost and with no expression tree:

```csharp
public PropertyRules<T, TValue> Range<TValue>(
    Func<T, TValue> value,
    TValue min,
    TValue max,
    string? field = null,
    [CallerArgumentExpression(nameof(value))] string? selector = null);
```

`"x => x.Age"` → strip the parameter → `Age` → `IValidationFieldNamer` → `age`. Scanned once, when
the rule set is built, never per validation. The generator ignores the argument and resolves the
same selector semantically, reaching the same name.

`field:` overrides inference, and a selector that is not a simple property path is `VM0071`.

### 19.4 The vocabulary — anchored chaining

The first call carries the selector; the rest inherit it. There is no `For` ceremony, though `For`
exists for when the anchor reads better stated.

```csharp
namespace ValidationModules;

public sealed class ValidationRules<T> {
    public PropertyRules<T, TValue> For<TValue>(Func<T, TValue> value, string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null);

    public PropertyRules<T, TValue> Required<TValue>(Func<T, TValue> value, ...);
    public PropertyRules<T, string?> Length(Func<T, string?> value, int min = 0, int max = int.MaxValue, ...);
    public PropertyRules<T, TValue> Range<TValue>(Func<T, TValue> value, TValue min, TValue max, ...)
        where TValue : IComparable<TValue>, IFormattable;
    public PropertyRules<T, string?> Pattern(Func<T, string?> value, Func<Regex> pattern, ...);
    public PropertyRules<T, TValue> AllowedValues<TValue>(Func<T, TValue> value, params TValue[] allowed);
    public PropertyRules<T, TCollection> Count<TCollection>(Func<T, TCollection?> value, int min, int max, ...);
    public PropertyRules<T, TValue> Nested<TValue>(Func<T, TValue?> value, ...);
    public PropertyRules<T, TCollection> Each<TCollection>(Func<T, TCollection?> value, ...);

    public ValidationRules<T> Ensure(Func<T, bool> predicate, string? field = null, string? code = null,
        string? message = null, [CallerArgumentExpression(nameof(predicate))] string? expression = null);

    public ValidationRules<T> Apply(RuleAction<T> rule);
}

public delegate void RuleAction<in T>(ref ValidationContext context, T value);
```

`PropertyRules<T, TValue>` carries the anchor — field name, accessor, selector text — and repeats the
same vocabulary without the selector. The members that are legal only for particular value types are
**extension methods constrained on the receiver's type argument**, which is how `Length` is offered on
`PropertyRules<T, string?>` and not on `PropertyRules<T, int>` — an instance method cannot be
constrained that way, and the alternative is a runtime check for something the compiler should catch.
Same split as `ValidatorForExtensions` and `ValidationContextExtensions`.

Codes, messages and severity are exactly §4.1 and §4's composed text. A rule declared here and the
same rule declared as an attribute are the same `ConstraintModel` before the emitter sees either.

### 19.5 `Ensure` — the exit from the vocabulary

For rules with no schema meaning: cross-field comparisons, arithmetic, anything the six constraints
cannot say. §17 deferred these as "not expressible as a per-property attribute without a predicate
language"; a method body is that language.

**The message is the predicate, rendered.** `CallerArgumentExpression` supplies the source text, the
parameter is stripped, member accesses off it take their wire names, and a period is appended:

| written | message |
|---|---|
| `x => x.Start < x.End` | `start < end.` |
| `x => x.Age is >= 0 and <= 30` | `age is >= 0 and <= 30.` |
| `x => x.Name.Length is >= 1 and <= 100` | `name.Length is >= 1 and <= 100.` |
| `x => !string.IsNullOrWhiteSpace(x.Name)` | `!string.IsNullOrWhiteSpace(name).` |
| `x => Patterns.Sku().IsMatch(x.Sku)` | `Patterns.Sku().IsMatch(sku).` |

Three properties this has that a composed message does not:

- **It cannot drift.** A composed message repeats a bound that someone can edit without editing the
  text. This message *is* the rule.
- **Both engines produce it identically**, because both start from the same string — the generator
  bakes a literal, the runtime renders once at rule-build time. This is the reason predicates are
  never lowered to vocabulary constraints: recognising `x.Age is >= 0 and <= 30` as a `Range` is easy
  syntactically and impossible for the runtime without shipping a parser, and two engines disagreeing
  on a code is the one thing §16 exists to prevent.
- **It is redaction-safe by construction.** The text is compile-time source, so no runtime value can
  reach it; a render can only ever contain schema, which HANDOFF §3.3 already classifies as
  publishable. The three-policy ladder in HANDOFF §3.2 does not apply to `Ensure` and needs no
  plumbing here. The residual is an author embedding a secret as a literal in a predicate, which is a
  secret-in-source problem.

**Whitespace is normalised — runs collapse to a single space — on both sides.** The compiler's
`CallerArgumentExpression` text and the generator's `argumentSyntax.ToString()` are both the
expression's source span and should be byte-identical, but interior trivia in a multi-line lambda is
where they would part company, and the design rests on them not parting. Normalising makes the
question moot and stops a reformatted lambda changing a message.

**The last two renders are bad, and that is the signal.** Both are cases the vocabulary has a word
for — `Required` and `Pattern` — each shorter than what was written. An ugly message means the wrong
tool, visible without reading a diagnostic, and `VM0073` says so as a suggestion with a code fix.

#### The code does not derive

`code` defaults to `ValidationCodes.Predicate` (`"predicate"`, new in §4.1) and is overridable.
Deriving it from the expression — slug or hash — was rejected: message and code have opposite churn
requirements. The message is human-facing and *should* track the rule; the code is a wire contract,
and `ValidationCodes`' own remarks exist so a client switching on `Code` does not break. Derive it
and widening `30` to `35` becomes a breaking change for every such client, and reordering does it too
if the code carries an ordinal.

Two `Ensure`s on one field both report `predicate`, distinguished by field and message. A client that
needs to tell them apart is branching on an ad-hoc rule, which is what the fixed vocabulary is for —
name the code and it becomes part of the contract deliberately.

**Cost, from this repo's own measurement.** `ValidatorEmitter.cs:14-19` priced a literal message at
107 of the 313 native bytes a constraint site costs, because messages embed field names and so
nothing deduplicates in the string heap. Every `Ensure` is a literal. It is fatter under AOT than the
composed path, which is a second reason it is the escape rather than the default spelling.

#### What a predicate may reference

**Its own parameter, and static or constant state. Nothing else.** No closure over `Describe`'s
locals and no `this`, because neither exists in the emitted validator — the generator lifts each
predicate to a static method and the runtime holds a delegate, and only this rule makes those two the
same thing. Violations are `VM0072`.

The generator lifts predicates into a **separate generated file per rules class, carrying that file's
`using` directives**. Copying a lambda into the validator file does not compile — `x => x.Status ==
Status.Active` needs the `using` that was in the author's file — and the alternatives are a
symbol-qualifying rewriter that has to reduce extension-method invocations, or this, which is a few
lines. It also keeps the predicate readable in `obj/…/generated`, which HANDOFF §3.5 leans on.

**Field inference for `Ensure`** takes the first member access off the parameter: `x => x.Start <
x.End` anchors to `start`. A predicate with no member access off its parameter, and no `field:`, is
`VM0075`. Anchoring matters beyond the label — an anchored `Ensure` is ordered with that property's
other rules, so collector suppression (§4.3) drops it when `required` has already failed on the
field.

### 19.6 `Apply` — a method group, not a name

```csharp
rules.Apply(PetChecks.SkuChecksum);     // static void SkuChecksum(ref ValidationContext ctx, Pet value)
```

Emitted as `global::PetChecks.SkuChecksum(ref ctx, value);`, held as a `RuleAction<T>` at runtime.

A `(Type, string)` pair — the shape `[Pattern(typeof(Patterns), "Sku")]` uses — is deliberately not
offered. That form exists only because an attribute cannot hold a method group. A method body can, so
the constraint that forced it does not apply, and taking the group directly gets compile-time
checking, go-to-definition and rename for free with no registry to keep in sync.

`Ensure` is sugar over `Apply`: the generator writes the static method you would otherwise have
written by hand.

### 19.7 Ordering

§4.2 is unchanged and this has to fit inside it: errors emit in **property** order, and both engines
have to agree on what that means when the rules were not written on the properties.

1. Rules group by field. Within a field, `Required` first, then declaration order.
2. Fields order by **source order** when the type carries constraint attributes of its own, and by
   **first mention in the `Describe` body** when it does not.
3. `Apply` rules own no field and run last, in declaration order.

Rule 2 is the one that had to be settled by looking at both engines. `DescribedValidator<T>` has only
the body to go on — it cannot see property source order without reflection — so a rules-only type has
to be ordered by first mention or the two engines disagree on sequence for the same declaration. A
type that *does* carry attributes keeps source order, because at that point source is where the rules
were written and mixing the two orderings on one type would be worse than either. Two rules classes
for one type contribute in a deterministic order: by class name, ordinal.

**A predicate never joins the `else if` chain.** The chain after a `Required` is an optimisation
(§4.3) that skips tests whose result would be discarded, and a predicate may read fields other than
its anchor, so an earlier failure on the anchor says nothing about it. Emitting one as `else if`
would make the generated engine report fewer errors than the runtime one — which is what happened
first, and what `RulesClassTests` now pins.

### 19.8 The runtime engine

```csharp
namespace ValidationModules;

/// <summary>
/// Runs a rules class without the generator. Describe runs once, in the constructor.
/// </summary>
public sealed class DescribedValidator<T> : IValidatorFor<T> {
    public DescribedValidator(IValidationRulesFor<T> rules);
    public void Validate(ref ValidationContext context, T value);
}
```

```csharp
namespace Microsoft.Extensions.DependencyInjection;

public static class DescribedValidatorExtensions {
    public static IServiceCollection AddDescribedValidator<T, TRules>(this IServiceCollection services)
        where TRules : IValidationRulesFor<T>, new();
}
```

Singleton, so `Describe` runs once per process. Each rule is a small sealed class over the accessor
and its bounds — no reflection, no `MakeGenericType`, nothing to trim-root. It reaches the same
`ValidationContext` and the same collector, so suppression, ordering and paths are the collector's
and are shared with generated validators for free (§4.3).

**Double registration is the hazard.** `ValidationRunner<T>` merges every registered
`IValidatorFor<T>` (§7), so if the generator compiled `PetRules` into `PetValidator` *and* something
calls `AddDescribedValidator<Pet, PetRules>()`, every error appears twice with nothing to tell the
two apart. Within one compilation the generator can see both and reports `VM0074`. Across
compilations the hazard cannot arise from the generator, because it does not compile rules classes
out of referenced assemblies — that is the case the runtime path exists for. The one uncovered
arrangement is assembly A running the generator over `PetRules` while assembly B separately calls
`AddDescribedValidator`; documented, not detectable.

### 19.9 Where the two engines diverge

Pinned by §16's conformance suite, which gains a second adapter so the same suite runs against
`DescribedValidator<T>` and against generated validators.

**Field naming.** §8 puts `[JsonPropertyName]` above `[DataMember]` above the MSBuild policy. The
generator reads all three; the runtime can read none of them without reflection, so it names from
selector text plus `IValidationFieldNamer` alone. A property carrying `[JsonPropertyName("pet_name")]`
is `pet_name` from one engine and `name` from the other. `field:` on the call is the escape, and it is
the right answer for a rules class emitted by another generator, which knows the wire names already
and should pass them.

**Nothing else.** Codes, messages, ordering, suppression and paths are identical, because they come
from the same `ValidationCodes`, the same `ValidationContextExtensions` and the same collector.

### 19.10 What this does to §6.4

The mirror-property overlay stays specced and unimplemented; a rules class is the recommended form.
`ctx.Required(x => x.Tag)` gets name and type checking from the C# compiler, which is what §6.4
claims `VM0030`/`VM0031` for, without the diagnostics and without restating the property list.

### 19.11 Diagnostics

Added to the table in §11:

| ID | Severity | | Status |
|---|---|---|---|
| VM0070 | Error | a statement in `Describe` is not a rule declaration | implemented |
| VM0071 | Error | a selector is not a simple property path | implemented |
| VM0072 | Error | a predicate references state outside its parameter | implemented |
| VM0073 | Info | a predicate matches a vocabulary constraint; the named form has a code and a composed message | **not implemented** |
| VM0074 | Warning | `AddDescribedValidator` for a rules class this compilation already compiled | **not implemented** |
| VM0075 | Error | an `Ensure` has no inferable field and no `field:` | implemented |

The two unimplemented IDs are reserved, not shipped: neither has a descriptor and neither appears in
`AnalyzerReleases.Unshipped.md`, so nothing claims to enforce them. VM0073 is a quality-of-life
suggestion. VM0074 needs a syntax scan for `AddDescribedValidator` call sites combined with the set
of rules classes this compilation compiled, and until it exists the double-registration hazard in
§19.8 is documented rather than caught.

**VM0071 is stricter than the runtime.** An `Ensure` whose anchor does not resolve to a property of
the validated type is an error here, while `DescribedValidator<T>` accepts it as long as `field:` is
supplied. The reason is §19.7: a rule is emitted inside its anchored property's chain so that both
engines order errors the same way, and there is nowhere to put one that belongs to no property. That
`field:` renames rather than detaches is the deliberate part.

**`Describe` is a whitelisted DSL, not general C#.** A local, a loop, an `if`, a call to anything that
is not on the builder is `VM0070` — a build error, never something silently dropped. The body being
runnable makes it look like ordinary code, which is exactly why the unsupported half must break the
build rather than behave differently on the two paths.

### 19.12 Not offered

- **Profiles on rules classes.** Stage 3. The seam is that every builder call already returns a
  builder, so `.From<V2>()` / `.Until<V3>()` / `.InProfiles(...)` are additive when profiles land.
- **`Describe` overloads taking dependencies.** A rules class is a declaration; anything needing I/O
  is `IAsyncValidatorFor<T>` (§7).
- **Runtime-mutable rule sets.** `Describe` runs once. A rule that varies per request is a business
  rule.
