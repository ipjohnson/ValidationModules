# Patterns and regex

`[Pattern]` has two forms, and on an AOT-published binary the choice between them is worth about
450 KB.

```csharp
// Inline
[Pattern("^[A-Z]{3}$")]
public string? Sku { get; init; }

// Referenced
[Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
public string? Sku { get; init; }
```

Both are correct, and both publish AOT-clean, because neither uses `RegexOptions.Compiled` and
nothing goes through `Reflection.Emit`. What differs is size.

## Why the inline form costs so much

The inline form emits a `Regex` built from a string:

```csharp
private static readonly Regex SkuPattern0 = new Regex("^[A-Z]{3}$");
```

Constructing a `Regex` from a pattern string at run time means the **regex parser and interpreter
have to be in the binary**, because the pattern is not known until the constructor runs. The trimmer
cannot remove them. That is roughly 450 KB on a published AOT binary, paid **once** however many
patterns follow.

The referenced form points at a member you declared:

```csharp
public static partial class PetPatterns {
    [GeneratedRegex("^[A-Z]{3}$")]
    public static partial Regex Sku();
}
```

```csharp
[Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
public string? Sku { get; init; }
```

```csharp
if (value.Sku is not null && !global::MyApp.PetPatterns.Sku().IsMatch(value.Sku))
    ctx.ReportPattern("sku");
```

`[GeneratedRegex]` is the .NET regex source generator: it emits a matcher specialised to that one
pattern, as straight-line C#. No parser, no interpreter, and the matching itself is usually faster
too. About 16 KB for the same pattern.

::: tip Why this library cannot emit `[GeneratedRegex]` for you
It could, except that a source generator cannot see another generator's output. The regex generator
would never see the partial method this one emitted, and the partial would have no implementation.
Declaring the member in your own source is what puts it where the regex generator can find it.
:::

## The policy

Which form is acceptable is governed by `ValidationModules_PatternPolicy`:

| Value | Inline patterns |
|---|---|
| `Allow` | accepted silently |
| `Warn` | [VM1301](/reference/diagnostics#vm1301) as a warning, and still emitted |
| `Error` | VM1301 as an error, and the constraint is dropped |
| *(unset)* | `Error` if the project is AOT-facing, `Allow` otherwise |

```xml
<PropertyGroup>
    <ValidationModules_PatternPolicy>Warn</ValidationModules_PatternPolicy>
</PropertyGroup>
```

"AOT-facing" means `PublishAot` **or** `IsAotCompatible` is true. Both, rather than `PublishAot`
alone, and the distinction matters: `PublishAot` is only ever true in the executable, so a class
library holding your models would never see it. `IsAotCompatible` is what a library sets when it
means to be publishable, and catching that is the difference between the diagnostic landing on the
library's own build and landing on somebody else's publish.

Set `Error` explicitly in a library that ships to AOT consumers, so the failure is yours.

## When the constraint is dropped

Under `Error`, the offending constraint is dropped and the rest of the type is still emitted:

```csharp
public sealed record Pet {
    [Required]
    public string? Name { get; init; }

    [Pattern("^[A-Z]{3}$")]
    public string? Sku { get; init; }
}
```

The build fails with VM1301, and the emitted file contains the `required` check and no regex. That
is deliberate: the build should fail with one useful diagnostic, not also with a second, less useful
error out of a generated file.

## Referencing a member

The member can be a method, a property or a field, and must be:

- **static**, since there is no instance for the validator to reach,
- **parameterless**, if it is a method,
- of type `Regex`,
- at least `internal`, so the generated validator in the same assembly can see it.

Anything else is [VM1107](/reference/diagnostics#vm1107), which names the reason:

```
VM1107: 'MyApp.PetPatterns.Sku' is not static, so the pattern on 'Sku' cannot be emitted
```

A field works if you would rather not write a method:

```csharp
public static partial class PetPatterns {
    [GeneratedRegex("^[A-Z]{3}$")]
    public static partial Regex Sku();

    // or, without the source generator, at the cost of the parser being rooted:
    public static readonly Regex Legacy = new("^[a-z]+$");
}
```

## Matching semantics

**Patterns are unanchored**, following JSON Schema. `[Pattern("abc")]` matches `"xabcx"`. Write
`^…$` if you mean the whole value.

This is the one place the native attribute and the DataAnnotations front end deliberately differ:
`[RegularExpression]` from DataAnnotations *is* anchored, checking that the match starts at 0 and
consumes the whole value. The front end reproduces that faithfully rather than quietly changing the
meaning of a model you moved across.

A null value is not tested. Combine with `[Required]` if absence should also fail.

## `RegexOptions`

```csharp
[Pattern("^[a-z]{3}$", Options = RegexOptions.IgnoreCase)]
```

Honoured, with one exception: `RegexOptions.Compiled` is
[VM1302](/reference/diagnostics#vm1302). It emits IL through `Reflection.Emit`, which is the habit
this library exists to remove. It does nothing here anyway, because patterns go through
`[GeneratedRegex]` or a plain constructor.

For the referenced form, options belong on your `[GeneratedRegex]` declaration instead; the
attribute's `Options` is not consulted when a member is named.

## Invalid patterns

Caught at build time, with the regex engine's own message:

```csharp
[Pattern("[")] // VM1106: The pattern on 'Sku' is not a valid regular expression: …
```

Re-describing the parser's complaint would produce a worse message than the one it already gives.
