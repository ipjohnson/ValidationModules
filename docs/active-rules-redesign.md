# Active rule classes — redesign of the `IValidationRulesFor<T>` surface

**Status:** agreed 2026-08-29, not yet implemented. Supersedes `API-SURFACE.md` §19 and the
rules surface shipped in 1.0.0. Breaking, deliberately: no consumer outside this repo references
the rules surface (verified against `~/Hardened` — zero hits), so the break is priced at a
snapshot re-verify and a `RuntimeContract.Version` bump, and it only stays that cheap now.

**Origin:** designed across a working session on 2026-08-28/29. This document is self-contained;
implement from it without that session's history. Where it contradicts `API-SURFACE.md` §19 or
`IMPLEMENTATION-PLAN.md` §2's phrasing, this document wins and §14 below lists the redraws.

---

## 1. The idea

A rules class is C# that is **read, never run.**

```csharp
internal sealed class AddressInputRules : IValidationRulesFor<AddressInput> {
    public static void Describe(ValidationRules<AddressInput> rules, AddressInput x) {
        rules.Require(x.Street);
        rules.Require(x.PostalCode);
    }
}
```

The source generator transcribes `Describe` into the generated validator. There is no runtime
engine, no interpretation, no reflection: vocabulary calls are recognized islands the generator
expands into check-and-report code, and every other statement — locals, LINQ, arithmetic,
`if`/`else` — is transcribed verbatim and runs at validation time *inside the generated
validator*. The rules class itself is never instantiated and never executed; under trimming and
Native AOT it disappears entirely (§12).

The 1.0.0 surface this replaces declared rules through selector lambdas
(`rules.Required(x => x.Street)`) with two consumers by design: the generator flattened the body,
and `DescribedValidator<T>` ran `Describe` once at startup as a portable interpreter. Both the
spelling and the dual-engine architecture are replaced. Reasons, in the order they were decided:
`Require(x.Street)` reads better than `Required(x => x.Street)`; control flow should be real
`if`/`else`, not `When`-closures; the runtime engine's use cases (rules classes from assemblies
that never ran the generator) are speculative with zero known consumers, and deleting it removes
the two-engines-must-agree problem class entirely while making the declaration layer trimmable.

## 2. Authoring model

```csharp
namespace ValidationModules;

public interface IValidationRulesFor<T> {
    static abstract void Describe(ValidationRules<T> rules, T x);
}
```

- **`Describe` is `static abstract`.** Nothing ever instantiates a rules class; `this` does not
  compile, so instance-state hazards are enforced by the language rather than by diagnostics.
- **The surface is inert by construction.** `ValidationRules<T>` and `PropertyRules<T,TValue>`
  get internal constructors and members that throw `NotSupportedException` — nobody can construct
  the builder, so nobody can invoke `Describe` meaningfully. The method exists to be read.
- **`x` is symbolic.** It never holds a value; it exists so member access typechecks, renames
  propagate, and go-to-definition works. The generator resolves `x.Street` as a symbol.
- **A breakpoint in `Describe` never hits.** This will be the support question forever; the
  answer ships in the docs (§14): step through the generated validator under `obj/…/generated`,
  which is readable straight-line code.

The canonical example, exercising every feature in this spec:

```csharp
internal sealed class OrderRules : IValidationRulesFor<Order> {
    public static void Describe(ValidationRules<Order> rules, Order x) {
        rules.Require(x.Number).Length(4, 12);
        rules.Range(x.Quantity, 1, 100);
        rules.Nested(x.ShippingAddress);
        rules.Count(x.Lines, 1, 50).Each();

        AuditRules.Standard(rules, x);                        // fragment: read and inlined (§7)

        if (x.International) {                                // control flow is just C#
            rules.Require(x.CustomsCode).Pattern(Patterns.CustomsCode);
        }

        var total = x.Lines.Sum(l => l.Price * l.Qty);        // computation: transcribed, runs per validation
        rules.Ensure(total <= x.CreditLimit);                 // message: "total <= creditLimit."

        if (!Luhn.Validates(x.AccountNumber)) {               // free-form logic + reporter tier (§9)
            rules.Context.Report(nameof(x.AccountNumber),     // nameof(x.…) → wire name (§8)
                "checksum", "account number failed its checksum");
        }
    }
}

public static class AuditRules {
    // generic fragment: the mixin attributes never had
    public static void Standard<T>(ValidationRules<T> rules, T x) where T : IAudited {
        rules.Require(x.CreatedBy);
        rules.RangeAtLeast(x.Version, 1);
    }
}
```

## 3. Execution model: transcription

The emission strategy is **positional transcription**, not collect-and-regroup. The generated
region mirrors the `Describe` body statement for statement; vocabulary calls are expanded in
place; ordinary statements are copied through.

- **Islands** — vocabulary calls, `Ensure`, fragment calls, `rules.Context.…`, `nameof(x.…)` —
  are recognized and transformed.
- **Everything else** is transcribed verbatim and executes at validation time.
- **Each rules class becomes its own region**, emitted as its own method in the per-class
  companion file that carries the author's `using` directives — the same recorded CSharpAuthor
  exception `PredicateEmitter` established (`IMPLEMENTATION-PLAN.md` §7.6): the structure is
  CSharpAuthor; user expressions are the author's source. This extends that exception; it does
  not create a second kind.
- **Region method parameters reuse the author's parameter names** (`x` stays `x`), so
  transcription needs no identifier rewriting except `rules.Context` → the context parameter.
- **`return` means "end this rules class's checks"** — the region is a method, so an early
  return skips the rest of the region and nothing else (attribute rules and other rules classes
  still run).
- **Ordering:** the attribute-declared region first (source order, §4.2 semantics unchanged),
  then one region per rules class, classes ordered by name (ordinal), statements in body order.
  §19.7's field-grouping rule for rules classes is replaced by body order — now trivially true,
  because the body *is* the validator.
- **Suppression survives without grouping.** A chain (`rules.Require(x.Number).Length(4, 12)`)
  is one contiguous expression and emits as `if`/`else if`. Across separate statements,
  suppression is the collector's job (field-keyed: later errors on a field whose `required`
  already failed are dropped), so it applies to vocabulary rules and free-form reports alike,
  automatically.
- **Semantics are C# semantics.** Conditions evaluate where written. The old once-per-pass
  condition hoisting existed so two engines would agree; with one engine, what the code says is
  the spec.

## 4. The two invariants

Everything else in the old whitelist relaxes; these two are load-bearing and non-negotiable.

1. **`rules` flows only where the generator can follow.** Phrased as a blacklist, mirroring §5:
   passing `rules` to a static, `void` method declared in this compilation is a fragment call and
   is followed, whatever the rest of its signature (§7). Disallowed is every flow the generator
   cannot read through: storing `rules` (or a chain value) in a local, field, or collection;
   capturing it in a lambda or local function; converting a fragment to a delegate; returning it;
   passing it to an instance, virtual, or non-void target — or to a cross-assembly one (VM0076,
   §7). This is the anti-silent-drop rule: a rule call the generator cannot see would transcribe
   into a call on the inert builder and validate nothing. (EF Core learned this lesson with
   implicit client-eval and made it an error; so do we.) A guarantee for free:
   `ValidationRules<T>` has no public constructor, so a method receiving it can only ever be
   called from inside a `Describe` body — the type system keeps fragments unreachable from
   unreadable contexts.
2. **Transcribed code must compile at the emission site.** `this` is impossible (static).
   What remains is an accessibility walk: `private`/`protected` members of the rules class and
   `file`-local types are unreachable from the companion file → diagnostic ("make it internal"),
   caught before the error can surface inside generated code (§7.5's worst place).

## 5. The blacklist

Short, and every item has a one-line reason:

| Rejected in `Describe` | Because |
|---|---|
| Island calls (vocabulary, `Ensure`, fragments) inside loops, lambdas, or local functions | Islands need generator-computed identity; collections are `Each`'s job; free-form reporting covers the exotic case (§9) |
| `await` / `yield` | Business rules are `IAsyncValidatorFor<T>` |
| Assignment to `x`'s members | Validation does not mutate its subject (we flag the detectable form; the rest is convention) |
| `goto`, `unsafe`, `lock`, `try`/`catch`/`finally`, `using` statements | v1-rejected exotica; admit later if a real case appears |

Allowed: local declarations, expression statements, `if`/`else`, `switch`, `for`/`foreach`/
`while` as *computation*, `return`, interpolated strings, calls to accessible helpers.

**Purity is now convention, not construction.** A database call in `Describe` compiles and runs
per validation. The old whitelist made "a rules class is a declaration; I/O goes in
`IAsyncValidatorFor<T>`" a build error; it becomes a documented line (§14). This is a line
moving, redrawn deliberately.

## 6. Vocabulary changes

- **Values, not selectors.** Every builder method flips from `Func<T,TValue>` to `TValue`; the
  overload matrix keeps its shape with the selector peeled. All `[CallerArgumentExpression]`
  parameters are deleted — the generator resolves arguments semantically.
- **`Required` → `Require`.** Imperative verbs suit an active surface. The rest of the
  vocabulary (`Length`, `Range`, `Pattern`, `Nested`, `Count`, `Each`, `Unique`,
  `AllowedValues`, `MultipleOf`, …) keeps its names. `Pattern` keeps `Func<Regex>`.
- **`When`/`Unless`/`ConditionalRules<T>` deleted.** Control flow is `if`/`else`.
- **`PropertyRules<T,TValue>.And` deleted.** Chains are single expressions now.
- **Field inference:** an island's value argument must be a member path on `x` — nested paths
  (`x.Home.PostalCode`) and `?.` allowed (`?.` is simply the nested-path spelling; emitted code
  null-guards anyway). Anything else requires `field:`. An explicit `field:` string remains a
  raw wire name on the author's head (existing contract, unchanged).
- **`Ensure(bool condition, string? field = null, string? code = null, string? message = null,
  ValidationSeverity severity = Error)`.** The generator captures the condition expression
  syntactically. §19.5's message rendering carries over: strip the parameter, wire-name the
  member accesses, normalize whitespace, append a period. Locals may now appear in messages
  (`total <= creditLimit.`) — local naming is user-facing text; one doc sentence makes it a
  feature, not a surprise. Anchor is the first member access off `x`; no anchor and no `field:`
  is an error (VM0075 retained).

## 7. Fragments

Decomposition and reuse are method extraction, read by the generator.

- **Recognition — any call `rules` can reach:** a call passing `rules` to a `static`, `void`
  method declared **in the same compilation** is a fragment call, whatever the rest of its
  signature. Parameter order is free; parameters beyond `rules` become locals bound to the
  call-site argument expressions (`CustomsRules.Declare(rules, x, strict: x.Value > 10_000m)`)
  and the body is inlined at the call site under the same transcription rules as `Describe`
  itself — same ordering, same suppression, same `RuleText`. The parameter typed `T` is the
  fragment's subject: its argument must be `x` (v1 — projected subjects like `x.Billing` need
  path-prefix composition and are future work); field inference and the `nameof` rewrite (§8)
  work through it, and a fragment with no `T` parameter may still compute and report with
  explicit `field:`. Fragments may call fragments; cycles are an error, not a hang.
- **Same-compilation is load-bearing:** the generator reads syntax, and a referenced assembly
  has none. Its own diagnostic, because the failure mode is otherwise a silently-unflattened
  call.
- **Generic fragments** with constraints are the payoff (`AuditRules.Standard<T> where T :
  IAudited` in §2): "every audited type gets these rules," said once, stamped out per concrete
  type — members resolved against the *concrete* type at each instantiation, so
  `[JsonPropertyName]` on the implementing property wins for field naming.
- **The line to helpers:** a method that *receives* `rules` is a fragment — read and expanded,
  the §5 blacklist enforced inside (violations reported in the fragment's body). A method that
  doesn't is a plain computation helper — transcribed as a call, never read, subject only to
  invariant 2. Methods that receive `rules` are read; methods that don't are run.

### Cross-assembly: fragments travel as source

A fragment is inlined from *syntax*, and a referenced assembly ships IL — the symbol has no body
to read. (An IDE host sometimes holds the referenced project's compilation and could see syntax;
the CLI build never can, and a generator must not emit different code per host, so that door is
closed deliberately.) The same-compilation rule is therefore physics, not policy — and a plain
`ProjectReference` is on the wrong side of it, which is exactly the shape a shared in-house
library takes.

The gap is narrow: attribute-declared rules on a shared library's *own types* already cross
assemblies — that assembly runs the generator, ships its validators, and consumers compose via
`Nested` and its registration module (§7.2's model). The hole is only rules aimed at types the
shared assembly has never seen — the mixin.

**v1, normative: shared fragments travel as source**, so they land in each consumer's
compilation and inline at full fidelity, concrete-type name resolution included:

- in-solution: a Shared Project (`.shproj`) or linked `Compile` items — a plain
  `ProjectReference` does **not** work, and VM0076 must say so;
- distributed: a source-only package — the §7.4 / Impl pattern this repo already ships
  (`IncludeBuildOutput=false`, compile items added via `build/*.targets`).

VM0076's message teaches the fix: *"fragment 'AuditRules.Standard' is compiled IL from a
referenced assembly; fragments must be part of this compilation — use a shared project or a
source package."*

**Reserved, additive — delegation for rules classes** (build when IL-shipping demand is real):
a shared assembly declares `AuditRules : IValidationRulesFor<IAudited>` (interface targets are
already legal — `[GenerateValidator]` allows `AttributeTargets.Interface`), runs the generator
itself, and ships the generated `IValidatorFor<IAudited>`. A consumer writes
`AuditRules.Describe(rules, x)` — the same call shape, typechecking through the static abstract —
and the consumer's generator, finding the target cross-assembly, emits a direct call to the
shipped validator, located by its deterministic name via `GetTypeByMetadataName`. No scanning;
§7.2-aligned (each assembly emits its own validators, consumers compose by direct reference);
`IValidatorFor<in T>`'s contravariance also lets DI registration compose it. Fidelity is lower
and documented: field names resolve at the *interface* (a consumer renaming the implementing
property's wire name diverges), struct implementers box through the interface call, and
`RuleText` stays in the declaring assembly. A cross-assembly target whose validator cannot be
found (the shared assembly never ran the generator) is its own §7.5-grade diagnostic. The
furthest rung — a compiled-companion protocol (`ValidationFlow Standard<T>(ref
ValidationContext, T) where T : IAudited` plus a marker attribute read from metadata) that keeps
constraint-genericity without boxing — is noted, not designed.

## 8. Field names: `nameof` through the parameter

Where free-form code needs a field name, the spelling is standard C#:

- **`nameof(x.P…)` — through the parameter — rewrites to the wire path relative to `x`**,
  resolved from the symbol: `[JsonPropertyName]` first, then the field namer.
  `nameof(x.AccountNumber)` → `"accountNumber"`; `nameof(x.Home.PostalCode)` →
  `"home.postalCode"`. Applies anywhere in the transcribed body, including inside interpolated
  strings.
- **`nameof(Order.P)` — through a type — is untouched**, ordinary C# yielding the CLR name.
  That is the deliberate escape hatch when the CLR name is really wanted.
- `nameof(x)` alone and `nameof` of non-property members: untouched.

This replaces the earlier `rules.Field(x.Name)` island proposal: standard syntax, zero new API,
rename-safe, and go-to-definition works.

## 9. The reporter tier

Free-form logic reports through a **narrow view of the context**, so the type system — not
generator diagnostics, not documentation — is what leads users to success.

```csharp
namespace ValidationModules;

public interface IValidationContextReporter {
    ValidationFlow Report(string field, string code, string message,
        ValidationSeverity severity = ValidationSeverity.Error);
    ValidationFlow ReportHere(string code, string message,
        ValidationSeverity severity = ValidationSeverity.Error);
}

public readonly struct ValidationContext : IValidationContextReporter { … }
// existing methods ARE the implementation; no new members on the struct
```

- **All `Report*` extensions move to a generic receiver:**
  `ReportRequired<TReporter>(this TReporter reporter, string field, …) where TReporter :
  IValidationContextReporter`. One home for the extensions, forever in sync across generated
  validators, hand-written validators, `Apply` methods, and this tier — a new extension lights
  up everywhere the day it's written. The constrained generic avoids boxing the struct and keeps
  emitted call sites textually identical
  (`ValidationContextExtensions.ReportRequired(ctx, "name")` infers `TReporter =
  ValidationContext`), so golden snapshots change only where intended.
- **`ValidationRules<T>.Context` is typed `IValidationContextReporter`** and inert like the rest
  of the builder; the generator rewrites `rules.Context` to the live context identifier, and
  transcription does the rest. IntelliSense shows exactly `Report`, `ReportHere`, and the
  seventeen extensions — nothing that doesn't work.
- **Auto-flow-wrap, type-driven:** any *expression-statement* whose type is `ValidationFlow`
  is wrapped `if ((…).ShouldStop) return …;` by the emitter. No method list to maintain — it
  covers every extension, future ones, and user helpers returning `ValidationFlow`. Assigning
  the flow (or `_ =` discard) opts into manual control.
- **Reporter calls are transcription, not islands** — legal anywhere in the body, including
  loops. Manual per-element reporting uses a computed field string
  (`rules.Context.Report($"lines[{i}].sku", …)`); the field argument is a transcribed
  expression.
- **Deliberately absent:** `Push*`, `HasErrors`, `ErrorCount`, `Services`, `StopMode`.
  Escalation for structural work is `Nested`/`Each`, then `Apply`. If real demand shows for a
  read (`HasErrors`), adding it to the interface later is additive and safe.
- **Values may reach messages here** (`$"checksum {computed} failed"`). The composed vocabulary
  and `Ensure` remain redaction-safe *by construction* — no runtime value can reach their text.
  `Report` deliberately reopens that, at an explicit call site, on the author's head. Documented
  as the point of the tier (§14).

### What the generator writes

```csharp
// author:
rules.Context.ReportRequired(nameof(x.Name));

// emitted (inside OrderRules' region method, ctx in scope):
if (global::ValidationModules.ValidationContextExtensions
        .ReportRequired(ctx, "name").ShouldStop) {
    return global::ValidationModules.ValidationFlow.Stop;
}
```

## 10. The four-tier ladder

| Tier | Spelling | Generator knows | Use when |
|---|---|---|---|
| Vocabulary | `rules.Require(x.Street).Length(1, 100)` | everything — code, message, `RuleText` | the rule has a name |
| `Ensure` | `rules.Ensure(x.Start < x.End)` | field, self-rendered message | one assertion, no name |
| Reporter | `rules.Context.Report(nameof(x.Name), …)` | field and severity; condition and message are yours | free-form logic found something |
| `Apply` | `rules.Apply(Checks.Sku)` | nothing — a direct call, ordered last | you need the raw context |

Each step down trades static knowledge for freedom; nothing is more than one step from its
neighbor. `Apply` keeps its 1.0.0 shape (`RuleAction<T>`, method group, emitted as a direct
call) — its territory shrinks but "shared opaque check with full context" is still its job.

## 11. Deleted, with reasons

| Deleted | Because |
|---|---|
| `DescribedValidator<T>`, `AddDescribedValidator`, `ICompiledRule` machinery | Generator-only model; use cases speculative; enables full trimming (§12) |
| The dual-engine design (§19.1) and the conformance suite's second adapter | One engine; "two engines must agree" problem class gone, including §19.9's `[JsonPropertyName]` divergence |
| `When` / `Unless` / `ConditionalRules<T>`, condition hoisting | `if`/`else` is better in every way |
| Selector `Func`s and every `[CallerArgumentExpression]` parameter | Generator resolves symbols directly |
| VM0072 (predicate scope policing) | Expressions transcribe inline where locals are in scope; there is nothing to police |
| VM0074 (double-registration warning) | The runtime path it warned about no longer exists |
| Predicate lifting to static methods | Same — expressions stay inline |
| `PropertyRules<T,TValue>.And` | Chains are single expressions |

## 12. AOT and trimming

Nothing references a rules class at runtime: the validator contains the *expanded* checks;
fragments are inlined, never invoked; registration registers the generated validator; there is
no scanning by design. Under Native AOT the class is never rooted — no native code is generated
for it. Under a trimmed publish, ILLink removes the type; even untrimmed, `Describe` is dead IL
that never JITs. The vocabulary types (`ValidationRules<T>`, `PropertyRules<T,TValue>`,
`IValidationRulesFor<T>`) trim from applications the same way.

Two refinements: static computation helpers survive because they genuinely run — correct, by
design. And the attribute form trims symmetrically (generated validators never read attributes
at runtime), so the README can say it plainly: **the entire declaration layer — attributes and
rules classes — is build-time-only; what ships is generated validators plus the small reporting
runtime.**

## 13. Diagnostics

IDs above VM0075 are proposals — the implementer assigns final numbers and updates
`AnalyzerReleases.Shipped/Unshipped.md` per plan §13.

| ID | Severity | Meaning | Status |
|---|---|---|---|
| VM0070 | Error | statement in `Describe`/fragment is not transcribable (blacklist §5) | **repurposed** — was "not on the whitelist" |
| VM0071 | Error | island value argument is not a member path on `x` and no `field:` given | kept |
| VM0072 | — | — | **retired** (§11) |
| VM0073 | Info | free-form check matches a vocabulary constraint | still reserved, unimplemented |
| VM0074 | — | — | **retired** (§11) |
| VM0075 | Error | `Ensure` has no inferable anchor and no `field:` | kept |
| VM0076 | Error | fragment target is compiled IL from a referenced assembly — fragments must be in this compilation (shared project / source package, §7) | **new** |
| VM0077 | Error | fragment call cycle | **new** |
| VM0078 | Error | `rules` (or a chain value) in a flow the generator cannot follow — stored, captured, delegate-converted, returned, or passed to an instance/virtual/non-void target | **new** — invariant 1 |
| VM0079 | Error | transcribed code references a member inaccessible from the emission site (`private`, `file`-local) — "make it internal" | **new** — invariant 2 |
| VM0080 | Error | island call inside a loop, lambda, or local function | **new** |

## 14. Documents to redraw

A line that moves without being redrawn is one nobody can rely on afterwards. The moves:

- **`API-SURFACE.md` §19** — rewritten wholesale; this document is the input.
- **`IMPLEMENTATION-PLAN.md` §2** — "rule graphs are built once, never per validation call"
  becomes "nothing expensive is constructed per validation call" (its actual intent, per
  Hardened §10.2's per-request `Regex`), with a note that rules-class computation runs per call
  by design.
- **§16 conformance** — one engine; drop the `DescribedValidator<T>` adapter.
- **§19.5 redaction** — split: vocabulary and `Ensure` stay redaction-safe by construction;
  the reporter tier is explicitly not, as its purpose.
- **§19.12 purity** — I/O in `Describe` now compiles; the line becomes convention + docs.
- **Debugging page (new)** — "this method is read, not run": breakpoints in `Describe` never
  hit; step through `obj/…/generated`.
- **README** — positioning: full-language authoring ergonomics with a zero-cost, fully-trimmed
  declaration layer. Verify the novelty claim with an ecosystem sweep before stating it flatly.

## 15. Decision log

Settled during design — do not relitigate without new information:

| Alternative | Verdict |
|---|---|
| Keep the dual engine, flip `DescribedValidator<T>` to per-call execution | Rejected: use cases speculative, per-call name/message work, keeps two-engine agreement burden, blocks trimming |
| Whitelisted DSL (original §19: every statement must be a rule declaration) | Replaced by transcription + two invariants; computation is the feature |
| Mirror `Report*` methods flatly on `ValidationRules<T>` | Rejected: permanent sync burden with `ValidationContextExtensions` |
| `rules.Context` typed as the full `ValidationContext` (raw alias) | Rejected: raw field strings bypass the namer, manual flow protocol, invites `Push`/`Services` rope — superseded by the reporter interface |
| `rules.Field(x.Name)` island for wire names | Superseded by `nameof(x.…)` rewrite (§8) |
| Instance `Describe` | `static abstract`: no phantom instance, `this` impossible |
| Cross-assembly fragment inlining from IL | Impossible: metadata has no syntax, and IDE-held compilations would fork IDE vs. CLI output. v1: fragments travel as source (shared project / source package). Delegation to shipped interface validators reserved as the additive follow-up (§7) |
| Exact fragment shape `(ValidationRules<T>, T)` required | Relaxed 2026-08-29: any static `void` same-compilation method receiving `rules` is followed; extra parameters bind as locals at the call site. The blacklist is unfollowable flows, not signatures |
| Expression-tree selectors | Banned from the start (plan §2: no `Expression.Compile`) |

## 16. Implementation checklist

**Runtime** (`ValidationModules.Runtime`):
- [ ] `IValidationRulesFor<T>` → `static abstract void Describe(ValidationRules<T>, T)`
- [ ] `ValidationRules<T>` / `PropertyRules<T,TValue>`: value-based signatures, internal
      constructors, throwing bodies; `Require` rename; delete `When`/`Unless`/
      `ConditionalRules<T>`/`And`; add `Context : IValidationContextReporter`
- [ ] `IValidationContextReporter`; `ValidationContext` implements it (no new struct members)
- [ ] `ValidationContextExtensions` → generic-constrained receiver
- [ ] Delete `DescribedValidator<T>`, `AddDescribedValidator`, `ICompiledRule` machinery
- [ ] `RuntimeContract.Version` 6 → 7; re-accept `PublicApiTests.RuntimeApi.verified.txt`

**Generator** (`ValidationModules.SourceGenerator.Impl`):
- [ ] `RulesFrontEnd` → transcriber: islands recognized/expanded, statements copied, region per
      rules class in the companion file (author's usings; CSharpAuthor structure per §3)
- [ ] Fragment inliner: recognition, recursion, cycle detection, generic instantiation with
      concrete-type member resolution
- [ ] `nameof(x.…)` rewrite (`[JsonPropertyName]` > namer; through-type untouched)
- [ ] Type-driven auto-flow-wrap for `ValidationFlow` expression-statements
- [ ] Accessibility walk (invariant 2); island-in-loop/lambda ban; `rules`-position check
      (invariant 1)
- [ ] Delete predicate lifting, condition hoisting, VM0072 machinery; diagnostics per §13
- [ ] Regions ordered: attribute region, then rules classes by name (ordinal)

**Tests:**
- [ ] Re-accept golden snapshots; read the diff line by line (§7.6 precedent)
- [ ] New goldens: fragment, generic fragment, conditional fragment call, computation +
      `Ensure` with a local in the message, reporter + `nameof` + interpolated message under
      `if`, auto-wrap of a user helper returning `ValidationFlow`
- [ ] A project that *compiles* emitted output (plan §13); delete `DescribedValidator` tests;
      re-point suppression/fail-fast coverage at generated validators

**Docs:** §14 above, in the same change.

**Unchanged non-negotiables:** CSharpAuthor-authored emission (companion file is the recorded
exception, extended not multiplied); no reflection, no `Expression.Compile`, no scanning;
`[GeneratedRegex]`; K&R braces; xunit v3.
