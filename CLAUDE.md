# ValidationModules

Compile-time validation for .NET. Rules declared as attributes, flattened into straight-line C# by
a source generator at build time. Native AOT is a hard requirement.

**Read `IMPLEMENTATION-PLAN.md` before starting any work.** It is the reasoning behind what was
built, and the traps in §7.2 were found the expensive way. It is no longer a description of the
library: §6 (profiles) and overlays were deliberately not shipped, and both say so at their head.
For what actually exists, the public surface is pinned by
`tests/ValidationModules.Runtime.Tests/Snapshots/PublicApiTests.RuntimeApi.verified.txt`.

Sibling working directories, both editable and both relevant:
- `~/DependencyModules` — the package this builds on. Copy its project layout, packaging and CI.
- `~/Hardened` — the first consumer. `IMPLEMENTATION-PLAN.md` §9 and §10 reference it directly.

## Autonomy contract

Work here is routinely fanned out across parallel agents; every stop-and-ask multiplies.

**Decide and proceed. Do not ask.**

- **Convention questions are answered** in `IMPLEMENTATION-PLAN.md` §13. Test framework, assertion
  style, mocking library, naming, brace style — settled. Copy the nearest existing example. Never
  ask "xUnit or NUnit", "FluentAssertions or Assert", "where does this file go".
- **The plan is a specification.** Execute it. Do not re-litigate it, do not ask which stage to
  start with, do not ask for confirmation between items.
- **Never block a fan-out.** A subagent that hits an ambiguity picks the most reasonable option,
  writes the test, and notes the assumption in its final report.
- **Batch uncertainty to the end** — one summary after the work, not interruptions along the way.
- **Partial blockage does not stop the rest.** Finish every other project in full and report what
  was left out and why.

Stop only when proceeding would be destructive and irreversible, or when every reading of a
requirement would make the work useless if guessed wrong. Neither is common.

## Non-negotiables

From `IMPLEMENTATION-PLAN.md` §2, repeated because they are easy to violate by habit:

- No `MakeGenericType`, `Activator.CreateInstance`, `Expression.Compile`, assembly scanning, or
  `Type.GetMethod(...).Invoke`. Anywhere.
- **Emitted C# is authored with CSharpAuthor** — `CSharpFileDefinition` + `OutputContext`, the same
  path `DependencyFileWriter` takes in DependencyModules. Never `StringBuilder`, never a line of C#
  built by interpolation, never a raw string literal holding a class body. Both generator projects
  already reference the package. Runtime string building (`FieldNamer`, `RuleText`,
  `ValidationContext`) is not covered — it builds values, not source. **The three emitters do not
  comply yet** — they predate the rule and are `StringBuilder` throughout; `IMPLEMENTATION-PLAN.md`
  §7.6 lists them. Match the rule, not the surrounding file, and do not add a fourth.
- `[GeneratedRegex]`, never `new Regex(..., RegexOptions.Compiled)`.
- Rule graphs are built once, never per validation call.
- The service interface is `IValidatorFor<T>` — `IValidator<T>` belongs to FluentValidation.
- Registration is emitted per assembly as `Add<Assembly>Validators()`; there is no cross-assembly
  scanning, deliberately.

`ValidationModules.Runtime` carries `IsAotCompatible` and escalates `IL2026;IL2055;IL2067;IL2072;
IL2075;IL2087;IL3050` to errors, so the compiler enforces the above rather than review.

## Commands

```bash
dotnet build --configuration Release
dotnet test  --configuration Release
dotnet test  --configuration Release --collect:"XPlat Code Coverage" --settings coverlet.runsettings
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
