<!-- Absolute URLs so the logo resolves when this file is packed into the NuGet package. It is sized
     to the H1 text so default baseline alignment sits it level with the name; NuGet's sanitizer
     strips an align attribute anyway. -->
# <picture><source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/ipjohnson/ValidationModules/main/assets/logo-dark.svg"><img src="https://raw.githubusercontent.com/ipjohnson/ValidationModules/main/assets/logo.svg" alt="" width="32" height="32"></picture> ValidationModules

[![NuGet](https://img.shields.io/nuget/v/ValidationModules.Runtime.svg)](https://www.nuget.org/packages/ValidationModules.Runtime/)
[![build](https://github.com/ipjohnson/ValidationModules/actions/workflows/build-package.yaml/badge.svg)](https://github.com/ipjohnson/ValidationModules/actions/workflows/build-package.yaml)
[![coverage](https://raw.githubusercontent.com/ipjohnson/ValidationModules/badges/coverage.svg)](https://github.com/ipjohnson/ValidationModules/actions/workflows/build-package.yaml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.txt)

Compile-time validation for .NET. You declare constraints as attributes on a model, and a source
generator writes the validator into your assembly during the build.

Nothing reflects at runtime. There are no expression trees, no regex compiled at startup, and no
rule graph to assemble. A clean pass over a flat model takes 32 ns and allocates 56 bytes, where
FluentValidation takes 179 ns and 664 bytes for the same rules. Native AOT is verified by publishing
and running a real binary on every release.

## Declare and validate

Constraints go on the model:

```csharp
using ValidationModules.Constraints;

public record Pet {
    [Required]
    [StringLength(min: 1, max: 100)]
    public string? Name { get; init; }

    [Pattern("^[a-zA-Z0-9-]*$")]
    public string? Sku { get; init; }

    [ValidateNested]
    public Address? Home { get; init; }

    [ItemCount(min: 1, max: 10), ValidateNested]
    public IReadOnlyList<Toy> Toys { get; init; } = [];
}
```

The generator emits one validator per type and a single registration call for the assembly:

```csharp
using ValidationModules;

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

Every error carries a field path and a stable `Code`. Build UI and localization off the code rather
than the message text.

## Install

```bash
dotnet add package ValidationModules.Runtime
dotnet add package ValidationModules.SourceGenerator
```

Web applications also want the [ASP.NET Core integration](#aspnet-core):

```bash
dotnet add package ValidationModules.AspNetCore
```

Requires .NET 8.0 or later. The packages ship both `net8.0` and `net10.0` assemblies, so a project
on either LTS release gets one built against its own framework.

## Rules classes

Some rules do not fit an attribute. A cross-field comparison, a computed total, or a type you cannot
edit belongs in a rules class. It is full C#, read at build time and never executed.

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

Locals, `if`/`else`, and helper calls transcribe into the generated validator and run there. The
vocabulary calls expand into the same checks the attributes produce.

Both declaration layers are build-time only. What ships is the generated validators plus the small
reporting runtime, and under trimming or Native AOT the rules class itself is gone. See
[Rule classes](https://ipjohnson.github.io/ValidationModules/guide/rule-classes) for the full
vocabulary.

## Performance

| Scenario | ValidationModules | FluentValidation | DataAnnotations |
|---|---|---|---|
| Clean pass, flat model | 32 ns · 56 B | 179 ns · 664 B | 958 ns · 2,696 B |
| Clean pass, flat model, `IsValid` only | 23 ns · 0 B | — | — |
| Five failures, flat model | 169 ns · 1,072 B | 2,404 ns · 9,904 B | 1,582 ns · 4,136 B |
| Clean pass, nested graph | 110 ns · 56 B | 1,817 ns · 5,224 B | 581 ns, top level only |
| 1,000-element collection | 15.4 µs · 56 B | 236 µs · 826 KB | — |

Measured with BenchmarkDotNet on an Apple M3 Pro, .NET 10.0.10, FluentValidation 12.1.1. All three
engines validate identical models carrying the same rules, and every cross-engine row is a full pass
that materializes a result on both sides.

Allocations are counted rather than timed, so they are exact and do not move between runs. The 56 B
is the result object. The pass itself allocates nothing at any nesting depth or collection size.

The `IsValid` row stands alone because neither competitor has a boolean-only API to pair with it.
That call returns at the first failure and builds no report.

DataAnnotations does not descend into nested objects or collection elements. Its nested figure
covers the top level only, and it has no figure for the collection row at all.

The suite refuses to run unless all three engines report the same failure counts. FluentValidation
runs with `CascadeMode.Stop` and the same `[GeneratedRegex]` instances the generated code uses, and
every validator is constructed once in setup. Run `./scripts/benchmark.sh --comparative` to
reproduce the numbers with full tables and error terms.

## Native AOT

`ValidationModules.Runtime` sets `IsAotCompatible` and escalates the trim and AOT warnings
(`IL2026`, `IL3050`, and the rest) to errors, so the compiler enforces the constraint rather than
code review. `scripts/verify-aot.sh` then publishes a real AOT binary on every release and runs it
against field paths, nested descents, error codes, and the ASP.NET Core filter. There is no separate
AOT mode, because no part of the library generates code at runtime.

## ASP.NET Core

```csharp
builder.Services.AddMyAppValidators();

app.MapPost("/orders", (CreateOrder order) => Results.Ok())
   .Validate<CreateOrder>();
```

A failed request gets an RFC 9457 response before the handler runs, carrying the field paths and the
stable codes. You name the type argument rather than let it be inferred, which keeps reflection out
of the request path. See
[ASP.NET Core](https://ipjohnson.github.io/ValidationModules/guide/aspnetcore) for the response
shape and the reasoning.

## Packages

| Package | Ships as | Referenced by |
|---|---|---|
| `ValidationModules.Runtime` | `lib/` | application code |
| `ValidationModules.SourceGenerator` | `analyzers/dotnet/cs` | application code, with `PrivateAssets=all` |
| `ValidationModules.AspNetCore` | `lib/` | web applications |
| `ValidationModules.Messages` | `messages/` and `build/` | applications wanting non-English messages |
| `ValidationModules.SourceGenerator.Impl` | source-only | framework authors |

`ValidationModules.Runtime` depends only on
`Microsoft.Extensions.DependencyInjection.Abstractions`, matched to the target framework. It does
not reference `DependencyModules.Runtime`. Only the generated module needs DependencyModules types,
and that module lands in your assembly, which already references DependencyModules if you use it.

## Documentation

The docs site is at <https://ipjohnson.github.io/ValidationModules/> and lives in `website/`:

```bash
cd website && npm install && npm run dev
```

`AGENTS.md` is the working guide for this repository.

## Building

```bash
dotnet build --configuration Release
dotnet test  --configuration Release
```

The public API is pinned by a snapshot at
`tests/ValidationModules.Runtime.Tests/Snapshots/PublicApiTests.RuntimeApi.verified.txt`, which is
also the quickest way to read the surface. Accept an intended change with:

```bash
UPDATE_SNAPSHOTS=1 dotnet test tests/ValidationModules.Runtime.Tests
```

## License

MIT.
