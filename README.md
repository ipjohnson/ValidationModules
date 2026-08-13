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

FluentValidation compiles expression trees at runtime. Under Native AOT `Expression.Compile()`
falls back to the LINQ interpreter rather than throwing, so it *works* — but property access is
interpreted and you carry IL2026/IL3050 trim warnings. For a workload where AOT is a requirement,
that is the gap this fills.

`ValidationModules.Runtime` carries `IsAotCompatible` and escalates `IL2026;IL2055;IL2067;IL2072;
IL2075;IL2087;IL3050` to errors, so the constraint is enforced by the compiler rather than by
review.

## Status

Pre-1.0, and under construction. Built so far:

| Stage | | |
|---|---|---|
| 1 | Runtime — contracts, context, error model, constraint attributes, naming | **done** |
| 2 | Generator, no profiles | not started |
| 3 | Profiles | not started |
| 4 | `Impl` packaging for framework authors | not started |
| 5 | Hardened integration | not started |
| 6 | FluentValidation adapter and conformance suite | not started |

Until Stage 2 lands there is no generator, so validators are hand-written. The shape they must take
is pinned by the tests in `tests/ValidationModules.Runtime.Tests/Infrastructure/SampleModel.cs`,
which double as the emitter's specification.

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
| `ValidationModules.SourceGenerator.Impl` | source-only | framework authors |
| `ValidationModules.FluentValidation` | `lib/` | optional adapter |
| `ValidationModules.Testing` | `lib/` | conformance suite |

`ValidationModules.Runtime` depends only on `Microsoft.Extensions.DependencyInjection.Abstractions`,
framework-matched per TFM. It does **not** reference `DependencyModules.Runtime` — the library is
DependencyModules-shaped in its ergonomics, but only the *generated module* needs DM types, and that
lands in the consumer's assembly, which already references DM.

## Building

```bash
dotnet build --configuration Release
dotnet test  --configuration Release
dotnet test  --configuration Release --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings
```

The public API is pinned by a snapshot at
`tests/ValidationModules.Runtime.Tests/Snapshots/PublicApiTests.RuntimeApi.verified.txt` — one file
listing every public type and member, which is also the quickest way to read the surface. To accept
an intended change:

```bash
UPDATE_SNAPSHOTS=1 dotnet test tests/ValidationModules.Runtime.Tests
```

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
