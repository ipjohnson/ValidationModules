# MSBuild properties

These properties govern the generator. All go in a `<PropertyGroup>` in the project that holds your
models, rather than in the application, unless those are the same project.

```xml
<PropertyGroup>
    <ValidationModules_Registration>ServiceCollection</ValidationModules_Registration>
    <ValidationModules_FieldNaming>SnakeCase</ValidationModules_FieldNaming>
    <ValidationModules_DataAnnotations>Ignore</ValidationModules_DataAnnotations>
    <ValidationModules_PatternPolicy>Error</ValidationModules_PatternPolicy>
    <ValidationModules_FailFast>Disabled</ValidationModules_FailFast>
    <GeneratedCodeStyle>KAndR</GeneratedCodeStyle>
</PropertyGroup>
```

## `ValidationModules_Registration`

What registration code to emit alongside the validators.

| Value | Effect |
|---|---|
| *(unset)* | auto: a module if `IDependencyModule` resolves, otherwise the extension |
| `DependencyModules` | always emit an `IDependencyModule` |
| `ServiceCollection` | always emit the `Add…Validators()` extension |
| `None` | emit no registration at all |

Auto-detection probes the compilation for
`DependencyModules.Runtime.Interfaces.IDependencyModule`. Set the property when DependencyModules
arrives transitively and you do not want your validators in a module. `None` emits the validators
and leaves the wiring to you.

See [Registration and DI](/guide/registration).

## `ValidationModules_FieldNaming` {#validationmodules-fieldnaming}

How a CLR property name becomes the field name in `ValidationError.Field`.

| Value | `PostalCode` becomes |
|---|---|
| *(unset)* / `CamelCase` | `postalCode` |
| `PascalCase` | `PostalCode` |
| `AsDeclared` | `PostalCode` |
| `SnakeCase` | `postal_code` |

`[JsonPropertyName]` and `[Display(Name = …)]` on the property both take precedence over this.

::: warning This is a build-time decision
Field names are baked into generated validators as string literals, so nothing computes them per
validation. Registering a different `IValidationFieldNamer` in DI does **not** rename a generated
validator's errors. It affects only names computed at run time, from `IValidatableObject` results
and DataAnnotations member names.

If you use both, set this property and the registered namer to the same policy. The built-in
policies are `CamelCaseFieldNamer`, `PascalCaseFieldNamer` and `SnakeCaseFieldNamer` in
`ValidationModules.Naming`, each a `FieldNamer` - the base that also owns how path segments and
indices join. The generated registration TryAdds the namer matching this property, so a namer you
register first wins.
:::

`SnakeCase` handles acronyms: `HTTPStatusLine` becomes `http_status_line`.

## `ValidationModules_FailFast` {#validationmodules-failfast}

Whether a generated validator returns at its first blocking failure.

| Value | Effect |
|---|---|
| *(unset)* / anything else | on: a failing rule returns, and the rules after it never run |
| `Disabled` / `false` | off: every rule is evaluated and the answer discarded |

On by default, because a validator that cannot stop makes
[`ValidationStopMode.StopOnFirstError`](/guide/errors#stopping) a filter rather than an
optimisation, and the person who would have to notice is the one who never asked for the mode.

**Turning it off does not change any result.** The collector closes the pass at its first blocking
failure regardless, so `ValidateFirst` returns the same single error either way; what you lose is
the skipping. That also means an assembly built with it off still composes correctly with one built
with it on.

What it costs to leave on, measured on an `osx-arm64` Native AOT publish: **54 bytes per report
site**, which is 27 KB across 500 sites, 1.1% of that binary and 2.2% of its `__managedcode`
section. Nothing
on the clean path: the return sits inside the failure branch, so a passing validation executes what
it always did.

The reason it needs a build-time switch at all is that it cannot be trimmed.
`ValidationErrorCollector.StopMode` is a runtime field with a public setter, so ILC can never prove
a consumer will not set it, and the branches stay in the binary whether or not anything uses them.

Both spellings are accepted, case-insensitively. Taking only one would let the other pass silently,
which is the failure this property exists to avoid.

## `ValidationModules_CaptureValues` {#validationmodules-capturevalues}

Whether generated report sites pass the failing member as
[`ValidationError.Value`](/guide/messages#the-attempted-value-and-who-may-show-it).

| Value | Effect |
|---|---|
| *(unset)* / anything else | on: the failing value rides on the error, for readers that opt in |
| `Disabled` / `false` | off: the emitter passes nothing, and the capture is absent from the binary |

On by default, and safe by default: the value is a reference to data the application already
holds, and no library surface renders it. Not the default message, not `ToString`, and not
`ValidationException`, not a problem-details body. Only an installed
[formatter](/guide/messages) can choose to show it, which makes that an explicit decision at a
named place.

Turning it off is for builds that must not carry values at all. Because the switch governs
*emission*. Off means the capture argument was never compiled, which is a property of the binary
that can
be audited, which is a stronger guarantee than any runtime flag. The cost of leaving it on is one
boxing allocation per failing value-type member, inside the failure branch; a clean pass executes
what it always did.

Both spellings are accepted, case-insensitively, for the same reason `FailFast` takes both.

## `ValidationModules_DataAnnotations`

Whether `System.ComponentModel.DataAnnotations` attributes are compiled.

| Value | Effect |
|---|---|
| *(unset)* | compiled |
| `Ignore` | skipped, and each skipped constraint reports [VM2001](/reference/diagnostics#vm2001) |

The comparison is case-insensitive, and any value other than `Ignore` means "compile". Turning it
off cannot silently unvalidate a model. A type whose only rules were DataAnnotations gets no
validator, and every constraint reports.

Governs one vocabulary; native constraints are unaffected.

See [DataAnnotations](/guide/data-annotations).

## `ValidationModules_CodeNamespace` {#validationmodules-codenamespace}

Prefixes the error codes this assembly invents. Unset by default.

```xml
<PropertyGroup>
    <ValidationModules_CodeNamespace>myapp</ValidationModules_CodeNamespace>
</PropertyGroup>
```

An `Ensure` then reports `myapp.start_less_than_end`, and a `[Required(Code = "guest_missing")]`
reports `myapp.guest_missing`. The separator is a dot, which survives being a JSON key and a URL
fragment untouched.

::: warning The built-in vocabulary is never prefixed
`required` stays `required`, and so does every other code in
[the fixed vocabulary](/reference/codes). That vocabulary is what lets a client switch on a code
without knowing which engine produced the error, and namespacing it would defeat the point of
having one. What collides when two assemblies merge is the codes people invent, which is exactly
what this covers.
:::

Opt-in because switching it on changes every code the assembly emits, which is a wire-contract
change for anything reading them. It is a once-per-assembly decision, cheapest before you have
consumers.

## `ValidationModules_PatternPolicy`

Whether an inline `[Pattern("…")]` is acceptable.

| Value | Effect |
|---|---|
| *(unset)* | `Error` if the project is AOT-facing, `Allow` otherwise |
| `Allow` | inline patterns accepted silently |
| `Warn` | [VM1301](/reference/diagnostics#vm1301) as a warning, constraint still emitted |
| `Error` | VM1301 as an error, constraint dropped |

"AOT-facing" means `PublishAot` **or** `IsAotCompatible` is `true`. Both, deliberately:
`PublishAot` is only ever true in the executable, so a class library holding your models would never
see it, and the diagnostic would land on somebody else's publish instead of on the library's own
build.

Set `Error` explicitly in a library that ships to AOT consumers.

See [Patterns and regex](/guide/patterns).

## `GeneratedCodeStyle` {#generatedcodestyle}

Which brace style generated files are written in.

| Value | Effect |
|---|---|
| *(unset)* / anything unrecognised | Allman: braces on their own lines |
| `KAndR` / `K&R` (case-insensitive) | the opening brace joins the declaration line |

The name carries no `ValidationModules_` prefix on purpose: the property is shared across source
generators, and DependencyModules reads the same one, so one csproj line styles all of your
generated code. It only moves braces; the code the generator emits is otherwise identical, which
is also why an unrecognised value falls back to Allman silently instead of raising a diagnostic.

## Properties read but not owned

### `PublishAot` / `IsAotCompatible`

Read as AOT signals for the pattern policy above. Setting `IsAotCompatible` on the project that
holds your models is worth doing regardless, because it turns on the trim analyzers for that
project.

### `EmitCompilerGeneratedFiles`

Not read by the generator, but the fastest way to see what it produced:

```xml
<PropertyGroup>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

Files land under `obj/<Configuration>/<tfm>/generated/ValidationModules.SourceGenerator/…`. Add an
explicit `CompilerGeneratedFilesOutputPath` if you would rather they went somewhere else.

Worth enabling in a repository where the emitted code is part of what you review.

## The assembly name

Not a property you set, but it names the registration method: `AddMyAppValidators()` is derived from
`AssemblyName`, because an assembly name is not necessarily a valid identifier. The name splits on
dots and on any character that is not a letter or digit, each segment is PascalCased, and the
segments join with nothing between them.

| `AssemblyName` | Registration method |
|---|---|
| `MyApp` | `AddMyAppValidators()` |
| `My.App` | `AddMyAppValidators()` |
| `My-App` | `AddMyAppValidators()` |
| `app2-signupapi` | `AddApp2SignupapiValidators()` |
| `7Eleven` | `Add_7ElevenValidators()` |
| *(empty)* | `Generated` |

## Diagnostic severity

Not MSBuild, but `.editorconfig`. Silence one by id:

```ini
[*.cs]
dotnet_diagnostic.VM1201.severity = none
```

`<NoWarn>$(NoWarn);VM1201</NoWarn>` and `#pragma warning disable VM1201` work as well.

The category-wide `dotnet_analyzer_diagnostic.category-ValidationModules.Usage.severity` does
**not** reach these, even though they all carry that category:
[the reference explains why](/reference/diagnostics#diagnostics). Several diagnostics are errors
because the alternative is generated code that does not compile.
