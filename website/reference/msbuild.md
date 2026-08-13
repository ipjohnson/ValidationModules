# MSBuild properties

Five properties govern the generator. All go in a `<PropertyGroup>` in the project that holds your
models — not in the application, unless that is the same project.

```xml
<PropertyGroup>
    <ValidationModules_Registration>ServiceCollection</ValidationModules_Registration>
    <ValidationModules_FieldNaming>SnakeCase</ValidationModules_FieldNaming>
    <ValidationModules_DataAnnotations>Ignore</ValidationModules_DataAnnotations>
    <ValidationModules_PatternPolicy>Error</ValidationModules_PatternPolicy>
</PropertyGroup>
```

## `ValidationModules_Registration`

What registration code to emit alongside the validators.

| Value | Effect |
|---|---|
| *(unset)* | auto — a module if `IDependencyModule` resolves, otherwise the static table |
| `DependencyModules` | always emit an `IDependencyModule` |
| `ServiceCollection` | always emit `GeneratedValidators.All` |
| `None` | emit no registration at all |

Auto-detection probes the compilation for
`DependencyModules.Runtime.Interfaces.IDependencyModule`. Set the property when DependencyModules
arrives transitively and you do not want your validators in a module — `None` emits the validators
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
Field names are baked into generated validators as string literals — nothing computes them per
validation. Registering a different `IValidationFieldNamer` in DI does **not** rename a generated
validator's errors; it only affects `DescribedValidator<T>` and the FluentValidation adapter.

If you use both engines, set this property and the registered namer to the same policy.
:::

`SnakeCase` handles acronyms: `HTTPStatusLine` becomes `http_status_line`.

## `ValidationModules_DataAnnotations`

Whether `System.ComponentModel.DataAnnotations` attributes are compiled.

| Value | Effect |
|---|---|
| *(unset)* | compiled |
| `Ignore` | skipped, and each skipped constraint reports [VM0010](/reference/diagnostics#vm0010) |

The comparison is case-insensitive, and any value other than `Ignore` means "compile". Turning it
off cannot silently unvalidate a model — a type whose only rules were DataAnnotations gets no
validator, and every constraint reports.

Governs one vocabulary; native constraints are unaffected.

See [DataAnnotations](/guide/data-annotations).

## `ValidationModules_PatternPolicy`

Whether an inline `[Pattern("…")]` is acceptable.

| Value | Effect |
|---|---|
| *(unset)* | `Error` if the project is AOT-facing, `Allow` otherwise |
| `Allow` | inline patterns accepted silently |
| `Warn` | [VM0017](/reference/diagnostics#vm0017) as a warning, constraint still emitted |
| `Error` | VM0017 as an error, constraint dropped |

"AOT-facing" means `PublishAot` **or** `IsAotCompatible` is `true`. Both, deliberately:
`PublishAot` is only ever true in the executable, so a class library holding your models would never
see it, and the diagnostic would land on somebody else's publish instead of on the library's own
build.

Set `Error` explicitly in a library that ships to AOT consumers.

See [Patterns and regex](/guide/patterns).

## Properties read but not owned

### `PublishAot` / `IsAotCompatible`

Read as AOT signals for the pattern policy above. Setting `IsAotCompatible` on the project that
holds your models is worth doing regardless — it turns on the trim analyzers for that project.

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

Not a property you set, but it decides where `GeneratedValidators` lands: the namespace is derived
from `AssemblyName`, sanitized, because an assembly name is not necessarily a valid namespace.

| `AssemblyName` | Namespace |
|---|---|
| `MyApp` | `MyApp` |
| `My-App` | `My_App` |
| `7Eleven` | `_7Eleven` |
| `My..App` | `My.App` |
| *(empty)* | `Generated` |

## Diagnostic severity

Not MSBuild — `.editorconfig`. Every diagnostic is in category `ValidationModules.Usage`:

```ini
[*.cs]
dotnet_diagnostic.VM0004.severity = none
dotnet_analyzer_diagnostic.category-ValidationModules.Usage.severity = suggestion
```

Prefer silencing one id over the whole category. Several diagnostics are errors because the
alternative is generated code that does not compile.
