# ValidationModules — agent guide

Compile-time validation for .NET. Rules are declared as attributes (or in a rules class) and
flattened into straight-line C# by a source generator at build time. Native AOT is a hard
requirement, not a supported configuration.

This file is the source of truth for every agent working in this repo. `CLAUDE.md` points here.

**Read `IMPLEMENTATION-PLAN.md` before starting work.** The traps in §7.2 were found the expensive
way. It is not a description of what shipped: §6 (profiles) and overlays were deliberately not
built. For what exists, the public surface is pinned by
`tests/ValidationModules.Runtime.Tests/Snapshots/PublicApiTests.RuntimeApi.verified.txt`.

---

## If you already know FluentValidation

You almost certainly do — it dominates the training data for .NET validation. Most of what you know
transfers, but two things do not, and they are where agents reliably go wrong. Read the right-hand
column before writing rules.

| FluentValidation | Here | Note |
|---|---|---|
| `class V : AbstractValidator<T>` + ctor body | `class V : IValidationRulesFor<T>` + `static Describe(ValidationRules<T> rules, T x)` | Interface, not base class — and the body is **read at build time, never run** |
| `RuleFor(x => x.Name)` | `rules.Require(x.Name)` etc. — the entry call carries the value | Values, not selectors; the generator resolves `x.Name` as a symbol |
| `Expression<Func<T,TValue>>` | plain values on a symbolic `x` | No expression tree, no delegate. This is why it is AOT-safe and fully trimmable |
| `.NotNull()`, `.NotEmpty()` | `.Require()`, `.RequireAllowingEmpty()` | Name only |
| `.Length(1,100)` | `.Length(1,100)` | Same |
| `.InclusiveBetween(0,30)` | `.Range(0,30)` | Name only |
| `.GreaterThanOrEqualTo(x)` / `.LessThanOrEqualTo(x)` | `.RangeAtLeast(x)` / `.RangeAtMost(x)` | Name only |
| `.Matches(regex)` | `.Pattern(() => MyRegex())` | Takes a thunk; pair with `[GeneratedRegex]` |
| `.SetValidator(child)` | `rules.Nested(x.Child)` or `[ValidateNested]` | Declarative; no child validator to wire |
| `RuleForEach(x => x.Items)` | `rules.Each(x.Items)` | Name only |
| `.When(p)` / `.Unless(p)` | `if (p) { … }` / `if (!p) { … }` | Control flow is C#, evaluated where written |
| `.WithSeverity(...)` | `severity:` parameter | Name only |
| `.Must((model, value) => …)` | **`Ensure(bool)` — a plain condition, see below** | **Semantic difference** |
| `.WithMessage("{PropertyName} …")` | `message:` parameter, no interpolation | **Semantic difference** |
| `IValidator<T>` | **`IValidatorFor<T>`** | `IValidator<T>` belongs to FluentValidation. Never introduce it |
| `RuleSet` | none — deferred past 1.0.0 | Not a gap to work around; see Non-goals |

Shared rule sets are [fragments](docs/active-rules-redesign.md) — a `static void` method receiving
`rules`, expanded by the generator, generics included. Free-form findings report through
`rules.Context`; a chain is one statement and its own suppression unit.

### The two that are not name changes

**`Ensure` is not `Must`.** `rules.Ensure(x.Start < x.End)` takes a plain bool the generator
captures syntactically and transcribes into the region — `Describe` is static, so `this` cannot
exist, and a `private` member of the rules class is `VM0088` ("make it internal"; a private
const bakes by value). Do not reach for `.Must((model, value) => ...)` patterns — write the
condition inline, or use a hand-written `IValidatorFor<T>` composed through DI when the rule
genuinely needs a service.

**Messages carry no interpolated values.** `ValidationError` is `Field, Code, Message, Severity`.
Build UI and i18n off the stable `Code`, not by parsing the message.

### The baseline is DataAnnotations, not FluentValidation

The attribute surface is intentionally DataAnnotations-shaped, and there is a DataAnnotations front
end for migrating existing models. When comparing or explaining this library, compare it to
`System.ComponentModel.DataAnnotations` first — that is the surface it replaces and the audience it
serves. FluentValidation is a secondary comparison.

---

## Non-goals

These are deliberate. Do not report them as defects, design workarounds for them in library code, or
"fix" them without an explicit decision to change scope.

- **Arbitrary predicates in declared rules.** Rules are flattened at build time. A rule needing
  arbitrary C#, an injected service, or captured state belongs in a hand-written `IValidatorFor<T>`
  composed through DI.
- **Rule sets / profiles / overlays.** Deferred past 1.0.0; declaration surfaces were withdrawn on
  purpose. See `docs/deferred-features.md`.
- **Localized message catalogues.** Not shipped. `Code` is the stable contract for a consumer's own
  i18n layer.
- **Runtime rule composition.** Architecturally excluded — the rule graph is built once at compile
  time.
- **Cross-assembly scanning.** Registration is emitted per assembly as `Add<Assembly>Validators()`.

---

## Non-negotiables

From `IMPLEMENTATION-PLAN.md` §2, repeated because they are easy to violate by habit:

- No `MakeGenericType`, `Activator.CreateInstance`, `Expression.Compile`, assembly scanning, or
  `Type.GetMethod(...).Invoke`. Anywhere.
- **Emitted C# is authored with CSharpAuthor** — `CSharpFileDefinition` + `OutputContext`, the same
  path `DependencyFileWriter` takes in DependencyModules. Never `StringBuilder`, never a line of C#
  built by interpolation, never a raw string literal holding a class body. Both generator projects
  already reference the package. Runtime string building (`FieldNamer`, `RuleText`,
  `ValidationContext`) is not covered — it builds values, not source. **All three emitters comply
  as of 2026-08-28** — `IMPLEMENTATION-PLAN.md` §7.6 records the conversion and the shared
  settings in `Emitters/EmitterOutput.cs`. Do not add a non-compliant fourth.
- `[GeneratedRegex]`, never `new Regex(..., RegexOptions.Compiled)`.
- Nothing expensive is constructed per validation call — no graph building, no compiled-regex
  construction, no hot-path allocation. (Rules-class computation runs per call by design;
  `docs/active-rules-redesign.md`.)
- The service interface is `IValidatorFor<T>`.
- Registration is emitted per assembly; there is no cross-assembly scanning, deliberately.

`ValidationModules.Runtime` carries `IsAotCompatible` and escalates
`IL2026;IL2055;IL2067;IL2072;IL2075;IL2087;IL3050` to errors, so the compiler enforces the above
rather than review.

## Diagnostics are the teaching surface

A consumer — human or model — learns this library from compiler diagnostics far more than from
docs. A diagnostic that names the member, explains the constraint, and prints the replacement call
is worth more than a documentation page. `VM0017` is the standard to match. When adding or changing
a diagnostic, state what to do instead, not only what is wrong.

## Autonomy contract

Work here is routinely fanned out across parallel agents; every stop-and-ask multiplies.

**Decide and proceed. Do not ask.**

- **Convention questions are answered** in `IMPLEMENTATION-PLAN.md` §13 — test framework, assertion
  style, mocking, naming, brace style. Copy the nearest existing example.
- **The plan is a specification.** Execute it; do not re-litigate it.
- **Never block a fan-out.** Hit an ambiguity, pick the most reasonable option, write the test, note
  the assumption in your final report.
- **Batch uncertainty to the end** — one summary after the work.
- **Partial blockage does not stop the rest.** Finish everything else and report what was left out.

Stop only when proceeding would be destructive and irreversible, or when every reading of a
requirement would make the work useless if guessed wrong. Neither is common.

## Reviewing this library

If you are evaluating rather than building:

- **Lead with the task, not a competitor.** "Build this domain, report where a rule could not be
  expressed" finds real gaps. "Check whether it can do what FluentValidation does" manufactures them
  by making another library's surface the scoring rubric.
- **Grade findings by reachability.** A finding needs a path from ordinary use — generated output, a
  documented pattern, a plausible hand-written validator — before it ranks as a defect.
- **Separate defects from non-goals.** The list above is scope, not failure.
- **Date what you find.** `git log -S '<the line>'` distinguishes a regression from original design.

## Commands

```bash
dotnet build --configuration Release
dotnet test  --configuration Release
dotnet test  --configuration Release --collect:"XPlat Code Coverage" --settings coverlet.runsettings
UPDATE_SNAPSHOTS=1 dotnet test tests/ValidationModules.Runtime.Tests   # accept an intended API change
```

`TreatWarningsAsErrors` is on only when `ContinuousIntegrationBuild=true`. Leave the solution
warning-free.

## Permissions

`git add` and `git commit` are fine — commit freely. Gated by design, do not route around:
`git push`, `git merge`, `git rebase`, `git reset --hard`, `gh pr create`, `gh pr merge`,
`gh release`, `dotnet nuget push`.

## Publishing

Follows the `ipjohnson-org` pattern: `secrets.GITHUB_TOKEN` with `permissions: packages: write`
(not a PAT), explicit `--source` on `dotnet nuget push`, `actions/setup-dotnet@v4`, Release
configuration throughout when packing with `--no-build`.

## Related working directories

- `~/DependencyModules` — the package this builds on. Copy its project layout, packaging and CI.
- `~/Hardened` — the first consumer. `IMPLEMENTATION-PLAN.md` §9 and §10 reference it directly.
