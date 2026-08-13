# ValidationModules — implementation plan

**Written:** 2026-08-12
**Status:** design settled, nothing built yet
**Origin:** designed in a session against `~/Hardened` and `~/DependencyModules`. Every claim in
§10 was verified by reading those repos, not inferred. This document is self-contained — you do
not need that session's history.

---

## 1. What this is

A compile-time validation library for .NET, built to run under Native AOT with no reflection and
no expression trees. Rules are declared as attributes; a source generator flattens them into
straight-line C# validators at build time. It wires up through DependencyModules but does not
require it.

It exists because the incumbent — FluentValidation — compiles expression trees at runtime.
Under Native AOT `Expression.Compile()` falls back to the LINQ interpreter rather than throwing,
so FluentValidation *works*, but property access is interpreted and you carry IL2026/IL3050 trim
warnings. For a workload where AOT is a hard requirement, that is the gap.

The first consumer is `Hardened.Framework`, which today has validation only for OpenAPI-generated
handlers and reflection on its per-request path. See §9 and §10.

---

## 2. Non-negotiables

These are settled. Do not reopen them, and do not ask.

- **Native AOT is a hard requirement.** No `MakeGenericType`, no `Activator.CreateInstance`, no
  `Expression.Compile`, no assembly scanning, no `Type.GetMethod(...).Invoke`. Follow
  `DependencyModules.Runtime.csproj`'s posture: `IsAotCompatible` plus
  `WarningsAsErrors=IL2026;IL2055;IL2067;IL2072;IL2075;IL2087;IL3050`, so the compiler enforces
  it rather than review.
- **Generated validators are plain classes.** No attributes on them, no DI-framework knowledge.
- **Regex uses `[GeneratedRegex]`**, never `new Regex(..., RegexOptions.Compiled)`. See §10.2 for
  what happens when you get this wrong.
- **Rule graphs are built once**, never per validation call.
- **The service interface is `IValidatorFor<T>`, not `IValidator<T>`.** FluentValidation owns
  `IValidator<T>` and the adapter's authors will have both namespaces imported.
- **Profiles are opt-in.** A codebase that declares no profiles must never see the concept, and
  must generate exactly one validator per type.

---

## 3. Packages

Mirrors the DependencyModules layout, for the reason DM has it: a source-only package must flow
`Microsoft.CodeAnalysis.CSharp` to its consumers, and you cannot let that flow to every
application.

| Package | Ships as | Referenced by |
|---|---|---|
| `ValidationModules.Runtime` | `lib/` | application code |
| `ValidationModules.SourceGenerator` | `analyzers/dotnet/cs`, `IncludeBuildOutput=false` | application code, `PrivateAssets=all` |
| `ValidationModules.SourceGenerator.Impl` | source-only `Content` under `src/` | framework authors only (Hardened) |
| `ValidationModules.FluentValidation` | `lib/` | optional adapter |
| `ValidationModules.Testing` | `lib/` | conformance suite |

`ValidationModules.Runtime` depends **only** on `Microsoft.Extensions.DependencyInjection.Abstractions`,
framework-matched per TFM the way `DependencyModules.Runtime.csproj` does it. It does **not**
reference `DependencyModules.Runtime`.

That last point is the resolution of an apparent tension in the name. The library is
DependencyModules-shaped and DM-first in its ergonomics, but the runtime carries no DM dependency —
because the only thing that needs DM types is the *generated module*, which lands in the user's
assembly, which already references DM. See §7.3.

Copy `Directory.Build.props`, the `Package/*.props|targets` pattern, and the CI/publish workflow
from `~/DependencyModules`. Publishing follows the `ipjohnson-org` pattern: `secrets.GITHUB_TOKEN`
with `permissions: packages: write`, explicit `--source` on `dotnet nuget push`,
`actions/setup-dotnet@v4`, Release configuration throughout.

---

## 4. Core contracts

`ValidationModules.Runtime`:

```csharp
namespace ValidationModules;

/// Structural validation. Generated, stateless, singleton.
public interface IValidatorFor<in T> {
    void Validate(ref ValidationContext context, T value);
}

/// Business rules that need I/O. Hand-written, scoped, takes dependencies.
public interface IAsyncValidatorFor<in T> {
    ValueTask ValidateAsync(ValidationContext context, T value, CancellationToken cancellationToken);
}

/// Marker for a validation profile. See §6.
public interface IValidationProfile;
public interface IValidationProfile<TPredecessor> : IValidationProfile
    where TPredecessor : IValidationProfile;

public readonly record struct ValidationError(string Field, string Code, string Message);
```

`ValidationContext` carries the field-path prefix so nested errors surface as `home.postalCode`
and `toys[3].name`. Build the path lazily — a validation pass that finds nothing must allocate
nothing. Do not concatenate strings per nesting level; keep a small path stack and materialise
only when an error is actually added.

```csharp
public ref struct ValidationContext {
    public ValidationContext Push(string segment);
    public ValidationContext PushIndex(string segment, int index);
    public void Add(string code, string message);
    public bool HasErrors { get; }
}
```

`ValidationResult` accumulates errors. **Do not add a `public static readonly ValidationResult
Success`** — Hardened has one and it is a mutable process-wide singleton that any caller can
poison with `AddError`. Either make the result immutable or omit the static.

### Ordering and short-circuit semantics — pin these before writing the emitter

Two engines producing "the same" errors in different orders is not substitutable, and the
conformance suite (§8) cannot be written until these are decided:

- Errors emit in **declaration order**, deterministically.
- A failed `[Required]` **suppresses** other rules on the same field. Hardened gets this
  incidentally today because `StringLengthRule` early-returns on non-strings; make it explicit.
- All errors are collected; there is no first-failure exit.
- Field names come from a single `IValidationFieldNamer` that every engine must apply, so the
  FluentValidation adapter cannot emit `Home.PostalCode` where the generator emits
  `home.postalCode`.

---

## 5. Constraint attributes

```csharp
[Required]
[StringLength(min: 1, max: 100)]
[Range(0, 30)]
[Pattern("^[a-zA-Z0-9-]*$")]
[AllowedValues("available", "pending", "sold")]
[ItemCount(min: 1, max: 10)]
[ValidateNested]
```

Applied to properties. `[ValidateNested]` recurses into an object or, on a collection, into each
element. Every constraint attribute also carries the profile arguments from §6.

Provide a diagnostic for nonsense pairings — `[StringLength]` on an `int`, `[ItemCount]` on a
non-collection. Generator diagnostics are roughly half the work of a generator; budget for it.

---

## 6. Profiles

The design that took the longest to settle. Read this section before writing any emitter.

**The problem.** As a document standard moves, different rules apply to different versions of the
same type. v1 requires `Tag`; v2 relaxes it and adds `Species`. The same shape appears as FHIR
profiles, EDI/ISO20022 message versions, tenant overlays, and draft-vs-published state.

**The design.** Profiles are types. Each *rule* declares which profiles it belongs to.

```csharp
public sealed class V1 : IValidationProfile;
public sealed class V2 : IValidationProfile<V1>;   // successor of V1
public sealed class V3 : IValidationProfile<V2>;

public record Pet {
    [Required]                                        public string  Name   { get; init; }
    [Required(FromProfile = typeof(V2))]              public string? Tag    { get; init; }
    [Required(UntilProfile = typeof(V2))]             public string? Legacy { get; init; }
    [Pattern("^[A-Z]{3}$", Profiles = new[] { typeof(Strict) })] public string Sku { get; init; }
}
```

Four properties make this work, and each of them is load-bearing:

1. **Unannotated means every profile.** This is what makes profiles opt-in and free. Most rules
   are common to all versions and stay bare. A codebase with no profiles declared generates one
   validator per type and never encounters the concept.

2. **Rule-level attribution dissolves the subtraction problem.** "V2 relaxes this" is expressed by
   *omission* — `FromProfile = typeof(V2)` simply does not admit V1. There is no inherited rule set
   and therefore nothing to subtract, and no need for a `[NotRequired]` counter-attribute. An
   earlier design modelled profiles as inheriting rules with deltas applied; it was worse, and the
   difference is entirely this point.

3. **`FromProfile`/`UntilProfile` walk the chain; `Profiles` handles the rest.** Document standards
   version linearly, so the scalar range form covers the common case and needs no editing when V4
   lands. Orthogonal profiles that are not on the chain — `Strict`, `TenantA`, `Draft` — use the
   explicit set. The `IValidationProfile<TPredecessor>` relationship supplies **ordering only**; it
   does not inherit rules.

4. **Profiles flatten at build time.** Per `(type, profile)` the generator collects every rule whose
   predicate admits that profile and emits one straight-line validator. Having five profiles costs
   exactly as much at runtime as having one. This is the concrete advantage over FluentValidation
   RuleSets, which compare strings per rule per call.

### Profile mechanics the emitter must get right

- **Alias identical rule sets.** 50 types × 4 profiles is 200 validators if you are naive. When the
  rule set for `Pet` under V2 is identical to under V1, emit one validator and have both resolve to
  it. With rule-level attribution this is a straight set comparison.
- **Profile propagates through nesting, automatically.** Validating `Pet` under V2 must validate its
  `Address` under V2, falling back to the aliased validator when V2 adds nothing to `Address`. If
  this is not automatic the feature is unusable.
- **A default profile.** `IValidatorFor<T>` is sugar for the unprofiled validator. Decide whether
  that is a distinct `Default` profile type or an absence, and be consistent.
- **Runtime profile selection needs a generated dispatch table.** Tenant-driven validation picks the
  profile at runtime, and `MakeGenericType(typeof(IValidatorFor<,>), petType, profileType)` is
  exactly what this library exists to avoid. Emit instead:

  ```csharp
  public static class PetValidators {
      public static IValidatorFor<Pet>? For(Type profile) => profile switch {
          _ when profile == typeof(V1) => PetValidator_V1.Instance,
          _ when profile == typeof(V2) => PetValidator_V2.Instance,
          _ => null,
      };
  }
  ```

  Every closed type statically referenced, no reflection, and "which profiles exist for this type"
  becomes answerable at runtime.

### Known gap

Attributes require editing the model, so you cannot attach a new profile's rules to a type from a
package you do not own. The escape hatch is a separate overlay declaration — rules for a type,
declared outside it. **Design it as an escape hatch, not the primary path**; an earlier version of
this design had overlays as the main mechanism and it was significantly worse to use.

Hardened's OpenAPI path never hits this: the generator owns the models and the constraints and
emits both in one pass.

**Syntax check before committing:** attribute arguments need the `new[] { typeof(X) }` form.
Verify whether C# collection expressions are legal in attribute arguments; if not, the scalar
`FromProfile`/`UntilProfile` form carries the common case anyway.

---

## 7. Generator architecture

### 7.1 What gets emitted

```csharp
// <auto-generated/>
public sealed partial class PetValidator_V2 : IValidatorFor<Pet> {
    public static readonly PetValidator_V2 Instance = new();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex SkuPattern();

    public void Validate(ref ValidationContext ctx, Pet value) {
        if (string.IsNullOrWhiteSpace(value.Name))      ctx.Add("required", "name is required.");
        else if (value.Name.Length > 100)               ctx.Add("string_length", "...");

        if (value.Tag is null)                          ctx.Add("required", "tag is required.");

        if (!SkuPattern().IsMatch(value.Sku))           ctx.Add("pattern", "...");

        if (value.Home is { } home) {
            var nested = ctx.Push("home");
            AddressValidator_V2.Instance.Validate(ref nested, home);
        }

        for (var i = 0; i < value.Toys.Count; i++) {
            var item = ctx.PushIndex("toys", i);
            ToyValidator_V1.Instance.Validate(ref item, value.Toys[i]);
        }
    }
}
```

Note the nested validators are referenced as `static Instance` rather than injected. That keeps
generated validators parameterless, which is what makes the factory registration in §7.3 free of
constructor reflection.

### 7.2 Three traps — verified, do not rediscover them

**Generators cannot see each other's output.** Emitting `[SingletonService]` on a generated
validator does nothing, because DM's generator never sees it. This is why generated validators
carry no attributes and why registration is emitted by the same generator that emits the
validators.

**Do not host DependencyModules' attribute stages.** `ValidationModules.SourceGenerator` must not
derive from `BaseSourceGenerator` and `yield return new ServiceSourceGenerator()`. A project
referencing both it and `DependencyModules.SourceGenerator` would then have two generators
processing `[DependencyModule]` and emit the module twice. Use DM's **writers and models as a
library**; do not use its **host**.

**Cross-assembly convention matching is deliberately unavailable.** DM's
`MetadataCandidateUtility` rejects any referenced assembly a convention does not name by name, and
the comment is explicit that "there is no way to ask for all of them, and there should not be one."
So the model is: each assembly emits and registers its own validators, and consumers compose
modules. Do not design around scanning.

### 7.3 Registration — conditional on whether DM is referenced

Probe once:

```csharp
var hasDependencyModules = context.CompilationProvider.Select((c, _) =>
    c.GetTypeByMetadataName("DependencyModules.Runtime.Interfaces.IDependencyModule") is not null);
```

`IncrementalValueProvider<bool>`, so downstream invalidates only when the answer flips.

**When DM is present**, emit a complete module. `IDependencyModule` has exactly one member without
a default implementation, so no DM generator involvement is needed and there is no partial to
complete:

```csharp
public sealed class ValidationModule : IDependencyModule {
    public void PopulateServiceCollection(IServiceCollection services) {
        services.AddSingleton<IValidatorFor<Pet>>(PetValidator_V1.Instance);
        services.AddSingleton<IValidatorFor<Address>>(AddressValidator_V1.Instance);
    }
}
```

`AddModule<T>() where T : IDependencyModule, new()` accepts that directly. Cross-assembly
composition works through the documented `GetModules()` override.

Framework authors compiling Impl in can instead emit through DM's own `DependencyModuleWriter` —
`~/Hardened/Hardened.Framework/src/SourceGenerators/Hardened.DependencyModules.SourceGenerator/HardenedSourceGenerator.cs`
shows the pattern — which inherits registration types, keyed registration and environment
conditions for free.

**When DM is absent**, emit a static table with factories:

```csharp
public static class GeneratedValidators {
    public static IReadOnlyList<ValidatorRegistration> All { get; } = [
        new(typeof(IValidatorFor<Pet>), static _ => PetValidator_V1.Instance),
    ];
}
```

plus a `services.AddValidationModules(GeneratedValidators.All)` extension. Factory delegates
rather than `(Type, Type)` pairs, so nothing goes through `ActivatorUtilities` constructor
reflection.

Both branches share one emitter for the registration body; only the wrapper differs.

Add an MSBuild override — `ValidationModules_Registration=DependencyModules|ServiceCollection|None` —
for the case where DM arrives transitively but the user does not want validators in a module.

### 7.4 The Impl seam for framework authors

Copy the `DependencyModules.SourceGenerator.Impl` packaging exactly: `IsPackable`,
`IncludeBuildOutput=false`, `NoWarn=NU5128`, `**/*.cs` packed as `Content` under
`src/ValidationModules.SourceGenerator.Impl/`, and a `build/*.targets` carrying the switch:

```xml
<ItemGroup Condition="'$(PackageValidationModulesIncludeSource)' == 'true'">
    <Compile Include="$(MSBuildThisFileDirectory)../src/**/*.cs" Visible="false"/>
</ItemGroup>
```

Roslyn dependencies flow deliberately from Impl and are `PrivateAssets=all` on the analyzer package.

**The `[Generator]`-attributed entry point must live in `ValidationModules.SourceGenerator` and
must not be packed into Impl.** That is how DM avoids double registration — its only `[Generator]`
is `DependencyModules.SourceGenerator/SourceGenerator.cs`, which Impl does not ship. Compiling
Impl into your own generator therefore gives you all the machinery and zero entry points.

What Impl must expose to framework authors:

```
ValidatedTypeModel        the IR: properties, constraints, profiles, nesting, element types
ValidatorEmitter          IR  → validator source
RegistrationEmitter       IR  → DM module or static table
AttributeFrontEnd         attributes → IR   (optional; Hardened does not use it)
```

Two front-ends build the IR — attributes, and OpenAPI specs inside Hardened — and both feed one
emitter, so spec-generated and attribute-generated validators produce identical field paths, codes
and error shapes. They never process the same input.

### 7.5 Version lockstep — design for it now

When Hardened compiles Impl into its own generator, the code it emits must match the
`ValidationModules.Runtime` version the *application* references. Build Hardened against Impl 1.2,
have the app reference Runtime 1.1, and the error surfaces inside generated code, which is the
worst possible place for it.

Mitigate with a marker-type probe and a clean diagnostic — "ValidationModules.Runtime 1.2 or later
required, found 1.1" — and keep the emitted surface small and additive-only. This is cheap now and
miserable to retrofit.

**Revised 2026-08-12.** Hardened's spec front-end lands in an MSBuild task rather than a Roslyn
generator (§9), so the check has a better place to live: the task can compare the resolved
`ValidationModules.Runtime` version at MSBuild time and fail with a project file attached. Build
both — the marker-type probe for generator-hosted front-ends, and an MSBuild-time check the task can
call. The second is the one Hardened hits first, and it produces the better error.

---

## 8. FluentValidation adapter and conformance

The adapter exists so an application can keep a rich hand-written `AbstractValidator<T>` for the
handful of types that need `When`/`Must`/cross-field rules, while everything else stays generated
and AOT-clean.

```csharp
public sealed class FluentValidatorAdapter<T> : IAsyncValidatorFor<T> {
    private readonly IEnumerable<FluentValidation.IValidator<T>> _inner;
    // map ValidationFailure -> ValidationError, applying the shared IValidationFieldNamer
}
```

**Register it closed, per type, from the generator** — `AddScoped<IAsyncValidatorFor<Pet>,
FluentValidatorAdapter<Pet>>()` — not as an open generic, so nothing depends on MS.DI's
reflection-based open-generic activation.

**Semantics: all registered validators for a type run and their results merge.** Not
replace-by-precedence. Structural constraints must not silently disappear because someone added a
business rule, and merging removes the precedence question entirely. Run sync structural
validators first and only run async ones if structural validation passed — do not hit the database
to check uniqueness on a field that is null.

**Code mapping is a finite table the adapter owns**: `NotNullValidator` → `required`,
`LengthValidator` → `string_length`, `RegularExpressionValidator` → `pattern`. FluentValidation's
`Severity` has no representation on this side; either add one or document that it is dropped.

**Prove substitutability with a conformance suite.** `~/Hardened` already has the pattern —
`Hardened.Requests.Testing/Conformance/` defines `ExecutionRequestConformanceTests` plus an
`IExecutionRequestConformanceAdapter`, and `AspNetExecutionRequestConformanceTests` runs the shared
suite against a real adapter. Do the same: one `ValidationEngineConformanceTests`, one adapter per
engine. If both pass, substitution is a fact rather than a claim.

FluentValidation is Apache 2.0 and free, with no paid tier. Worth restating because *FluentAssertions*
— different library, similar name — moved to a paid commercial licence in January 2025.

---

## 9. Hardened integration

**Superseded 2026-08-12 by `~/Hardened/VALIDATION-INTEGRATION-PLAN.md`**, which is the executable
spec. The original text is kept below the line because most of it held; the summary here records
what changed and why.

The change is one fact about Impl: `ValidatorEmitter` and `RegistrationEmitter` are `StringBuilder`
over the IR with no `Microsoft.CodeAnalysis` reference — only `FrontEnds/` and `ValidationDiagnostics`
are Roslyn-coupled. So **an MSBuild task can drive the emitter**, and Hardened's spec front-end goes
there rather than into a Roslyn generator. Consequences:

- **`[GeneratedRegex]` becomes available to spec-driven patterns.** A task writes ordinary source
  into `@(Compile)`, where the regex generator sees it. 448 KB → 33 KB on an AOT publish. §18.8's
  inline-pattern problem does not arise for the spec front-end at all.
- **VM0017's per-front-end policy leaves Hardened's critical path.** Still worth doing for this
  library's own consumers; no longer blocking.
- **VM0040 gains a better home** — an MSBuild-time version check (§7.5).
- **No `Hardened.Validation` package.** ValidationModules.Runtime *is* the standalone validation
  family; only the request-pipeline adapter needs a home, and it is `Hardened.Requests.Runtime`.
- **The validated type is an interface per operation**, emitted by the task, implemented by
  Hardened's generated `Parameters` class. The task cannot name `Parameters` — it is nested inside a
  handler class with a computed suffix — so the interface is the seam between the two halves.
- **"Compiles Impl in" survives, for the attribute front-end.** Hand-written controllers and
  `[HardenedFunction]` carry constraints on method parameters, which are in the compilation, so that
  path stays in Hardened's Roslyn generator. Two front-ends, one emitter — exactly §7.4.
- **Auto-attach is the primary path**, with `[Validate]` for tuning and opting out.
- **Failure throws** (§12 Q4), routed through Hardened's `IExceptionToModelConverter`, which also
  handles this library's `ValidationException` so both routes produce one shape.

One correction to the original: attaching through `IRequestFilterProvider` does **not** by itself
give construction-time resolution, because `RequestFilterInfo.FilterFunc` runs per request. But
`GetFilters` itself runs once per handler — the routing table caches handlers with
`??= new Handler(_rootServiceProvider)` — so a provider that builds the filter in `GetFilters` and
returns it by capture is construct-once. That is the shape to use.

---

*Original, 2026-08-06:*

Hardened is the first consumer. `Hardened.Validation` is the only package that lands in that repo:
`ValidationFilter<TBody>` plus a `[Validate]` attribute.

- **`ValidationFilter<TBody>` is generic**, constructed by generated code that knows `TBody`, and
  resolves `IEnumerable<IValidatorFor<TBody>>` **once at handler construction**. That single change
  fixes both defects in §10.2 and §10.3, which are two symptoms of the filter being non-generic.
- **Attachment uses the existing extension point.** An attribute implementing `IRequestFilterProvider`
  is collected out of the handler's static `_metadata` array by `ExecutionHelper.GetFilterInfo`;
  `RetryAttribute` is the working example to copy. Better still, have the web and function
  generators emit the filter automatically when a bound parameter type has constraints, leaving
  `[Validate]` for opting out or tuning.
- **`Hardened.OpenApi.SourceGenerator` compiles Impl in** and emits validators alongside models in
  the pass that already reads the spec. Nothing else opens the yaml. `ValidationFilterEmitter.cs`
  is deleted rather than fixed.
- **Each spec file becomes a profile.** `v1.yaml` and `v2.yaml` produce `V1` and `V2`, and routes
  bind to the right validator with no user action. This also supplies the missing partitioning axis
  for §3.1 of `~/Hardened/OPENAPI-GENERATOR-FINDINGS.md`, where two spec files in one project
  currently cannot compile because `OpenApiJsonTypeInfoResolver` collides in a flat namespace.

---

## 10. What was found in Hardened, and why it shaped this

Verified by reading source and emitted output under `obj/Release/net8.0/generated/`.

**10.1 Validation reaches only OpenAPI handlers.** A workspace-wide grep for `ValidationFilter`,
`IValidationRule` and `ICustomRequestValidator` outside the validation folders returns only
`Hardened.OpenApi.SourceGenerator`. Web controllers, `[HardenedFunction]`, all four Amz Lambda
runtimes, Commands and Canaries have none, and no attribute exists that a developer could write to
get any.

**10.2 Rules are rebuilt on every request, including a compiled Regex.**
`ExecutionChain.cs:27` invokes `RequestFilterInfo.FilterFunc` per request, and the generated code is
`ctx => new ValidationFilter(... new PatternRule("^[a-zA-Z ]+$") ...)`. `PatternRule.cs:12` is
`new Regex(pattern, RegexOptions.Compiled)`, which emits IL through `Reflection.Emit` on every call.
This is why §2 mandates `[GeneratedRegex]` and why rule graphs must be built once.

**10.3 Reflection on the per-request path.** `ValidationFilter.cs:61-69` does `MakeGenericType` +
`GetMethod("ValidateAsync")` + `Invoke` per request — the one place in the pipeline that would break
a Native AOT publish, inside a framework whose entire serializer story exists to avoid reflection.

**10.4 Body validation is one level deep.** The emitter walks top-level properties once and emits
`body => ((CreatePetRequest)body).Name`. Nested objects are never validated even though the spec
declares their fields required, array elements are never validated, and `bodyParameterName` is
hardcoded to `"body"` at `ValidationFilterEmitter.cs:96` and `:125`.

**10.5 `ValidationResult.Success` is a public mutable singleton** (`ValidationResult.cs:4`). Nothing
internal poisons it today, but the natural way to write a custom validator does.

**10.6 Unrelated, found while reading — worth fixing in Hardened.**
`ParametersClassGenerator.WriteItemSetProperty` (`ParametersClassGenerator.cs:91-105`) emits
`caseBlock.Break()` per case followed by an unconditional `Throw`, so the generated
`parameters[0] = x` throws `IndexOutOfRangeException` on a *valid* index, in every generated
handler. The getter is correct because it uses `Return`. `TrySetParameter` works, which is why
nothing has noticed. Not a validation bug; recorded so it is not lost.

---

## 11. Staged plan

**Stage 0 — Hardened, independent of this repo. ~~Do this first.~~ Skipped, 2026-08-12.** Hoist the
generated rule graph into a `static readonly` field so the per-request `new Regex(..., Compiled)`
stops, and replace the `MakeGenericType`/`Invoke` block with a generated typed call. Both are fixes
to `ValidationFilterEmitter.cs`, which Stage 5 deletes. Do them only if Stage 5 will slip past a
release that has to ship.

**Stage 1 — Runtime.** Contracts, `ValidationContext` with lazy path building, `ValidationResult`,
constraint attributes, `IValidationFieldNamer`. No generator yet; hand-write a validator in tests
to pin the semantics in §4.

**Stage 2 — Generator, no profiles.** IR, `ValidatorEmitter`, nesting and collections, both
registration branches, diagnostics for nonsense pairings. Ship this and it is already useful.

**Stage 3 — Profiles.** Attribution, chain ordering, flattening, aliasing, nested propagation, the
runtime dispatch table.

**Stage 4 — Impl packaging.** Extract the source-only package, verify a second generator can compile
it in, add the version-lockstep probe.

**Stage 5 — Hardened.** Restructured 2026-08-12; the detail is
`~/Hardened/VALIDATION-INTEGRATION-PLAN.md` §10. In order: VM0040 as an MSBuild-time check; the
OpenAPI build task (parse first, then the pure emitters); `ValidationFilter<T>` /
`ValidationFilterProvider<T>` / `[Validate]` in `Hardened.Requests.Runtime`; the spec front-end in
the task, deleting `ValidationFilterEmitter` and mapping spec files to profiles; then the attribute
front-end for controllers, `[HardenedFunction]` and the Amz runtimes.

**Stage 6 — FluentValidation adapter and the conformance suite.**

---

## 12. Open questions

1. Overlay declaration syntax for types you do not own (§6, known gap). Needed by Stage 3 at the
   latest; prototype before committing to the attribute shape.
2. Default profile — a distinct `Default` type, or the absence of a profile?
3. Does `Severity` enter the error model, or is dropping it from FluentValidation documented?
4. ~~Should validation failures throw, or write the response directly?~~ **Resolved 2026-08-12 —
   throw.** The filter throws and Hardened's `IExceptionToModelConverter` owns the response. That
   converter also handles this library's `ValidationException`, so a hand-written `ValidateAndThrow`
   and the filter path produce one shape rather than two agreeing by duplication — which is what
   that type's own documentation asks for.
5. Is `[Required]` treating whitespace-only strings as missing the intended policy, and should it
   be opt-out? Still open; decide when Hardened's attribute front-end lands.

---

## 13. Conventions

Same as `~/Hardened`, same author, no reason to diverge. Settled — copy the nearest example rather
than asking.

- **xunit.v3 `3.2.2`** — never xunit 2.x. `Microsoft.NET.Test.Sdk` `17.12.0`,
  `xunit.runner.visualstudio` `3.1.5` with `PrivateAssets=all`.
- **NSubstitute** for mocking. **Built-in xUnit assertions** — no FluentAssertions, no Shouldly.
- **SimpleFixture** where autofixturing helps, class annotated `[SubFixtureInitialize]`.
- Test project `<ProjectUnderTest>.Tests` as a sibling directory, referenced via `ProjectReference`.
- Test naming `Method_Scenario`. File-scoped namespaces. `net8.0`, `ImplicitUsings`, `Nullable`.
- **K&R braces** — opening brace on the same line, including on types. 4-space indent.
- Generator projects target `netstandard2.0`, `LangVersion 10`, `IsRoslynComponent`,
  `EnforceExtendedAnalyzerRules`.
- Add `AnalyzerReleases.Shipped.md` / `Unshipped.md` for the diagnostic IDs from the start; RS2008
  will demand them as soon as the first diagnostic is declared.

Source-generator projects want golden-file tests over emitted output plus at least one project that
*compiles* what was emitted. `~/Hardened/Hardened.Framework/src/SourceGenerators/Hardened.SourceGeneration.Testing`
is the existing harness to copy.
