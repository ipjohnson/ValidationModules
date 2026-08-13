# Spike: MSBuild task + source generator, together

Proves the one route to `[GeneratedRegex]` for spec-driven patterns, and that it needs only one
parse of the spec.

## The problem

A source generator cannot emit `[GeneratedRegex]`. Its output is not in the compilation the regex
source generator sees, so the partial method is never implemented and the *consumer's* build fails
with CS8795. For attribute-declared patterns there is an answer — the consumer declares the
`[GeneratedRegex]` themselves and `[Pattern(typeof(X), nameof(X.Y))]` points at it. For a pattern
that came from a spec file there is no consumer source to put it in.

## The shape

```
pet.spec.yaml
   │
   ├─ ExtractSpec task, BeforeTargets="CoreCompile"        ← reads the spec, once
   │     ├─→ obj/SpecPatterns.g.cs   → @(Compile)          ← ordinary source, so the regex
   │     │                                                    generator implements it
   │     └─→ obj/spec-model.txt      → @(AdditionalFiles)
   │
   └─ ValidatorGenerator source generator                  ← reads the model, never the yaml
         └─→ PetValidator.g.cs, calling SpecPatterns.P_43e30b328479()
```

MSBuild runs before the compiler, so a file it puts in `@(Compile)` is indistinguishable from one a
human wrote. That is the whole trick — the same reason `RegisterPostInitializationOutput` works,
reached from outside the compiler.

## Two things worth copying

**One parse.** The task writes a normalized model and the generator reads *that*. The generator
needs no yaml parser, and so none of the embedded-DLL and `AssemblyResolve` machinery that comes
with one. In Hardened that would remove three embedded assemblies, the static constructor, the
resolve hook and an RS1035 suppression from `Hardened.OpenApi.SourceGenerator`.

**No shared naming convention.** The task names each pattern member by a hash of the pattern and
writes that name into the model, so the generator is told what to call rather than having to derive
it the same way. Identical patterns across a spec collapse to one member for free.

## Measured

```
no-pattern baseline      1,102,712
spec via task+generator  1,135,856   +33 KB   (two patterns)
same via new Regex()     1,550,264  +448 KB
```

Zero IL warnings, and the published AOT binary runs.

## Run it

```bash
dotnet build SpecTask/SpecTask.csproj
MSBUILDDISABLENODEREUSE=1 dotnet run --project App/App.csproj
```

`MSBUILDDISABLENODEREUSE=1` only matters while iterating on the task — MSBuild reuses long-lived
nodes and they hold the task assembly open.

## Not production shape

The spec parser is a deliberately small stand-in; a real task would use the same OpenAPI reader the
generator uses today. Packaging is by path rather than a `tasks/` folder. The thing to verify before
committing to this is **design-time builds** — if the target does not run in the IDE, the
intermediate files will not exist there and the generator produces nothing, which reads as red
squiggles everywhere while the CLI build works.
