# Conditional rules and polymorphism — merged execution plan

Merges two independently-written specs that overlap in four files and collide on two diagnostic ids:

- **“The Declared-Type Problem”** — inherited constraints, polymorphic descent, services on the collector.
- **“Shipping the When”** — conditional rules across the attribute surface and both DSL shapes.

Neither spec knew about the other. This document is the reconciliation and the order of work. Where the
two disagree, **this document wins**; where it is silent, the source specs stand.

## 1. The collision, and how it resolves

Both specs allocated the next free ids off `main` and both landed on VM0028/VM0029.

`Shipping the When` placed its six ids **by meaning** — VM0028/29/33/34 continue the constraint-declaration
block that `[EnumDefined]`’s VM0027 just extended, and VM0076/77 continue the rules-DSL block that ends at
VM0075. `The Declared-Type Problem` took VM0028–VM0030 only because they were next.

**Resolution: the conditional ids stand; polymorphism moves down to a contiguous VM0030–VM0032.**

| Id | Owner | Severity | Fires when |
|----|-------|----------|------------|
| VM0028 | conditions | Error | `When`/`Unless` names a member the validated type does not have |
| VM0029 | conditions | Error | the named member is not a `bool` property, parameterless `bool` method, or `static bool` method taking the model |
| VM0030 | polymorphism | Warning | a derived property hides a base property whose constraints are now dropped *(was VM0028)* |
| VM0031 | polymorphism | Warning | a `[ValidateNested]` target is not sealed and no `Polymorphism` mode is set *(was VM0029)* |
| VM0032 | polymorphism | Error | `Polymorphism.Runtime` on a sealed or value type *(was VM0030)* |
| VM0033 | conditions | Error | a constraint sets both `When` and `Unless` |
| VM0034 | conditions | Warning | a condition folds to a constant |
| VM0076 | conditions | Warning | a conditional block declares no rules |
| VM0077 | conditions | Warning | a chained `.When()` applies to no rules |

VM0005, VM0011–VM0015, VM0019 and VM0020 stay retired/reserved, per the note in `ValidationDiagnostics.cs`.

## 2. Where the two features actually touch

Four files, and only one of them is a genuine conflict.

| File | Polymorphism | Conditions | Kind |
|------|--------------|------------|------|
| `Emitters/ValidatorEmitter.cs` → `EmitNested` | Part B emits a type switch **inside** the descent | Stage A guards the descent with `c0 &&` | **real — same statement** |
| `FrontEnds/AttributeFrontEnd.cs` member loop | Part A swaps `GetMembers()` for `MemberWalk` | Stage A reads `When`/`Unless` per constraint | same loop, different axes |
| `Models/ValidatedPropertyModel.cs` | adds `Polymorphism` | adds `string? Condition` | additive, mergeable |
| `PublicApiTests.RuntimeApi.verified.txt` | Part C + `Polymorphism` | `When`/`Unless`, `ConditionalRules<T>` | both move it |

**The seam.** The conflict dissolves once the descent is emitted as two separable concerns: a *guard* that
decides whether to descend at all, and a *body* that decides which validator runs. Conditions own the
guard, polymorphism owns the body. `EmitNested` therefore takes an optional condition and an optional
subtype list, and the two compose without either knowing about the other:

```csharp
if (c0 && value.Payment is { } p) {          // <- condition owns this
    var ctxP = ctx.Push("payment");
    switch (p) {                              // <- polymorphism owns this
        case global::Ns.Premium __v: (_premium ??= new()).Validate(ref ctxP, __v); break;
        default:                     PaymentValidators[0].Validate(ref ctxP, p);   break;
    }
}
```

## 3. Two interactions neither spec addresses

**A condition on an inherited constraint resolves against the validated type, not the declaring type.**
Once Part A hoists a base’s `[Required(When = nameof(IsExpedited))]` onto a derived type, `nameof` has
already been resolved to a string by the compiler and the generator only has the name. Resolving it
against the type being validated is both correct and strictly more permissive — a derived type sees every
member its base does, and may deliberately shadow the predicate. VM0028 fires against the derived type.

**`[ValidateNested]` can carry a mode and a condition at once.** `[ValidateNested(Polymorphism.CompileTime,
When = nameof(IsAuto))]` is well-formed and falls out of the seam above with no extra work. It needs a test,
not a design.

## 4. Order of work

Sequenced so that each phase lands green, nothing is written twice, and the single runtime-contract bump
happens once at the end.

| Phase | Work | Source | Why here |
|-------|------|--------|----------|
| **1** | Stage 0 — expression-bodied `Describe` crash; `else if` chain drops errors | When §0.1, §0.2 | Both bugs are live on `main`, both sit in code every later phase edits, and §0.2 rewrites the emitted shape that every later golden snapshot is written against. Front-load the churn. |
| **2** | Part A — inherited constraint collection (`MemberWalk`), VM0030 | Poly Part A | Gated on the Roslyn metadata spike. Fixes Defect 1 alone, needs no runtime surface, and Part B depends on it — a self-contained subtype validator is what lets dispatch avoid double-reporting. Snapshots are born against Phase 1’s corrected shape. |
| **3** | Stage A — attribute `When`/`Unless`, VM0028/29/33 | When Stage A | Highest value per unit of work. Establishes the condition guard seam in `EmitNested`. Part A has landed, so the inherited-condition interaction (§3) is testable here. |
| **4** | Part B — `DeclaredOnly` + `CompileTime`, VM0031 | Poly Part B | Drops the type switch into the descent body against the seam Phase 3 built. No contract bump. Ships two of three modes. |
| **5** | Stages B + C — chained and block DSL, runtime condition array, VM0076/77 | When §3, Stages B, C | Almost disjoint from polymorphism — `RulesFrontEnd`, `PropertyRules`, `ValidationRules`, `CompiledRules`, new `ConditionalRules`. |
| **6** | Part C + `Polymorphism.Runtime` — `ctx.Services`, `IDynamicValidator`, VM0032, contract 3 → 4 | Poly Part C | The only contract bump in the programme, taken last so one bump covers everything. |
| **7** | VM0034 constant folding, website reference, migration note, size re-measure | When Stage D | VM0034 needs a constant-folding pass and is the one genuinely new analysis. |
| **4b** | cross-assembly subtype discovery via a `[GeneratedValidatorFor]` manifest | Poly Part B | **Recommended dropped** once `Runtime` shipped — see below. |

Diagnostics land with the phase that introduces them, not batched into Phase 7.

## 5. Verification, per phase

Every phase ends on `dotnet build -c Release && dotnet test -c Release`, solution warning-free, and moves
`PublicApiTests.RuntimeApi.verified.txt` only by its own additions. Beyond that:

- **Phase 1** — a conformance test asserting the generated and described engines return identical error
  sequences for a field carrying two failing constraints. This is the baseline every later parity test
  rests on, and it fails on `main` today.
- **Phase 2** — the metadata-attribute spike runs *first*; if Roslyn does not surface base-type constraint
  attributes across an assembly reference, Part A shrinks to same-compilation bases and this plan is revised.
- **Phase 3–4** — golden tests for switch ordering (sort by inheritance depth descending, then ordinal:
  emitting `case Card` before `case Premium : Card` is **CS8120** inside generated code), shadowing, each
  `Polymorphism` mode, and a guarded polymorphic descent.
- **Phase 5** — once-per-pass observed: a condition reading a mutable static counter, asserted to increment
  exactly once per `Validate` call under both engines. This test fails against the naive per-rule design.
- **Phase 6** — engine parity on a polymorphic descent with a provider-carrying collector.
- **Phase 7** — re-measure emitted size against the +65 KiB Native AOT baseline.
  **Measured, and it did not move**: `scripts/verify-aot.sh` publishes byte-for-byte the same
  binary at `origin/main` and at the end of phase 7 — 2,248,008 bytes on osx-arm64 either way. That
  is the design decisions paying off rather than a happy accident: an unconditional constraint emits
  exactly what it emitted before, `DeclaredOnly` emits no switch, and `IDynamicValidator` adapters
  are emitted only for an assembly that actually dispatches dynamically. A consumer using none of
  this pays nothing for it.

## 6. What actually shipped, and what did not

Phases 1-7 are on `main`'s history as seven commits. Two things are worth carrying forward:

**Dropped: phase 4b.** The manifest was specified so that `CompileTime` could dispatch over
subtypes declared in referenced assemblies. Two things make it a poor investment now:

- **`Runtime` already covers the case properly.** It resolves by runtime type through the container,
  so a subtype in any registered assembly is dispatched to without the switch having to know it
  exists at build time.
- **The manifest could never close the gap anyway.** It would let a switch see subtypes *upstream*,
  in assemblies this one references. A subtype added *downstream* — a consumer writing
  `Crypto : Payment` against our package — can never appear in a switch compiled before it was
  written. That is inherent to a compile-time switch, not an implementation shortfall, so 4b would
  turn a clear boundary ("the compilation") into a fuzzy one ("the compilation, plus references")
  while leaving the sharp edge in place.

What that leaves: `CompileTime` dispatches over subtypes declared in the compilation, full stop —
which is a property of the mode rather than a defect. A hierarchy spanning assemblies uses
`Runtime`. The residual case 4b would serve is a *container-free* consumer with an upstream
hierarchy; it stays additive if that turns out to matter.

**Decided along the way, and not in either source spec:**

- An `override` is one property with two declarations rather than two properties, so its constraint
  attributes accumulate — `ValidationConstraintAttribute` is `Inherited = true`. Only a `new`
  declaration is a hide, and only that raises VM0030.
- Diagnostics from a base or interface declaration are reported where that declaration lives, not
  once per derived type. A consumer cannot edit a base type that arrived as metadata.
- A condition must be written as a lambda. A method group has no body to lift and would come out of
  the emitter as `=> true` — a condition that silently always holds. Reported instead.
- `IDynamicValidator` adapters are emitted for every validated type in an assembly that dispatches
  dynamically, and for none at all in one that does not. A registration roots its adapter past the
  trimmer, so an assembly that never asked for the mode pays nothing; within a dispatching assembly
  the set is complete, so a registry miss is unambiguous.
- Those adapters resolve their validators on first use rather than in the constructor. Building the
  registry resolves every adapter at once, so a self-referential model — whose validator depends on
  the service it is itself registered under — would otherwise turn a latent DI cycle into a
  container that will not build.
- `Polymorphism.Runtime` was withheld from the published enum until phase 6 built it, rather than
  shipping in phase 4 as a member whose only behaviour was a build error.
- A predicate lifted into `{RulesClass}_Rules` loses the rules class's scope, so a bare reference to
  one of its members was CS0103 in generated code — for `public` members as readily as `private`
  ones, which made it read as an accessibility problem when it was a qualification problem. Bare
  references are now qualified, which reads the real member rather than copying it, so the described
  engine running the original lambda still sees the same value. A `private` member cannot be reached
  even qualified: a constant of any type crosses by value (C# bakes those anyway, so the copy is the
  original by the language's own rules) and anything else is VM0078. Writing a constant back needs
  the suffix and round-trip format that preserve its type as well as its value - `G17`/`G9` rather
  than the default, since shortest-round-trip `ToString` only became the default in .NET Core 3.0
  and this generator is netstandard2.0.

## 7. What is not being built

Carried over from the source specs, restated so nothing is rediscovered:

- No automatic polymorphic dispatch. Coverage that depends on physical assembly layout is worse than no
  feature — it shrinks silently when a type moves to another package. Modes are always named.
- No `SubtypeRegistry<TBase>` and no per-assembly `IValidatorFor<TBase>` dispatcher. The first adds
  process-wide mutable state; the second double-reports, because nested descent runs *every* registered
  validator.
- No `Runtime`-mode fallback to the compile-time switch. A missing provider throws.
- No `ApplyConditionTo`. A `.When()` conditions every constraint in the statement it terminates; to guard
  less, write two statements.
- No `WhenAsync`/`UnlessAsync`, no `Func<T, ValidationContext<T>, bool>` overloads, no `DependentRules`.
- No enumeration of types in referenced assemblies. Subtypes arrive by base-chain inversion over the
  compilation, plus a `[GeneratedValidatorFor]` assembly manifest read from references.
