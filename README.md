<!-- Absolute URLs so the image survives being packed into the NuGet README; GitHub swaps the
     variant with its theme. The logo is sized to the H1 text (~32px), so default baseline
     alignment sits it level with the name on every renderer - no align attribute, which NuGet's
     sanitizer strips anyway. -->
# <picture><source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/ipjohnson/ValidationModules/main/assets/logo-dark.svg"><img src="https://raw.githubusercontent.com/ipjohnson/ValidationModules/main/assets/logo.svg" alt="" width="32" height="32"></picture> ValidationModules

[![NuGet](https://img.shields.io/nuget/v/ValidationModules.Runtime.svg)](https://www.nuget.org/packages/ValidationModules.Runtime/)
[![build](https://github.com/ipjohnson/ValidationModules/actions/workflows/build-package.yaml/badge.svg)](https://github.com/ipjohnson/ValidationModules/actions/workflows/build-package.yaml)
[![coverage](https://raw.githubusercontent.com/ipjohnson/ValidationModules/badges/coverage.svg)](https://github.com/ipjohnson/ValidationModules/actions/workflows/build-package.yaml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.txt)

Compile-time validation for .NET. Attributes on your models become straight-line C# at build time,
so a validation pass is a handful of `if` statements: **~32 ns and 56 B** for a clean pass over a
flat model — **23 ns and 0 B** on the boolean fast path — where FluentValidation takes 179 ns and
664 B for the same work. No reflection, no expression trees, no regex compiled at runtime, and a
Native AOT publish is verified on every release, not just supported.

## Install

```bash
dotnet add package ValidationModules.Runtime
dotnet add package ValidationModules.SourceGenerator
```

Web apps also want the [ASP.NET Core integration](#aspnet-core):

```bash
dotnet add package ValidationModules.AspNetCore
```

Requires .NET 8.0 or later. The packages ship both `net8.0` and `net10.0` assemblies, so a
project on either LTS release gets one built against its own framework.

## Declare, then validate

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

The generator emits a validator per type and one registration call for the assembly:

```csharp
services.AddMyAppValidators();                 // named after your assembly

var validator = provider.GetRequiredService<IValidatorFor<Pet>>();
var result = validator.Validate(pet);

foreach (var error in result.Errors) {
    Console.WriteLine($"{error.Field}: {error.Code}");
}
// name             required
// home.postalCode  required
// toys[3].name     required
```

## Rules as code

For the type you cannot edit, and the rule that is not a per-property fact, write a rules class —
full C#, **read at build time and never run**:

```csharp
public sealed class OrderRules : IValidationRulesFor<Order> {
    public static void Describe(ValidationRules<Order> rules, Order x) {
        rules.Require(x.Number).Length(4, 12);

        if (x.International) {
            rules.Require(x.CustomsCode);
        }

        var total = x.Lines?.Sum(l => l.Price * l.Qty) ?? 0m;
        rules.Ensure(total <= x.CreditLimit);   // message: "total <= creditLimit."
    }
}
```

Locals, `if`/`else` and helpers transcribe into the generated validator and run there;
vocabulary calls expand into the same checks the attributes produce. The declaration layer —
attributes and rules classes alike — is build-time-only: what ships is generated validators plus
the small reporting runtime, and under trimming or Native AOT a rules class disappears
entirely. See [Rule classes](https://ipjohnson.github.io/ValidationModules/guide/rule-classes).

## Performance

| Scenario | ValidationModules | FluentValidation | DataAnnotations |
|---|---|---|---|
| Clean pass, flat model | 32 ns · 56 B | 179 ns · 664 B | 958 ns · 2,696 B |
| Clean pass, boolean fast path¹ | 23 ns · 0 B | — | — |
| Five failures, flat model | 169 ns · 1,072 B | 2,404 ns · 9,904 B | 1,582 ns · 4,136 B |
| Clean pass, nested graph | 110 ns · 56 B | 1,817 ns · 5,224 B | 581 ns · top level only² |
| 1,000-element collection | 15.4 µs · 56 B | 236 µs · 826 KB | — |

Measured with BenchmarkDotNet (Apple M3 Pro, .NET 10.0.10, FluentValidation 12.1.1) on identical
models carrying the same rules; every cross-engine row is a full pass that materializes a result
on both sides. Allocations are counted, not timed, and are exact — the 56 B is the result object,
and the pass itself allocates nothing at any nesting depth or collection size. ¹The generated
`IsValid` returns at the first failure and builds no report; it is measured on its own row
because neither competitor has a boolean-only API to pair it with. ²DataAnnotations does not
descend into nested objects or collection elements. A parity check refuses to run the suite
unless all three engines find the same failure counts, FluentValidation runs with
`CascadeMode.Stop` and the same `[GeneratedRegex]` instances as the generated code, and every
validator is constructed once in setup. Reproduce the numbers, with full tables and error terms,
by running `./scripts/benchmark.sh --comparative`.

## Native AOT

`ValidationModules.Runtime` carries `IsAotCompatible` and escalates the trim/AOT warnings
(`IL2026`, `IL3050`, and friends) to errors, so the compiler enforces the constraint.
`scripts/verify-aot.sh` then publishes a real AOT binary and runs it — paths, nested descents,
error codes, the ASP.NET Core filter — on every release. There is no separate AOT mode, because
there is no runtime code generation for AOT to take away.

## ASP.NET Core

```csharp
builder.Services.AddMyAppValidators();

app.MapPost("/orders", (CreateOrder order) => Results.Ok())
   .Validate<CreateOrder>();
```

A failure answers with RFC 9457 before the handler runs, carrying the field paths and stable
codes. The type argument is named rather than inferred, which keeps the request path free of
reflection — `website/guide/aspnetcore.md` covers why, and what the response looks like.

## Documentation

The docs site publishes to <https://ipjohnson.github.io/ValidationModules/> and lives in
`website/`:

```bash
cd website && npm install && npm run dev
```

`AGENTS.md` is the working guide for this repository. The exact public surface is pinned by
`tests/ValidationModules.Runtime.Tests/Snapshots/PublicApiTests.RuntimeApi.verified.txt`.

## Packages

| Package | Ships as | Referenced by |
|---|---|---|
| `ValidationModules.Runtime` | `lib/` | application code |
| `ValidationModules.SourceGenerator` | `analyzers/dotnet/cs` | application code, `PrivateAssets=all` |
| `ValidationModules.AspNetCore` | `lib/` | web applications |
| `ValidationModules.SourceGenerator.Impl` | source-only | framework authors |
| `ValidationModules.FluentValidation` | `lib/` | planned adapter |
| `ValidationModules.Testing` | `lib/` | planned conformance suite |

`ValidationModules.Runtime` depends only on
`Microsoft.Extensions.DependencyInjection.Abstractions`, framework-matched per TFM. It does
**not** reference `DependencyModules.Runtime` — only the *generated module* needs DM types, and
that lands in the consumer's assembly, which already references DM.

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
| — | ASP.NET Core integration — minimal APIs | **done** |
| — | ASP.NET Core integration — MVC, Blazor, options validation | not started |

Also built: the rules-class front end (`IValidationRulesFor<T>`, which the generator reads at build
time and never executes) and a DataAnnotations front end. Profiles and overlays are deferred past
1.0.0 and their declaration surfaces were withdrawn. Both return additively when they ship.

## Building

```bash
dotnet build --configuration Release
dotnet test  --configuration Release
```

The public API is pinned by a snapshot at
`tests/ValidationModules.Runtime.Tests/Snapshots/PublicApiTests.RuntimeApi.verified.txt` — also
the quickest way to read the surface. Accept an intended change with:

```bash
UPDATE_SNAPSHOTS=1 dotnet test tests/ValidationModules.Runtime.Tests
```

## License

MIT.
