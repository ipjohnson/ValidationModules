# Trimming and Native AOT

Native AOT is a hard requirement here rather than a supported configuration. That distinction shows
up in what the library *cannot* do, not in what it claims.

## What is banned, and enforced

None of these appears anywhere in the runtime or in emitted code:

- `MakeGenericType`
- `Activator.CreateInstance`
- `Expression.Compile`
- assembly scanning
- `Type.GetMethod(…).Invoke`
- `new Regex(pattern, RegexOptions.Compiled)`

Not by convention. `ValidationModules.Runtime` carries `IsAotCompatible` and escalates the trim and
AOT analyzer warnings to errors:

```xml
<IsAotCompatible>true</IsAotCompatible>
<WarningsAsErrors>IL2026;IL2055;IL2067;IL2072;IL2075;IL2087;IL3050</WarningsAsErrors>
```

So the compiler enforces it on every build of this library, rather than review catching it
sometimes.

## Why this exists

FluentValidation compiles expression trees at run time. Under Native AOT `Expression.Compile()` does
not throw — it falls back to the LINQ interpreter. So FluentValidation *works* under AOT, which is
the awkward part: property access is interpreted rather than compiled, and you carry `IL2026` and
`IL3050` warnings into a published build.

Nothing fails. It just costs more than it looks like it costs, in the configuration where you can
least afford it, and the warnings that would tell you are the ones every project learns to suppress.

## Where reflection would otherwise creep in

Three places, each closed deliberately:

**Registration.** A `(serviceType, implementationType)` pair goes through `ActivatorUtilities`, which
finds a constructor reflectively. So the generator emits **factory delegates** instead, and generated
validators are parameterless with a static `Instance` for the delegate to return.

**Nested validators.** Injecting them would require the same activation. They are referenced
from an array the constructor materialised — `validatorsHome[vi].Validate(ref ctxHome, nestedHome)`.

**Runtime type dispatch.** "Give me the validator for this `Type`" is `MakeGenericType` territory.
Where that is needed the generator emits a switch over closed types instead, so every type is
statically referenced and the trimmer can see all of them.

## The one thing that needs your attention

Everything above is handled for you. This is not:

```csharp
[Pattern("^[A-Z]{3}$")] // roots the regex parser and interpreter — about 450 KB
public string? Sku { get; init; }
```

Building a `Regex` from a string at run time means the parser and interpreter have to be in the
binary, because the pattern is not known until the constructor runs. That is paid once, however many
patterns follow, and the trimmer cannot remove it.

Declare the pattern with `[GeneratedRegex]` and point at it:

```csharp
public static partial class PetPatterns {
    [GeneratedRegex("^[A-Z]{3}$")]
    public static partial Regex Sku();
}

[Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
public string? Sku { get; init; }
```

In an AOT-facing project the inline form is [VM0017](/reference/diagnostics#vm0017) — an error by
default — so you find out at build time. [Patterns and regex](/guide/patterns) has the full policy.

## Set `IsAotCompatible` on your model library

`PublishAot` is only ever true in the executable. A class library holding your models never sees it,
so gating diagnostics on `PublishAot` alone would push the failure onto somebody else's publish.

```xml
<PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
</PropertyGroup>
```

That is what a library sets when it means to be publishable, and this generator treats it as an AOT
signal for exactly that reason. Set it on the project that holds the models, not only on the app.

## Verifying

The repository ships a script that publishes a sample app with `PublishAot` and checks the result:

```bash
./scripts/verify-aot.sh
```

For your own application, the check that matters is that a published build produces no `IL2026` or
`IL3050` warnings originating in validation, and that the binary does not grow by ~450 KB when you
add your first pattern. If it does, you have an inline `[Pattern]` somewhere and the policy was set
to `Allow`.

## Allocation

A clean validation pass over a generated validator allocates nothing per `Push`, at any depth or
element count — the path lives inside the context struct, which is copied rather than heap-allocated.

Two caveats worth knowing rather than discovering:

- `validator.Validate(value)` constructs a collector (48 bytes) and a result. `IsValid(value)` and
  `ValidateInto(collector, value)` are the allocation-conscious entry points; the second lets you own
  and reuse the collector.
- `ValidationRunner<T>` holds its validators as `IEnumerable<T>`, so `foreach` over an
  array-as-`IEnumerable` boxes an enumerator — 32 bytes per call, and the async path pays it twice.
  Call the validator directly on a hot path where a single validator is registered.

## Trimming without AOT

Everything above applies to `PublishTrimmed` as well. The generated code is ordinary C# referencing
closed types, so the trimmer keeps exactly what is used and nothing depends on metadata surviving.
The one thing that will not trim away is the regex parser, if you rooted it.
