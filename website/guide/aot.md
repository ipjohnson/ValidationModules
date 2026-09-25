# Native AOT

Generated validators work in trimmed and Native AOT applications. They read properties directly and
the registration uses closed generic types, so there is nothing for the trimmer to remove and
nothing to compile at run time.

## How this is checked

- `ValidationModules.Runtime`, `ValidationModules.AspNetCore` and `ValidationModules.Options` are
  built with `IsAotCompatible` set. Their builds treat the main trim and AOT analysis warnings as
  errors, so none of their code can use reflection that the trimmer cannot follow. In continuous
  integration every warning is an error.
- Every build of the repository publishes test applications with `PublishAot` and runs them. They
  exercise generated validators, the registration method, runners, list validation, and an
  ASP.NET Core application that uses the endpoint filter and the exception handler. A publish that
  produces any trim or AOT warning fails the build.

## What to do in your application

Most models need nothing. Four areas need attention.

### Regular expressions

Declare patterns with `[GeneratedRegex]` and point `[Pattern]` at them. The inline form,
`[Pattern("...")]`, needs the regular expression parser and interpreter at run time, which adds
about 360 KB to the binary however many inline patterns it has. `Options` or a match timeout on an
inline pattern adds about 490 KB more.

By default, a project that sets `PublishAot` or `IsAotCompatible` to `true` treats an inline pattern
as an error, `VM1301`. The message shows the referenced form to use instead.
`ValidationModules_PatternPolicy` changes this. [Patterns](./patterns) describes both forms and the
policy.

A class library that AOT applications consume should set `IsAotCompatible`. Its inline patterns then
fail in the library's own build rather than in an application's publish.

The DataAnnotations `[RegularExpression]` attribute always compiles to an inline pattern, so the
policy applies to it too. `VM1301` then prints the `[Pattern]` and `[GeneratedRegex]` to use in its
place. The attribute has a 2000 millisecond match timeout unless `MatchTimeoutInMilliseconds` is
`-1`, so by default it adds about 840 KB.

### JSON in ASP.NET Core

A minimal API published with Native AOT needs a `JsonSerializerContext` for its request and
response types. The problem details body that `ValidationModules.AspNetCore` writes carries its own
metadata, so the application's context lists only the application's types. [ASP.NET
Core](./aspnetcore#native-aot) shows the setup.

### DataAnnotations resource messages

A custom `ValidationAttribute` that sets `ErrorMessageResourceType` formats its message through
DataAnnotations, which looks up the resource property with reflection. The trimmer can remove that
property. The generator reports `VM2009`. Set `ErrorMessage` instead, or keep the resource type from
being trimmed. The built-in DataAnnotations attributes read resources without reflection.

### Language packs and invariant globalization

Language packs choose their text by `CultureInfo.CurrentUICulture`. An application published with
`InvariantGlobalization` set to `true` has only the invariant culture, so it cannot switch to
another culture and the packs are never used. Leave globalization on in an application that shows
translated messages.

## Smaller validators

Two properties remove code that a size-sensitive application may not need:

| Property | Removes |
| --- | --- |
| `ValidationModules_FailFast` set to `false` | The early return after each check. A `StopOnFirstError` pass then runs every check, though it still records one error. |
| `ValidationModules_CaptureValues` set to `false` | The capture of the failed value into `ValidationError.Value`. |

The [MSBuild reference](../reference/msbuild) lists every property.
