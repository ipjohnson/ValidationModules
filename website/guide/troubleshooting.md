# Troubleshooting

## No validator was generated

**The type carries no constraint the generator recognises.** A validator is emitted for a type
because it has at least one constraint, a `[ValidateNested]`, a rule class targeting it, or
`[GenerateValidator]`. Check that the attribute is one of ours and that the namespace is imported:

```csharp
using ValidationModules.Constraints;   // not ValidationModules
```

**The constraint is on a record parameter.** This is the most common cause, and it now reports
[VM1008](/reference/diagnostics#vm1008), so check your warnings before reading further:

```csharp
public sealed record Pet([Required] string Name);              // VM1008
public sealed record Pet([property: Required] string Name);    // works
```

The attribute binds to the constructor parameter, so the property carries no metadata and the type
looks unconstrained to the generator.

**The generator is not running.** Check that the analyzer reference survived:

```xml
<PackageReference Include="ValidationModules.SourceGenerator" Version="…" PrivateAssets="all" />
```

`PrivateAssets="all"` stops it flowing to *your* consumers; it does not stop it running for you. If
you referenced it as a plain `ProjectReference` in this repository, it needs
`OutputItemType="Analyzer"`.

Then look at what was actually produced:

```xml
<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
```

Files appear under `obj/<Configuration>/<tfm>/generated/`. If the directory is empty, the generator
did not run at all; if the files are there, the problem is downstream.

**DataAnnotations are switched off.** If your model's only rules are
`System.ComponentModel.DataAnnotations` attributes and
`ValidationModules_DataAnnotations` is `Ignore`, no validator is emitted. Every skipped constraint
reports [VM2001](/reference/diagnostics#vm2001), so check your warnings.

## The validator exists but a rule never fires

**`[Required]` on a non-nullable value type.** `int Age` is always present, so `[Required]` can never
fail. That is [VM1201](/reference/diagnostics#vm1201), a warning. You probably wanted `[Range]`, or
`int?`.

**A pattern that is not anchored.** `[Pattern("abc")]` matches `"xabcx"`, because patterns follow
JSON Schema and are unanchored. Write `^abc$`.

**A null value.** Every constraint except `[Required]` skips a null: a null string is not too long, a
null collection has no element count. Add `[Required]` if absence should fail too.

**`[ValidateNested]` on a type with no rules.** Nothing was generated for the nested type, so there
is nothing to call. [VM1501](/reference/diagnostics#vm1501) warns about exactly this. If the rules
come from a [rule class](/guide/rule-classes) the warning stays quiet, because the generator knows
about it; mark the type `[GenerateValidator]` if they come from somewhere it cannot see.

## Generated code does not compile

**A `[Range]` bound that does not parse.** String bounds are parsed against the member's type at
build time, and one that does not parse is [VM1103](/reference/diagnostics#vm1103) with the
constraint dropped, so this should no longer reach generated code. If it does, that is a bug worth
reporting.

**A referenced pattern member that is not visible.** The generated validator lands in your assembly,
so a `private` member is out of reach. [VM1107](/reference/diagnostics#vm1107) names the reason.

**Two types with the same name in one assembly.** This is handled. The hint name is qualified by
namespace, so `Api.V1.Customer` and `Api.V2.Customer` coexist. If you see a duplicate-file error, it is a bug
worth reporting.

## `IValidatorFor<T>` does not resolve

**Registration was not called.** Without DependencyModules you need the one call:

```csharp
services.AddMyAppValidators();
```

The method is named after the assembly with the dots removed, so `My.App` emits
`AddMyAppValidators()`. If you would rather read the name than derive it, it is at the bottom of
`obj/Debug/<tfm>/generated/…/GeneratedValidatorRegistration.g.cs`.

**Registration was suppressed.** Check for
`<ValidationModules_Registration>None</ValidationModules_Registration>`.

**The validator is in another assembly.** Each assembly emits and registers its own validators;
there is no cross-assembly scanning, deliberately. Call that assembly's own `Add…Validators()`, or
load its module, from your composition root.

**No validator was generated at all.** See the first section. This is usually that in disguise.

## Every error appears twice

A type has two validators registered. `ValidationRunner<T>` merges every registered
`IValidatorFor<T>` on purpose, so a hand-written validator composes with the generated one.
The usual cause is calling the assembly's `Add…Validators()` twice, or registering by hand a
validator the generated registration already added.

## Errors are in the wrong order

Ordering is: properties in source order, constraints in attribute order, nested objects at the point
of their property, collection elements ascending. Two exceptions by design:

- `[Required]` is evaluated first within a property, whatever order you wrote the attributes in.
- Rules from a [rule class](/guide/rule-classes) report after the attribute-declared checks, in
  body order, because the body is the validator. Two rules classes for one type run in class-name
  order.

An async validator that fans out internally produces its own errors in completion order.

## The field name is wrong

Precedence, highest first: `[JsonPropertyName]`, `[Display(Name = …)]`, then the
[`ValidationModules_FieldNaming`](/reference/msbuild#validationmodules-fieldnaming) property
(camelCase by default).

Field names are **baked in at build time**, so registering a different `IValidationFieldNamer`
does not rename a generated validator's errors. It affects only names computed at run time, from
`IValidatableObject` results and DataAnnotations member names. Set both to the same policy if you
use both.

## The AOT binary grew by half a megabyte

An inline `[Pattern("…")]` roots the regex parser and interpreter. Declare the pattern with
`[GeneratedRegex]` and reference it. See [Patterns and regex](/guide/patterns).

If you did not see [VM1301](/reference/diagnostics#vm1301) warning you, the policy resolved to
`Allow`, which happens when neither `PublishAot` nor `IsAotCompatible` is set on the project holding
the models. Set `IsAotCompatible` there.

## `InvalidOperationException: Validation nested more than 64 levels deep`

Your object graph contains a cycle, or a genuinely very deep tree. The message names the path it
reached.

This is a guard rather than a cycle detector, because tracking visited instances would cost an
allocation and a lookup on every descent. It throws rather than reporting an error because a cycle is a bug in
the graph, not invalid data, and the alternative is a `StackOverflowException`, which cannot be
caught.

If the depth is legitimate, leave the recursive property without `[ValidateNested]` and validate the
levels you care about explicitly. See [Cycles and depth](/guide/nesting#cycles-and-depth).

## A diagnostic is too noisy

Every diagnostic is in category `ValidationModules.Usage`, so you can tune one or all of them from
`.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.VM1201.severity = none
dotnet_analyzer_diagnostic.category-ValidationModules.Usage.severity = suggestion
```

Prefer silencing one id over the whole category. Several of them are errors because the alternative
is generated code that does not compile.

## Every diagnostic in the reference is wired up

There is no longer a "declared but never reported" list. `DiagnosticCatalogueTests` fails in both
directions: a descriptor with no report site fails, and a report site with no test fails. A rule
you expected to catch something either did, or is genuinely not the rule you wanted.
