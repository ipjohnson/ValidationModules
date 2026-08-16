# Deferred features: withdrawn from 1.0.0, and what keeps the door open

Two features were specified, had their declaration surface shipped, and were never built. Both
surfaces were withdrawn before 1.0.0 pinned them. Profiles are the long half of this document;
overlays are a short note at the end.

## Profiles

**Decided:** 2026-08-16, before the 1.0.0 surface was pinned.

Profiles were specified in `IMPLEMENTATION-PLAN.md` Stage 3 and never built, but their *declaration
surface* shipped anyway — `FromProfile`, `UntilProfile` and `Profiles` on every constraint,
`IValidationProfile`, `IValidatorProvider`'s profile members, a `Type? profile` parameter on five
entry points, and `[DefaultValidationProfile]`. Using any of it was `VM0019`, an error, because the
arguments were read by nothing: a rule written to apply only from V2 was enforced under V1 as well.

A 1.0.0 pins the public surface. Pinning members whose only behaviour is a build failure is the
wrong thing to promise, so the surface was withdrawn rather than shipped.

**Profiles are still wanted.** This document exists so that the person who implements them does not
have to work out whether 1.0.0 made it harder — and so that anyone editing the affected types knows
which properties have to be preserved.

## The rule that makes this safe

Every removal below is reversible **additively**: putting it back is a new type, a new member or a
new overload, none of which breaks a caller compiled against 1.0.0. That is the whole argument, and
it holds for all but one item, which is called out.

What was deliberately *not* done: leaving the surface in place as inert. Inert members would have to
be honoured forever, and the natural way to restore behaviour later — adding a `Type? profile = null`
parameter to an existing method — is a **binary** break even though it is source-compatible. Removing
now and adding an overload later avoids that entirely.

## What was removed, and how each comes back

| Removed | Comes back as | Additive? |
|---|---|---|
| `IValidationProfile`, `IValidationProfile<TPredecessor>` | new interfaces | yes |
| `DefaultValidationProfileAttribute` | new attribute | yes |
| `ValidationConstraintAttribute.FromProfile` / `.UntilProfile` / `.Profiles` | new init-only properties | yes |
| `GenerateValidatorAttribute.Profiles` | new init-only property | yes |
| `ValidationOverlayForAttribute<T>.Profiles` | new init-only property | yes |
| `ValidationErrorCollector(Type? profile)`, `.Profile` | new ctor overload + new property | yes |
| `ValidationErrorCollector.CreateSynchronized(Type?)` | new `CreateSynchronized(Type)` overload | yes |
| `ValidationContext.Profile` | new property | yes |
| `ValidatorRegistration.Profile` | new ctor overload + new init property | yes |
| `ValidationRunner<T>.Validate(T, Type?)` | new `Validate(T, Type)` overload | yes |
| `ValidationRunner<T>.ValidateAsync(T, Type?, CancellationToken)` | new `ValidateAsync(T, Type, CancellationToken)` overload | yes |
| `ValidatorForExtensions.Validate` / `.IsValid` / `.ValidateAndThrow` profile parameters | new overloads | yes |
| `IValidatorProvider.GetValidator<T>(Type)`, `.GetProfiles<T>()` | **default interface members** | see below |
| `VM0019` | not restored — see below | n/a |

### The one item that needs a technique

`IValidatorProvider` is **implemented by consumers**, not just called by them. Adding an interface
member in 1.1 would break every implementer compiled against 1.0.0.

Restore its two profile members as **default interface members**:

```csharp
public interface IValidatorProvider {
    IValidatorFor<T>? GetValidator<T>();

    // 1.1: defaults, so an implementer written against 1.0.0 still compiles and still runs.
    IValidatorFor<T>? GetValidator<T>(Type profile) => null;
    IReadOnlyList<Type> GetProfiles<T>() => [];
}
```

`net8.0` is the floor and supports these, so this costs nothing. The interface was kept rather than
deleted because it has a live, profile-free job: `DescribedValidator<T>` resolves nested validators
through `GetValidator<T>()` on the generator-less path.

### VM0019 is retired, not reserved

It existed only to reject profile arguments that nothing read. The properties are gone, so writing
one is now `CS0117` from the compiler, which is a better error than a custom diagnostic.

`VM0011`–`VM0015` and `VM0020` **stay reserved** for profile *semantics* — a profile argument that is
not a profile, a range that admits nothing, a cyclic chain. Do not reuse those ids.

## What 1.0.0 does not decide

Two things a profile implementation will need, neither of which is foreclosed:

**Selecting a validator per (type, profile) at runtime.** Today registration is one
`IValidatorFor<T>` per type. Profiles need several, keyed. `Microsoft.Extensions.DependencyInjection`
8.0+ has keyed services (`AddKeyedSingleton`, `GetKeyedServices`), which is purely additive to what
the generator emits today — the unkeyed registration stays as the default profile and keyed ones sit
beside it.

**One caution, learned the expensive way on this exact surface.** Whatever resolves those keyed
registrations must not let MS.DI satisfy an `IEnumerable<>` through constructor injection.
`CallSiteRuntimeResolver.VisitIEnumerable` builds its array with `Array.CreateInstance(Type, int)`,
and under Native AOT that throws unless the closed array type was named statically somewhere. That
is what broke `ValidationRunner<T>` — see `AddValidationRunner<T>`'s remarks. Resolve through an
explicit factory calling `GetServices<T>()` / `GetKeyedServices<T>()` with the closed type written
out, and add a *minimal* dedicated AOT probe for it: the rich probe in `verify-aot.sh` does not
reproduce this class of fault, because one mention of the interface anywhere in the image roots the
array for every instantiation.

**The wire shape.** `ValidationError` and `ValidationResult` are untouched by profiles, so nothing
about the error model constrains the design.

## The generated surface

`RuntimeContract.Version` was not bumped. The generator never emitted anything profile-related — no
emitter referenced a profile — so no generated code changed and no consumer's emitted output is
invalidated by this removal.

---

## Overlays

`[ValidationOverlayFor<TTarget>]` declared rules for a type you do not own, from outside it. It was
in the runtime's public surface and **read by no front end** — applying it compiled and did nothing
at all, which is the profiles problem without even a diagnostic to say so.

Withdrawn on the same reasoning, and the reversibility argument is simpler: it was one attribute
with one property, and re-adding an attribute is purely additive.

**Rule classes already cover most of what overlays were for.** `IValidationRulesFor<T>` declares
rules for a type from outside it, works today, and is tested — see `website/guide/rule-classes.md`.
What it does not offer is the overlay's per-member mirroring, where the declaration site names the
target's properties and the generator checks each one exists. That check is the part worth building
if overlays return; the declaration surface is not the interesting half.
