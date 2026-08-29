---
layout: home

hero:
  name: ValidationModules
  text: Validation, decided at compile time
  tagline: >-
    Declare constraints on the model they belong to, and a source generator writes the checks during
    the build. Nothing reflects, nothing compiles an expression tree, and no regex is built at
    startup — so a Native AOT publish keeps every rule you declared.
  image:
    src: /hero.svg
    alt: A constrained model on the left becoming straight-line generated validation code on the right
  actions:
    - theme: brand
      text: Get started
      link: /guide/getting-started
    - theme: alt
      text: Constraints
      link: /guide/constraints
    - theme: alt
      text: View on GitHub
      link: https://github.com/ipjohnson/ValidationModules

features:
  - title: Validators you can read
    details: >-
      Every check is emitted into your assembly as plain C#. Set EmitCompilerGeneratedFiles and the
      file under obj/ is the ground truth — no rule graph to reason about, no startup cost, and
      nothing between the attribute you wrote and the branch that runs.
    link: /guide/constraints
    linkText: The constraint attributes

  - title: Native AOT is the requirement
    details: >-
      No MakeGenericType, no Activator.CreateInstance, no Expression.Compile, no assembly scanning.
      The runtime escalates the trim and AOT warnings to errors, so the compiler enforces this
      rather than review.
    link: /guide/aot
    linkText: Trimming and AOT

  - title: Mistakes reported at build time
    details: >-
      A length constraint on an int, a pattern that will not parse, bounds that can never both be
      satisfied, a constrained property with no getter — each is a VM diagnostic in the IDE rather
      than a rule that silently never fires.
    link: /reference/diagnostics
    linkText: Diagnostics reference

  - title: Three ways to declare a rule
    details: >-
      Native constraint attributes, System.ComponentModel.DataAnnotations compiled rather than
      reflected, and rule classes for a type you do not own. All three read into one model and out
      through one emitter, so a rule's origin stops mattering.
    link: /guide/rule-classes
    linkText: Rule classes

  - title: One error shape
    details: >-
      Declaration order, fixed wire codes, and a field path that reads home.postalCode or
      toys[3].name. A failed required suppresses the rest of its field, enforced where every engine
      reaches rather than in emitted control flow.
    link: /guide/errors
    linkText: The error model

  - title: Registers itself
    details: >-
      One generated call named after your assembly, so two of them compose without ceremony. With
      DependencyModules referenced you get a module wrapping the same body instead. Validators are
      singletons; resolving one costs about 4ns.
    link: /guide/registration
    linkText: Registration and DI

  - title: Answers a request
    details: >-
      An endpoint filter validates a minimal API argument before the handler runs and answers with
      RFC 9457, carrying the field paths and the stable codes rather than only English. Published
      Native AOT and served, not just unit tested.
    link: /guide/aspnetcore
    linkText: ASP.NET Core
---

<div class="vm-sample">

## Declare, and the check is built

Constraints live on the model. A source generator writes the validator during the build:

```csharp
using ValidationModules.Constraints;

public sealed record Pet {
    [Required]
    [StringLength(min: 1, max: 100)]
    public string? Name { get; init; }

    [Range(0, 30)]
    public int Age { get; init; }

    [ValidateNested]
    public Address? Home { get; init; }
}
```

What comes out the other side is the code you would have written by hand, in your own assembly:

```csharp
public sealed partial class PetValidator : IValidatorFor<Pet> {

    public ValidationFlow Validate(ref ValidationContext ctx, Pet value) {
        if (string.IsNullOrWhiteSpace(value.Name)) {
            if (ctx.ReportRequired("name").ShouldStop) return ValidationFlow.Stop;
        }
        else if (value.Name is not null && (value.Name.Length < 1 || value.Name.Length > 100)) {
            if (ctx.ReportStringLength("name", 1, 100).ShouldStop) return ValidationFlow.Stop;
        }

        if ((value.Age < 0 || value.Age > 30) && ctx.ReportRange("age", 0, 30).ShouldStop)
            return ValidationFlow.Stop;

        if (value.Home is { } nestedHome) {
            var ctxHome = ctx.Push("home");
            if (HomeValidators[0].Validate(ref ctxHome, nestedHome).ShouldStop)
                return ValidationFlow.Stop;
        }

        return ValidationFlow.Continue;
    }
}
```

And when a rule outgrows an attribute — cross-field facts, computation, a type you do not own — a
[rules class](/guide/rule-classes) is full C# that is **read at build time and never run**:

```csharp
public sealed class PetRules : IValidationRulesFor<Pet> {
    public static void Describe(ValidationRules<Pet> rules, Pet x) {
        if (x.Age > 20) {
            rules.Require(x.Name).Length(2, 40);   // control flow is just C#
        }

        rules.Ensure(x.Age != 13, code: "unlucky");
    }
}
```

Then run it. The entry points are extension methods, so the calling file imports
`ValidationModules` — the constraints namespace is for the model:

```csharp
using ValidationModules;

var result = new PetValidator().Validate(pet);

foreach (var error in result.Errors) {
    Console.WriteLine($"{error.Field}: {error.Code}");
}
// name             required
// home.postalCode  required
// toys[3].name     required
```

## Why the build, and not the request

Validation is the layer most likely to be quietly reflective. FluentValidation compiles an
expression tree per property access; `System.ComponentModel.DataAnnotations` walks attributes with
`Validator.TryValidateObject`. Both work — and under Native AOT neither fails loudly:
`Expression.Compile()` falls back to the LINQ interpreter, so the rules still run, just interpreted
and carrying `IL2026`/`IL3050` trim warnings into the published build. They cost more than they
look like they cost, in the configuration where you can least afford it.

Both have a home here anyway: existing DataAnnotations models
[compile as-is](/guide/data-annotations) — migration support, at your own pace — and
FluentValidation [translates almost one to one](/guide/getting-started#coming-from-another-library)
into attributes and rules classes.

## What it costs

Measured against the same rules expressed in FluentValidation and in DataAnnotations, on .NET 10,
Apple M3 Pro. The full method and the four choices made in FluentValidation's favour are in
[`benchmarks/README.md`](https://github.com/ipjohnson/ValidationModules/blob/main/benchmarks/README.md);
the run these came from, and what is and is not stable in it, is in
[`benchmarks/RESULTS.md`](https://github.com/ipjohnson/ValidationModules/blob/main/benchmarks/RESULTS.md).

| | ValidationModules | FluentValidation | DataAnnotations |
|---|---|---|---|
| Flat model, valid | **26.4 ns** / 40 B | 196 ns / 664 B | 1,031 ns / 2,696 B |
| Nested model, valid | **104 ns** / 40 B | 1,804 ns / 5,224 B | 588 ns *(top level only)* |
| 1,000 elements | **12.0 µs** / 40 B | 250 µs / 846 KB | *does not descend* |
| Resolve from DI | **4 ns** / **0 B** | ~4–6 µs / 11 KB | — |

The allocation column is the one worth reading twice, because it is counted rather than timed and so
does not move between runs. **40 bytes is the whole cost of a passing validation** — one result
object, the same for a flat model, a nested one, and a thousand elements, because the walk itself
allocates nothing.

The last row is a range rather than a figure deliberately. `AddValidatorsFromAssemblyContaining`
registers validators **scoped**, so by default FluentValidation rebuilds its rule graph on every
request. That costs about 11 KB per resolve, which is exact, and a few microseconds, which is
dominated by garbage collection and moves too much between runs on our hardware to quote more
precisely than that.

</div>

<style>
.vm-sample {
  max-width: 1152px;
  margin: 0 auto;
  padding: 0 24px 64px;
}

.vm-sample h2 {
  border-top: 1px solid var(--vp-c-divider);
  padding-top: 40px;
  margin-top: 8px;
}
</style>
