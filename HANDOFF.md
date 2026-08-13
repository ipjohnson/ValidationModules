# Handoff — benchmark suite, and the error-model design that came out of it

**Written:** 2026-08-13
**Reason:** session ended mid-design. Everything below is either committed and green, or a decision
taken in conversation and not yet implemented.

**Read §3 before §2.** The allocation investigation in §2 was largely superseded by the decision in
§3.1, and picking it back up without knowing that will waste a day.

---

## 1. State of the tree

Solution builds clean under `ContinuousIntegrationBuild=true`, 278 tests pass.

Last commits:

```
da2187a Stop the shape benchmarks measuring nothing, and bound their iteration time
7cd42f8 Prototype the chain-shaped context and benchmark it against the log
882f84e Give the benchmark script the selector BenchmarkDotNet wants
208a272 Add a benchmark suite, and an opt-in comparative one
```

Uncommitted, and building: `Design/LazyPinPrototype.cs` plus its wiring into `ContextShapeParity`
and a `LazyPin_Clean` / `LazyPin_Failing` arm in `ContextShapeBenchmarks`. The arm is **not** added
to `ContextShapeCollectionBenchmarks`, so the lazy-pin shape has no collection numbers. It has
never been run. Given §3.1 it may not be worth finishing — decide before investing in it.

---

## 2. What was built and measured

### 2.1 The benchmark suite

Two projects, split so that "did this change make the library slower" and "is it faster than
FluentValidation" stay separate questions. `benchmarks/README.md` is the reference; the short form:

```bash
./scripts/benchmark.sh                     # ValidationModules alone, JIT + Native AOT
./scripts/benchmark.sh --quick             # JIT, few iterations
./scripts/benchmark.sh --comparative       # vs FluentValidation and DataAnnotations
```

The default suite has three categories — `component`, `endtoend`, `design` — and references the
source generator, so end-to-end numbers measure emitted code. The comparative suite is opt-in and
refuses to start unless `EngineParity` finds the three rule sets still agree on failure counts.

Two traps worth not rediscovering, both documented in the code:

- **`--job short` inherits Native AOT** in the default suite, because BenchmarkDotNet resolves the
  toolchain for a runtime-less job from the project's `PublishAot`. Hence our own `--runtime` and
  `--quick` switches in `BenchmarkArguments.cs`.
- **Sub-10ns cells make BenchmarkDotNet climb to 134M ops per iteration** at the default 500ms
  target; one cell took fifteen minutes. `[IterationTime(100)]` on the shape classes fixes it.

### 2.2 Competitive position (JIT, .NET 10, arm64)

| | ValidationModules | FluentValidation | DataAnnotations |
|---|---|---|---|
| Flat, clean | **39.6 ns / 472 B** | 194 ns / 664 B | 1,056 ns / 2,696 B |
| Flat, clean, pooled collector | **34 ns / 0 B** | — | — |
| Nested, clean | **119 ns / 472 B** | 1,675 ns / 5,224 B | 545 ns (top level only) |
| 1000 elements | **15.0 µs / 49 KB** | 242 µs / 846 KB | does not descend |
| Resolve per request | **4.1 ns** | 2,269 ns | — |

The resolution figure is the one worth quoting: `AddValidatorsFromAssemblyContaining` registers
validators **scoped**, and constructing a FluentValidation validator costs ~2,163 ns, so it rebuilds
its rule graph every request by default. That is §10.2 of the plan as the framework's own default
rather than as somebody's mistake.

The comparison is tilted toward FluentValidation deliberately — cascade mode set to `Stop`, the same
`[GeneratedRegex]` handed to both, validators constructed once. All four choices are listed in
`benchmarks/README.md`.

**Native AOT was never run for the comparative suite.** `./scripts/benchmark.sh --comparative --aot`
would settle §1's claim that `Expression.Compile` falls back to the LINQ interpreter. It is the
single most valuable unrun measurement in the repo.

### 2.3 Allocation findings that still stand

Measured with a probe, not inferred:

| | Bytes |
|---|---|
| `new ValidationErrorCollector()` | **472** — of which ~408 is the eager `PathNode[16]` |
| Pooled + `Reset()`, 0 → 1000 pushes | **0** |
| One error added | 56 |
| `ToResult()` on five errors | +264 (three allocations) |
| `ValidationRunner.Validate` vs direct | **+32** — a boxed array enumerator per call |

Three fixes independent of any design question:

1. **Allocate `_nodes` lazily.** A flat model never pushes; 408 of the 472 bytes is waste on the
   commonest call there is.
2. **`ValidationRunner<T>` should hold arrays, not `IEnumerable<T>`.** `foreach` over an
   array-as-`IEnumerable` boxes an enumerator — exactly 32 B, measured. The async path pays twice.
3. **`ToResult` allocates a `List`, its backing array and a `ReadOnlyCollection`.** One array would
   do.

Also: **`IsValid` and `ValidationRunner.Validate` both document allocation-free clean passes and
neither is.** Fix the code or fix the text.

### 2.4 Two corrections to be aware of

The shape investigation produced two wrong readings before it produced right ones. Both flattered
the prototype, and both are fixed in the committed benchmarks:

- **The JIT deleted the chain's loop body.** Element contexts were pushed and dropped; the log's
  push mutates the collector and cannot be elided, the chain's leaf push has no side effect. The
  chain "measured" 0.28 ns/element. `Consume(ref …)` from a non-inlined method — the shape the
  emitter actually writes — made the corrected figure 4× worse.
- **"93% less allocation" was mostly the eager buffer, not the shape.** The log arm allocated a
  472 B collector against the chain's 32 B, and 408 B of that gap is the field initializer in §2.3,
  not anything to do with linking. The clean comparison is the **pooled** rows, where neither arm
  allocates a collector: chain is ~2× faster on time for leaf collections, and **slower** at depth 4
  (19.8 ns / 144 B against 11.6 ns / 0 B).

Do not quote the chain's flat/depth-1 allocation ratios. They are not measuring what they claim.

---

## 3. Decisions taken this session, not yet implemented

### 3.1 Field paths are compact by default

**This is the decision that supersedes §2's investigation.**

The premise: the first consumer is Hardened, for query, path and body validation. Query, path and
header parameters are flat scalars — depth 0. Body is depth 1. §10.4 records that Hardened's current
emitter does not walk past the top level, so nothing downstream depends on deep paths today.

So full ancestry is not worth what it costs. Report **the outermost segment, the immediate parent and
the field**, eliding whatever sits between the first two: `body...address.postalCode`.

Rationale, in the order it was argued:

- Long messages need truncating anyway; this controls where the truncation happens.
- Most real data is depth 0–2, which yields a full path regardless.
- `body...property.property` is a good amount of information, particularly with the key.

**The consequence for §2: none of the four context designs is needed.** The log, the chain, lazy-pin
and roll-up all exist solely to reconstruct ancestry. Drop the requirement and the context is:

```csharp
public readonly struct ValidationContext {
    private readonly ValidationErrorCollector _collector;   // 8
    private readonly string?                  _outermost;   // 8  — first pushed segment, null at depth 0
    private readonly string?                  _parent;      // 8  — immediate parent segment
    private readonly string?                  _parentKey;   // 8  — dictionary key, else null
    private readonly int                      _parentIndex; // 4  — element index, else -1
    private readonly int                      _depth;       // 4
}
```

`Push` copies the struct, moves the incoming segment into the parent slot, fills `_outermost` if it
is still null, and increments depth. No log, no nodes, no pinning, no unwind bookkeeping, no
allocation and no writes at any depth or element count. Simpler and cheaper than anything
benchmarked.

Key and index stay as separate components rather than a pre-rendered `"[3]"` suffix, because
rendering the index at push time would allocate on a path that currently does not.

**There is no root name and nothing is synthesized.** Path, query and header parameters are depth-0
scalars and render bare — `id`, `page`. `body` is not a special root, it is an ordinary property that
gets pushed like any other, so the anchor appears only because something pushed it. `_outermost` is
the first pushed segment retained, never a configured prefix.

**Elision fires only when a segment was actually dropped**, which is what makes the marker mean
something:

| true path | pushes | renders |
|---|---|---|
| `id` | 0 | `id` |
| `body.email` | 1 | `body.email` |
| `body.lines[3].sku` | 2 | `body.lines[3].sku` |
| `body.order.address.postalCode` | 3 | `body...address.postalCode` |

**Collections — resolved.** Retaining the parent's own index subsumes the case that raised the
question: `body.lines[3].sku` is depth 2 and renders complete, so a 500-row bulk import says which
row without a second index field. Two losses are accepted rather than fixed. An index on a
*non-parent* ancestor is dropped (`body.order.lines[3].address.postalCode`), which needs depth ≥3
*and* an object between the element and the failing field. An index on the outermost segment itself
is dropped — validating a bare `List<Order>` at the top reports `...lines[3].sku` — which is not a
request-body shape.

**Not a wire-shape change** — `ValidationError.Field` keeps its place in the JSON. The *value* of
that field changes, which matters only to clients keying off it to attach messages to form inputs.

**Adding full paths back later is additive, and here is the shape it would take.** There is no
MSBuild opt-in for them now — one cannot coexist with the struct above, because there is no ancestry
left to reconstruct a path from. Checked against the emitter rather than assumed:

- **The `ctx.Add*` extensions are unaffected.** They take the context by value and call
  `Add(field, code, message, severity)`. Neither signature nor body references path storage; the
  message embeds the bare field name and the path lives in `ValidationError.Field`.
- **The emitted text is identical under both designs** — `ValidatorEmitter` writes `ctx.Push`,
  `ctx.PushIndex` and `ctx.PushKey` at :145, :167 and :135, and none of them depend on the
  representation.
- So the choice is *where* the switch lives. Reusing the existing push names means no emitter change
  but taxes everyone — the context must always carry a node index and the collector must always keep
  the log, because at push time it cannot know what the process wants. **Distinct names
  (`PushTracked`, …) let the generator pick at compile time**, the same mechanism as §3.5, and the
  compact path stays free.
- Cost of the tracked variant: a `_node` int in the struct (`-1` when compact, 44 bytes → 48
  padded), a node write in `PushTracked` only, and one branch in `Emit` — which is the failure path,
  never the clean one. Allocate the node array lazily and a compact-only process never touches it.
  §7.5's additive-only commitment covers adding the three names.

**The hazard, if that day comes:** the property is per-compilation, but a context flows across
assembly boundaries at runtime, and ValidationModules / Hardened / consumer app is exactly that
arrangement. A compact-compiled validator nesting into a tracked-compiled one yields a path that is
full only from the point tracking began. It degrades rather than breaks, and it will not be obvious.

### 3.2 Redaction: shape by default

Three policies, selectable at assembly (csproj), class, and property level — same resolution order
the pattern policy already uses.

| Policy | Records |
|---|---|
| **strict** | code, field, constraint description. Nothing that is a function of the value. |
| **shape** *(default)* | the above plus irreversible facts — "got 12 characters, expected at most 10", "got digits, expected letters", "got 14 items". |
| **full** | the value itself. |

Shape is the default because a public API wants to be called, and not telling callers what shape you
expect is poor developer experience. It is safe by construction rather than by enumeration — a
blocklist ("don't record passwords, emails, writeOnly") always misses SSN, PAN, phone, DOB, tokens,
whereas shape cannot leak any of them regardless of whether anyone remembered to mark the field. The
error also goes back to the caller who *sent* the value, so it discloses nothing to them; the
exposure is the log, and "got 16 digits" in a log is not a card number.

**The rule that keeps shape safe: cardinality and category only, never position or content.** Length,
item count, character-class membership over the whole value — yes. First character, prefix,
"contains @", substrings, hashes — no.

**The one real leak is secrets**, where length narrows a brute force. Marked properties drop to
strict, and OpenAPI supplies the marks automatically.

### 3.3 Two disclosure axes, currently conflated

`AddRange` echoes its bounds, `AddAllowedValues` echoes the whole permitted set, `AddPattern`
deliberately echoes nothing. That is not inconsistent policy about values — bounds and enum members
are **schema** facts, published in the OpenAPI document anyway. Only value-derived facts need policy.
Drawing that line resolves the inconsistency, and suggests the pattern omission is now over-cautious
since patterns are in the published spec too.

### 3.4 OpenAPI has no sensitivity keyword, but two usable signals

No `sensitive`, `pii` or `redact` in 3.0 or 3.1. Usable instead:

- **`writeOnly: true`** — may be sent in a request, must not appear in a response. Semantically exact
  for this purpose.
- **`format: password`** — narrower, unambiguous, nobody marks a field this way casually.

Both are on the schema, so `Hardened.OpenApi.SourceGenerator` gets them free — it already reads the
spec and emits models and validators in one pass, so it can emit `[Sensitive]` with no user action.

Two gaps the spec cannot close:

- **Dictionary keys**, because the key is data, not schema. The *map property* is schema
  (`additionalProperties`), so mark the property and wipe the keys under it: `items[***].name`.
  User-controlled keys are also an unbounded **log cardinality** problem independent of PII, which
  argues for wiping by default with opt-in to show.
- **Path and query parameters carrying PII.** `/users/{email}` is nothing special to OpenAPI. Manual
  attribute still needed.

*(Confidence: high on both signals and on 3.0/3.1 having no dedicated keyword. 3.2 not verified.)*

### 3.5 Policy is selected at compile time, by which arguments get emitted

The mechanism, and the strongest part of the design: the extension methods take parameters for
everything, and **the generator decides at compile time which to pass.**

```csharp
ctx.AddStringLength("name", 1, 10);                              // strict
ctx.AddStringLengthActual("name", 1, 10, value.Name.Length);     // shape
```

In strict mode the value never enters the call — it is absent from the emitted IL, not filtered at
runtime. This **converts a deployment-time risk into a build-time, greppable one**: "is this
configured correctly in prod right now" is unanswerable from the repo and wrong the first time
someone ships a new environment; "does this binary ever pass a password into a message" is answered
by reading generated source.

**Use distinct method names for the value-carrying variants**, not optional parameters, so the audit
is a symbol grep rather than argument-position analysis.

**Back it with an analyzer.** A rule that errors on a value-derived argument for a `[Sensitive]`
property makes it a build failure. This covers the gap generation cannot: `IAsyncValidatorFor<T>` is
hand-written and nothing stops an author writing `context.Add(field, code, $"got {value.Password}")`.

> Note this does **not** contradict §7.2 of the plan. *Generators* cannot see each other's output;
> *analyzers* run over the post-generation compilation and see all of it. Opt in with
> `ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze)`, which is off by default.

`EmitCompilerGeneratedFiles` stays worth enabling, for humans reading what happened rather than as
the enforcement mechanism.

### 3.6 A runtime binding time, deferred

Scope (assembly / class / property) and binding time (compile-time / runtime) are **independent
axes**. Per-class override is scope and is fully compiled. A runtime policy is a binding time and can
have its own scope.

Deferred deliberately. It is safe to defer because it arrives as *new* method names alongside the
existing ones, and §7.5 already commits the emitted surface to being additive-only.

The one property worth writing down: runtime mode requires the value to be captured for anything to
decide about later, so it is the one selection that gives up the compile-time guarantee wherever it
is applied.

### 3.7 Hardened has to share the policy

If Hardened logs the raw body on failure, the validation policy is irrelevant — the payload is
already in the log. The generator should emit a **redaction map per type** consumed by both the
validator (for messages) and the logger (for payloads). For AOT that likely means a second
`JsonSerializerContext` variant for the logging path rather than a post-serialization scrub.

---

## 4. Consequences for work already in the repo

- **`MessageMaterializationBenchmarks` priced a two-argument deferred payload.** Shape needs three
  numerics — min, max and *actual* — for string length, range and item count alike. `DeferredError`
  goes from ~24 to ~32 bytes of payload and `ValidationError` from 32 to ~48. Still ahead of the
  ~56 bytes a composed message costs, but thinner than that run suggests. The literal option was
  already dead on code-size grounds (107 of 313 native bytes per constraint) and is not affected.
- **§4 of the plan says field names come from a single `IValidationFieldNamer` so every engine
  agrees.** Compact paths make the FluentValidation adapter's job harder in a specific way: it
  receives full property chains and must actively truncate to match, where today it only
  case-converts. Settle before §8's conformance suite is written.
- **Compact paths are a wire *value* change for Hardened's 400 bodies.** Decide before Stage 5, not
  after.

---

## 5. Suggested order

1. Allocation fixes #2 and #3 from §2.3 — the boxed enumerator in `ValidationRunner<T>` and
   `ToResult`'s three allocations. Independent of every design question and both small.

   **Fix #1 is moot.** §3.1 deletes `_nodes` outright, so lazily allocating it is work with no
   surviving target. And fix #2 alone does not make `ValidationRunner.Validate`'s
   "allocation-free when the value is clean" true — it news a collector on every call, so that needs
   either an overload taking a pooled collector or the doc comment corrected.

2. ~~Settle §3.1's collection question.~~ **Resolved 2026-08-13 — see §3.1.** Outermost segment plus
   parent plus field, parent carries its own index, no second index field.
3. Implement the context from §3.1 and delete the four-way shape comparison, or keep it in `Design/`
   as a record of why the simple shape was chosen. Deleting the node log takes `AddNode`, the
   cycle-depth walk and the parent-chain `BuildPath` with it, and makes the async-safety argument in
   `ValidationContext`'s remarks unnecessary rather than merely satisfied — the struct has no shared
   mutable backing to race on. Update `ValidationContextExtensions`' "it is two words" justification
   for taking the context by value; the reasoning still holds (a `ref`/`in` receiver would refuse
   `context.Push("home").AddRequired(...)`), but not on size grounds.
4. §3.2 and §3.5 together — the policy ladder and the compile-time argument selection are one change
   to the emitter.
5. The analyzer in §3.5.
6. Run `--comparative --aot` once, for §2.2.
