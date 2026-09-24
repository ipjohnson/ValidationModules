# MSBuild properties

These properties change what the generator writes. Set them in the project file of the project that
declares the validated types:

```xml
<PropertyGroup>
  <ValidationModules_FieldNaming>SnakeCase</ValidationModules_FieldNaming>
  <ValidationModules_CodeNamespace>shop</ValidationModules_CodeNamespace>
</PropertyGroup>
```

The `ValidationModules.SourceGenerator` package declares each property as a
`CompilerVisibleProperty`, which is what makes it visible to the generator. A project that
references the generator with a `ProjectReference` instead of the package must declare them itself.

Each `ValidationModules_*` property except `ValidationModules_CodeNamespace` takes one of a fixed
set of values, and the values are not case-sensitive. A value that a property does not accept means
the property's default, and the generator reports `VM5004` with the values it accepts.

## `ValidationModules_FieldNaming`

This property sets how member names become field names in errors.

| Value | `PostalCode` becomes |
| --- | --- |
| not set, or `CamelCase` | `postalCode` |
| `SnakeCase` | `postal_code` |
| `PascalCase` or `AsDeclared` | `PostalCode` |

`[JsonPropertyName]` and `[Display(Name)]` on a property take precedence. The registration method
also registers the matching `IValidationFieldNamer`: `CamelCaseFieldNamer`, `SnakeCaseFieldNamer`
or `PascalCaseFieldNamer`. A namer you register first takes precedence. Custom namers derive from
`FieldNamer`.

## `ValidationModules_CodeNamespace`

This property sets a prefix for the codes you set with `Code` on a built-in attribute or a
`CustomConstraintAttribute`, and for the codes of `Ensure`, set with `code:` or derived. With
`shop`, the code `stay_order` becomes `shop.stay_order`. Codes passed to `Report` calls and helpers
are not prefixed, and built-in codes are never prefixed. It is not set by default.

## `ValidationModules_PatternPolicy`

This property sets what the generator does with an inline `[Pattern("...")]` and with a
DataAnnotations `[RegularExpression]`, which compiles to the same thing.

| Value | Effect |
| --- | --- |
| not set, or `Auto` | `VM1301` error when `PublishAot` or `IsAotCompatible` is `true`. Allowed otherwise. |
| `Error` | `VM1301` error. |
| `Warn` | `VM1301` warning, and the pattern is compiled. |
| `Allow` | Allowed. |

See [Patterns](../guide/patterns).

## `ValidationModules_DataAnnotations`

Set to `Ignore` to stop the generator compiling `System.ComponentModel.DataAnnotations` attributes.
It then reports `VM2001` for each one. The default, `Compile`, compiles them. See
[DataAnnotations](../guide/data-annotations).

## `ValidationModules_FailFast`

Set to `false` or `Disabled` to leave out the early return after each check. The validators get
smaller. A `StopOnFirstError` pass still records only one error but runs every check. It is on by
default, and `true` or `Enabled` turns it on explicitly.

## `ValidationModules_CaptureValues`

Set to `false` or `Disabled` to stop recording the failed value in `ValidationError.Value`. It is
on by default, and `true` or `Enabled` turns it on explicitly.

## `ValidationModules_Registration`

This property selects the registration code the generator writes.

| Value | Effect |
| --- | --- |
| not set, or `Auto` | `DependencyModules` when the project references DependencyModules, `ServiceCollection` otherwise. |
| `ServiceCollection` | The `Add<Assembly>Validators()` extension method only. |
| `DependencyModules` | The extension method, registration into each module entry point, and a `ValidationModule` class when there is no entry point. |
| `None` | No registration code. |

See [Registration](../guide/registration).

## Other properties

| Property | Package | Effect |
| --- | --- | --- |
| `ValidationModulesLanguages` | `ValidationModules.Messages` | Which built-in languages to compile: a list such as `fr;de`, `all` (the default), or `none`. |
| `GeneratedCodeStyle` | `ValidationModules.SourceGenerator` | `KAndR` or `K&R`, in any case, for K&R braces in the generated code. Allman braces otherwise. DependencyModules reads the same property. |
| `PublishAot`, `IsAotCompatible` | .NET SDK | When either is `true`, the default pattern policy rejects inline patterns. |
| `EmitCompilerGeneratedFiles` | .NET SDK | Writes the generated files under `obj/` so that you can read them. |
| `PackageValidationModulesIncludeSource` | `ValidationModules.SourceGenerator.Impl` | See below. |

## Building your own generator

`ValidationModules.SourceGenerator.Impl` contains the generator's source code, for authors who want
to drive the same front ends and emitters from a generator of their own. Setting
`PackageValidationModulesIncludeSource` to `true` compiles that source into the project that
references the package.

The package contains no `[Generator]` class, so it never runs by itself. Your generator provides the
entry point, reads its own options, and reports its own errors. The main types are
`AttributeFrontEnd` and `RulesFrontEnd`, which read declarations, and `ValidatorEmitter`,
`RegistrationEmitter` and `LanguagePackEmitter`, which write code. Do not run
`ValidationModules.SourceGenerator` over the same types as well, or each type gets two validators.

`EmitterContract.Probe(compilation)` checks that the referenced runtime is new enough and returns
the diagnostic to report when it is not. The runtime package also publishes the version of its
contract as the MSBuild property `ValidationModulesRuntimeContract`, for hosts that check
compatibility without a compilation. A generator that builds its own table of registrations can pass
it to `services.AddValidationModules(registrations)`.
