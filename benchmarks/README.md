# Benchmarks

Two suites, split because they answer different questions.

| Suite | Project | Question | Run |
|---|---|---|---|
| Default | `ValidationModules.Benchmarks` | Did this change make the library slower? | every time |
| Comparative | `ValidationModules.Benchmarks.Comparative` | Is it faster than FluentValidation? | opt-in |

The default suite references nothing but `ValidationModules.Runtime` and the source generator, so
its numbers move only when this repository does. The comparative suite pulls in FluentValidation
and uses `System.ComponentModel.DataAnnotations`, and its numbers move when *those* change — which
is a fine thing to measure occasionally and a poor thing to gate a change on.

```bash
./scripts/benchmark.sh                          # default suite, JIT and Native AOT
./scripts/benchmark.sh --quick                  # default suite, JIT, few iterations
./scripts/benchmark.sh --comparative            # comparative suite, JIT
./scripts/benchmark.sh --comparative --aot      # ...and under Native AOT
./scripts/benchmark.sh --all                    # both
./scripts/benchmark.sh -- --list flat           # what exists, without running any of it
./scripts/benchmark.sh -- --anyCategories=endtoend
./scripts/benchmark.sh -- --filter '*Collection*'
```

Anything after `--` goes to BenchmarkDotNet unchanged. Results land in
`BenchmarkDotNet.Artifacts/results` beside the project that produced them.

Both suites take `--runtime jit|aot|both`. It is ours, not BenchmarkDotNet's: `--runtimes` *adds*
to the jobs a config declares rather than replacing them, so there would otherwise be no way to
drop the AOT job once the config had declared it — and the AOT job publishes through ILC, which
turns a thirty-second check into a five-minute one. `--quick` is ours for a related reason, in
[`BenchmarkArguments.cs`](ValidationModules.Benchmarks/BenchmarkArguments.cs).

---

## Default suite

Three categories, filterable with `--anyCategories=`.

### `component` — what one primitive costs

| Class | Measures |
|---|---|
| `ValidationContextBenchmarks` | Push, PushIndex, PushKey, Add, and the render that builds `lines[3]...shipTo.postalCode`, at depths 1/4/16 |
| `ErrorCollectorBenchmarks` | A fresh collector per pass against a pooled one, the synchronized collector's uncontended lock, `ToResult`, `Merge` |
| `SuppressionBenchmarks` | The linear scan enforcing "a field that failed Required accepts nothing further", at 1/8/64 failed fields |
| `ConstraintBenchmarks` | Each constraint alone, through generated code, passing and failing |
| `FieldNamerBenchmarks` | The naming policies — which generated validators never call, and the FluentValidation adapter calls per failure |

`ValidationContextBenchmarks.Push_NoAdd` is §4 of the plan stated as a benchmark: *a validation
pass that finds nothing must allocate nothing*. Its allocation column must read `0 B` at every
depth. If it ever does not, that is a regression regardless of what the timings say.

### `endtoend` — what a whole pass costs

| Class | Measures |
|---|---|
| `ValidationPassBenchmarks` | Flat and nested payloads, clean / one failure / all failing, through `IsValid`, `Validate` and `ValidateInto` |
| `CollectionScalingBenchmarks` | 1 / 10 / 100 / 1000 validated elements |
| `NestingDepthBenchmarks` | Depth 1 / 4 / 16, clean descent against a failure at the leaf |
| `ValidationRunnerBenchmarks` | `ValidationRunner<T>` overhead, the async path, and the gate that skips business rules when structural validation failed |
| `RequestPipelineBenchmarks` | The four shapes a request filter could be written in |
| `RegistrationBenchmarks` | `AddValidationModules`, `BuildServiceProvider`, and resolution |

`RequestPipelineBenchmarks` is the one the library exists for. §10.2 and §10.3 of
`IMPLEMENTATION-PLAN.md` record the incumbent rebuilding its rule graph per request — including a
`RegexOptions.Compiled` regex — and then reaching the validator through `MakeGenericType` and
`Invoke`. The benchmarks there are the alternatives, from the shape a filter written by habit lands
on to the shape a generated filter should emit.

### `design` — which shape should the emitter produce

These predate the generator and several of them measure hand-written stand-ins for code it does
*not* emit. That is the point: they exist to settle a design question, not to track a regression.

| Class | Question |
|---|---|
| `EmissionShapeBenchmarks` | Inline the comparison and message at every site, split them, or push both into a runtime helper |
| `MessageMaterializationBenchmarks` | Literal message in metadata, composed on failure, or deferred until read |
| `RegexStrategyBenchmarks` | `[GeneratedRegex]`, interpreted `static readonly Regex`, `RegexOptions.Compiled`, or a specialized matcher |

---

## Comparative suite

ValidationModules against FluentValidation 12 and in-box DataAnnotations. Categories: `flat`,
`nested`, `collection`, `startup`, `di`.

| Class | Measures |
|---|---|
| `FlatValidationComparison` | Five rules, no nesting. The central reading |
| `NestedValidationComparison` | An order with a nested buyer, a nested address and three elements |
| `CollectionScalingComparison` | 1 / 10 / 100 / 1000 elements, ValidationModules against FluentValidation |
| `ValidatorConstructionComparison` | What each engine costs before it validates anything, and what constructing a validator per request costs |
| `DependencyInjectionComparison` | Registration — a generated table against `AddValidatorsFromAssemblyContaining`, which scans — and per-request resolution |

### Keeping it fair

A comparative benchmark's failure mode is not a wrong number, it is a number that is right about
the wrong thing. The rules are declared three times — constraint attributes, a `RuleFor` chain,
DataAnnotations attributes — and nothing in the compiler relates them.

So `EngineParity.Verify()` runs before any measurement and **refuses to start the suite** unless
the engines agree on how many failures each sample payload has. It counts rather than compares
failures, because the engines produce different field-path casing and message text by design; that
belongs to the conformance suite in §8 of the plan, not here.

Four further choices, all of which cut against this library:

- **FluentValidation runs with `RuleLevelCascadeMode = CascadeMode.Stop`.** Generated code emits
  Required-suppression as an `else if`; FluentValidation's default is to run the whole chain
  anyway. Left at the default it would report a different error set *and* do more work to report
  it, so the comparison would be measuring two specifications rather than two engines.
- **Both engines are given the same `[GeneratedRegex]` object.** FluentValidation's `Matches`
  accepts a `Regex`, so this takes the regex engine out of the comparison entirely. A typical
  FluentValidation codebase writes `Matches("^[A-Z]{3}$")` and gets an interpreted `Regex` built
  from a string, which is slower than what is measured here.
- **Every validator is constructed once, in setup.** That is what all three engines recommend, and
  it hides FluentValidation's construction cost completely. `ValidatorConstructionComparison` is
  where that cost is shown instead.
- **FluentValidation resolves through a scope, because that is what its own registration
  extension asks for.** The scope is part of what a request pays and is included rather than
  factored out.

### The DataAnnotations caveat

`Validator.TryValidateObject` does not descend. It walks the top-level properties of the object it
is handed and stops, even with `validateAllProperties: true` — a nested object's `[Required]` is
never evaluated, and neither is a collection element's.

So its rows in `NestedValidationComparison` are labelled **TOP LEVEL ONLY** and are not
like-for-like: on the failing order it finds one of the four failures the other two engines find.
They are shown because "what does the free in-box option cover" is worth an answer, and because
that gap is the same one §10.4 of the plan found in a shipping framework. It is absent from
`CollectionScalingComparison` entirely, where a flat line across the sweep would say nothing.

---

## Reading the numbers

- **The clean-payload rows are the ones that matter.** Production traffic mostly validates cleanly.
  A failing pass composes messages and materializes paths, and lands immediately before a 400
  response whose serialization costs more than all of it.
- **Allocation is usually the finding, not time.** The design commitments — a path that lives in the
  context struct, a linked list of errors, a shared `ValidationResult.Valid` — are about what a
  clean pass allocates. Watch that column first.
- **The pooled rows are a measurement, not a recommendation.** Pooling a collector is worth 48 bytes
  on a clean pass and costs a node per error on every failing one, so the library builds a fresh one
  per validation. Those rows stay because that is the number the decision rests on.
- **Run on a quiet machine.** No debugger, no build in another terminal. BenchmarkDotNet will warn
  about multimodal distributions when something else is competing for the CPU.
- **`--quick` numbers are not quotable.** Three iterations after one warmup answers "does it still
  run", not "how fast is it".
