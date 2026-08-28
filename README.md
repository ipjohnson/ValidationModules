# ValidationModules

Compile-time validation for .NET. Rules are declared as attributes and flattened into straight-line
C# by a source generator at build time. No reflection, no expression trees, no regex compiled at
runtime — Native AOT is a hard requirement rather than a supported configuration.

```csharp
public record Pet {
    [Required]
    [StringLength(min: 1, max: 100)]
    public string Name { get; init; }

    [Pattern("^[a-zA-Z0-9-]*$")]
    public string? Sku { get; init; }

    [ValidateNested]
    public Address? Home { get; init; }

    [ItemCount(min: 1, max: 10), ValidateNested]
    public IReadOnlyList<Toy> Toys { get; init; } = [];
}
```

```csharp
var result = validator.Validate(pet);

foreach (var error in result.Errors) {
    Console.WriteLine($"{error.Field}: {error.Code}");
}
// name             required
// home.postalCode  required
// toys[3].name     required
```

## Why

Native AOT is a hard requirement here rather than a supported configuration, and that single
decision produces the rest of the design. Rules are read at build time and flattened into
straight-line C#: the generated validator is a sequence of `if` statements over your own properties,
in a file you can open and read. Nothing is compiled on first use, no rule graph is walked per call,
and there is no reflection on the path at all.

**It publishes clean, and the compiler is what says so.** `ValidationModules.Runtime` carries
`IsAotCompatible` and escalates `IL2026;IL2055;IL2067;IL2072;IL2075;IL2087;IL3050` to errors, so the
constraint is enforced at build time rather than by review. `scripts/verify-aot.sh` then publishes a
real AOT binary and runs it — paths, nested descents, codes, the ASP.NET Core filter — so the claim
is checked end to end on every release rather than asserted. There is no separate AOT path to keep
in step, because there is no runtime code generation for AOT to have taken away.

**A pass that finds nothing allocates nothing.** Not nearly nothing — `0 B`, on the hot path and
through a pooled collector, flat and nested alike; `Validate` allocates only the small result object
it hands back. That floor is defended rather than observed: *a validation pass that finds nothing
must allocate nothing* is §4 of the plan restated as a benchmark, and
`ValidationContextBenchmarks.Push_NoAdd` has to read `0 B` at every nesting depth or the change is a
regression whatever its timings say. A clean flat pass runs in roughly 30 ns and a nested one in
about 100 ns.

**What you pay for is what failed.** Messages are composed by the runtime on the failure path rather
than emitted as literals at every constraint site, which keeps them out of your assembly's string
heap entirely — measured at 107 of the 313 native bytes a constraint would otherwise cost. A passing
request allocates nothing to discover that it passed; a failing one composes a message immediately
before a 400 response that costs considerably more to serialize.

*Timings from `./scripts/benchmark.sh` on an Apple M3 Pro under .NET 10 — approximate, and the
reason the script is in the repository rather than the numbers. The allocation figures are exact.*

## Status

Release candidate for 1.0.0. Built so far:

| Stage | | |
|---|---|---|
| 1 | Runtime — contracts, context, error model, constraint attributes, naming | **done** |
| 2 | Generator, no profiles | **done** |
| 3 | Profiles | deferred past 1.0.0 |
| 4 | `Impl` packaging for framework authors | **done** |
| 5 | Hardened integration | substantially done |
| 6 | FluentValidation adapter and conformance suite | not started |
| — | ASP.NET Core integration | **done** |

Also built since the plan was written, and not in its staging: a declarative rule-class front end
(`IValidationRulesFor<T>`, `API-SURFACE.md` §19) and a DataAnnotations front end (§18).

**Profiles and overlays are deferred past 1.0.0, and their declaration surfaces have been
withdrawn.** Both shipped before the features behind them: using a profile argument was `VM0019`, an
error, and `[ValidationOverlayFor<T>]` was read by nothing at all. A 1.0.0 pins the public surface,
and members whose only behaviour is a build failure — or no behaviour whatsoever — are the wrong
thing to pin. Every removal is additively reversible; `docs/deferred-features.md` records the
analysis, including the one member set that needs default interface methods to come back without
breaking implementers.

Every declared diagnostic now has a report site. `DiagnosticCatalogueTests` fails in both
directions, so a new descriptor without one cannot ship.

## Documentation

The docs site lives in `website/` and publishes to
<https://ipjohnson.github.io/ValidationModules/>:

```bash
cd website && npm install && npm run dev
```

Dead internal links fail the build, so a rename cannot rot a link silently.

## Design

- `IMPLEMENTATION-PLAN.md` — what is being built and why. A specification, not a discussion
  document.
- `API-SURFACE.md` — the exact public surface, the reasoning behind each decision, and the
  verification log behind the claims.

The single most consequential decision is in `API-SURFACE.md` §13.1: `ValidationContext` is a
`readonly struct` rather than a `ref struct`, carrying its own path rather than indexing into shared
storage. That is what lets `IAsyncValidatorFor<T>` take the same context as the synchronous side,
and what makes a context safe to hold across an await or hand to a concurrent branch.

## Packages

| Package | Ships as | Referenced by |
|---|---|---|
| `ValidationModules.Runtime` | `lib/` | application code |
| `ValidationModules.SourceGenerator` | `analyzers/dotnet/cs` | application code, `PrivateAssets=all` |
| `ValidationModules.AspNetCore` | `lib/` | web applications |
| `ValidationModules.SourceGenerator.Impl` | source-only | framework authors |
| `ValidationModules.FluentValidation` | `lib/` | planned adapter |
| `ValidationModules.Testing` | `lib/` | planned conformance suite |

`ValidationModules.Runtime` depends only on `Microsoft.Extensions.DependencyInjection.Abstractions`,
framework-matched per TFM. It does **not** reference `DependencyModules.Runtime` — the library is
DependencyModules-shaped in its ergonomics, but only the *generated module* needs DM types, and that
lands in the consumer's assembly, which already references DM.

## Building

```bash
dotnet build --configuration Release
dotnet test  --configuration Release
dotnet test  --configuration Release --collect:"XPlat Code Coverage" --settings coverlet.runsettings
```

The public API is pinned by a snapshot at
`tests/ValidationModules.Runtime.Tests/Snapshots/PublicApiTests.RuntimeApi.verified.txt` — one file
listing every public type and member, which is also the quickest way to read the surface. To accept
an intended change:

```bash
UPDATE_SNAPSHOTS=1 dotnet test tests/ValidationModules.Runtime.Tests
```

## ASP.NET Core

```csharp
builder.Services.AddMyAppValidators();

app.MapPost("/orders", (CreateOrder order) => Results.Ok())
   .Validate<CreateOrder>();
```

A failure answers with RFC 9457 before the handler runs, carrying the field paths and the stable
codes. The type argument is named rather than inferred, which is what keeps the request path free of
reflection — `website/guide/aspnetcore.md` covers why, and what the response looks like.

## Benchmarks

```bash
./scripts/benchmark.sh                     # ValidationModules alone, JIT and Native AOT
./scripts/benchmark.sh --quick             # the same, fast enough to run after a change
./scripts/benchmark.sh --comparative       # against FluentValidation and DataAnnotations
```

Two suites. The default one measures this library on its own and is what a change should be checked
against; the comparative one is opt-in, because its numbers move when FluentValidation changes as
well as when this does. `benchmarks/README.md` covers what each measures, and the four choices the
comparative suite makes in FluentValidation's favour so the comparison stays honest.

## License

MIT.
