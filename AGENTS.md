# ValidationModules — agent guide

Compile-time validation for .NET. Rules are declared as attributes or in a rules class. A source
generator flattens them into straight-line C# at build time. Native AOT is a hard requirement, not
a supported configuration.

This file is the guide for every agent working in this repo.

## Where current truth lives

The design documents (`IMPLEMENTATION-PLAN.md`, `API-SURFACE.md`, `HANDOFF.md`, `docs/`) were
deleted on 2026-08-30 because they had drifted from the code. Use these instead:

| Question | Source |
|---|---|
| What is the public API? | `tests/ValidationModules.Runtime.Tests/Snapshots/PublicApiTests.RuntimeApi.verified.txt` |
| How does a consumer use this? | `website/` (VitePress site) |
| What diagnostics exist? | `src/ValidationModules.SourceGenerator.Impl/AnalyzerReleases.Shipped.md` |
| Why is this line here? | `git log -S '<the line>'` |

Do not recreate the deleted documents. Record decisions in code comments, tests, or the website.

## Coming from FluentValidation

FluentValidation dominates the .NET validation training data, so most agents arrive with its model
in mind. Most of it maps across. Two things do not, and those two are where agents go wrong.

| FluentValidation | Here | Difference |
|---|---|---|
| `class V : AbstractValidator<T>` plus constructor body | `class V : IValidationRulesFor<T>` plus `static Describe(ValidationRules<T> rules, T x)` | Interface, not base class. The body is read at build time and never executed. |
| `RuleFor(x => x.Name)` | `rules.Require(x.Name)` | Values, not selectors. The generator resolves `x.Name` as a symbol. |
| `Expression<Func<T,TValue>>` | Plain values on a symbolic `x` | No expression tree and no delegate. This is what makes it AOT-safe and trimmable. |
| `.NotNull()`, `.NotEmpty()` | `.Require()`, `.RequireAllowingEmpty()` | Name only |
| `.Length(1,100)` | `.Length(1,100)` | Same |
| `.InclusiveBetween(0,30)` | `.Range(0,30)` | Name only |
| `.GreaterThanOrEqualTo(x)` / `.LessThanOrEqualTo(x)` | `.RangeAtLeast(x)` / `.RangeAtMost(x)` | Name only |
| `.Matches(regex)` | `.Pattern(() => MyRegex())` | Takes a thunk. Pair it with `[GeneratedRegex]`. |
| `.SetValidator(child)` | `rules.Nested(x.Child)` or `[ValidateNested]` | Declarative. There is no child validator to wire up. |
| `RuleForEach(x => x.Items)` | `rules.Each(x.Items)` | Name only |
| `.When(p)` / `.Unless(p)` | `if (p) { … }` / `if (!p) { … }` | Control flow is plain C#, evaluated where written. |
| `.WithSeverity(...)` | `severity:` parameter | Name only |
| `.Must((model, value) => …)` | `Ensure(bool)` | Semantic difference. See below. |
| `.WithMessage("{PropertyName} …")` | `message:` parameter, no interpolation | Semantic difference. See below. |
| `IValidator<T>` | `IValidatorFor<T>` | `IValidator<T>` is FluentValidation's name. Never introduce it here. |
| `RuleSet` | Not implemented | Deferred past 1.0.0. See Non-goals. |

Shared rule sets are written as fragments: a `static void` method that receives `rules` and is
expanded by the generator, generics included. Free-form findings report through `rules.Context`. A
chain is one statement and one suppression unit.

### `Ensure` is not `Must`

`rules.Ensure(x.Start < x.End)` takes a plain bool. The generator captures it syntactically and
transcribes it into the generated region. `Describe` is static, so `this` does not exist, and
referring to a `private` member of the rules class produces `VM3004`. Write the condition inline.
If a rule genuinely needs a service or captured state, write a hand-written `IValidatorFor<T>` and
compose it through dependency injection.

### Messages carry no interpolated values

`ValidationError` is `Field`, `Code`, `Message`, `Severity`. Build UI and localization off the
stable `Code`. Do not parse the message.

### The baseline is DataAnnotations

The attribute surface is deliberately shaped like `System.ComponentModel.DataAnnotations`, and
there is a DataAnnotations front end for migrating existing models. When comparing or explaining
this library, compare it to DataAnnotations first. That is the surface it replaces. FluentValidation
is the secondary comparison.

## Non-goals

These are deliberate. Do not report them as defects, work around them in library code, or change
them without an explicit decision to change scope.

- **Arbitrary predicates in declared rules.** Rules are flattened at build time. A rule that needs
  arbitrary C#, an injected service, or captured state belongs in a hand-written `IValidatorFor<T>`.
- **Rule sets, profiles, and overlays.** Deferred past 1.0.0. The declaration surfaces were
  withdrawn on purpose.
- **Localized message catalogues in the core packages.** `Code` is the stable contract for a
  consumer's own localization layer. `ValidationModules.Messages` ships translation data separately.
- **Runtime rule composition.** The rule graph is built once at compile time.
- **Cross-assembly scanning.** Registration is emitted per assembly as `Add<Assembly>Validators()`.

## Non-negotiables

These are easy to violate by habit.

- No `MakeGenericType`, `Activator.CreateInstance`, `Expression.Compile`, assembly scanning, or
  `Type.GetMethod(...).Invoke`. Anywhere.
- Emitted C# is built with CSharpAuthor, using `CSharpFileDefinition` and `OutputContext`. Never
  `StringBuilder`, never string interpolation, never a raw string literal holding a class body. Both
  generator projects already reference the package, and shared settings live in
  `Emitters/EmitterOutput.cs`. All three emitters comply. Do not add a fourth that does not.
  Runtime string building such as `FieldNamer`, `RuleText`, and `ValidationContext` is exempt,
  because it produces values rather than source code.
- Use `[GeneratedRegex]`. Never `new Regex(..., RegexOptions.Compiled)`.
- Nothing expensive is constructed per validation call. No graph building, no regex construction, no
  allocation on the hot path. Rules-class computation runs per call by design.
- The service interface is `IValidatorFor<T>`.

`ValidationModules.Runtime` sets `IsAotCompatible` and escalates `IL2026`, `IL2055`, `IL2067`,
`IL2072`, `IL2075`, `IL2087`, and `IL3050` to errors, so the compiler enforces most of this.

## Diagnostics

Consumers learn this library from compiler diagnostics more than from documentation. A diagnostic
that names the member, states the constraint, and prints the replacement call is worth more than a
documentation page. `VM1301` is the standard to match. When adding or changing a diagnostic, say
what to do instead, not only what is wrong.

## Working style

Work here is often split across parallel agents, so every question asked back multiplies.

- Decide and proceed. Do not ask.
- Copy the nearest existing example for test framework, assertion style, mocking, naming, and brace
  style. Do not ask which to use.
- On hitting an ambiguity, pick the most reasonable option, write the test, and note the assumption
  in the final report.
- Collect uncertainty into one summary at the end rather than interrupting.
- If one part of the work is blocked, finish the rest and say what was left out and why.

Stop only when proceeding would be destructive and irreversible, or when every reading of the
request would make the work useless if guessed wrong. Neither is common.

## Writing

This applies to code comments, commit messages, documentation, and messages to the user.

- Use the identifier from the code. Do not invent a name or metaphor for something that already has
  one.
- One idea per sentence. No em-dash asides.
- No sentence fragments for emphasis.
- Do not restate a point a second time in more figurative words.
- Commit subjects are imperative and say what changed. No wordplay.
- Write a comment only to state something the code cannot show. Do not narrate the next line.

## Reviewing this library

- Lead with a task, not a competitor. "Build this domain and report where a rule could not be
  expressed" finds real gaps. "Check whether it does what FluentValidation does" manufactures gaps
  by scoring against another library's surface.
- Grade a finding by reachability. It needs a path from ordinary use before it counts as a defect.
- Separate defects from the non-goals above. That list is scope, not failure.
- Date what you find. `git log -S '<the line>'` distinguishes a regression from the original design.

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

`git add` and `git commit` are fine. Commit freely.

These are gated deliberately. Do not route around them: `git push`, `git merge`, `git rebase`,
`git reset --hard`, `gh pr create`, `gh pr merge`, `gh release`, `dotnet nuget push`.

## Publishing

Follows the `ipjohnson-org` pattern: `secrets.GITHUB_TOKEN` with `permissions: packages: write`
rather than a personal access token, an explicit `--source` on `dotnet nuget push`,
`actions/setup-dotnet@v4`, and Release configuration throughout when packing with `--no-build`.

## Related working directories

- `~/DependencyModules` is the package this builds on. Copy its project layout, packaging, and CI.
- `~/Hardened` is the first consumer.
