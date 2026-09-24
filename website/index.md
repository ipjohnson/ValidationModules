---
layout: home

hero:
  name: ValidationModules
  text: Validation for .NET, written at build time
  tagline: Declare rules with attributes or in a rules class. A source generator turns them into a validator class of plain C#, which runs under Native AOT.
  image:
    src: /hero.svg
    alt: A model with constraint attributes beside the validator code generated from it
  actions:
    - theme: brand
      text: Get started
      link: /guide/getting-started
    - theme: alt
      text: How it works
      link: /guide/how-it-works
    - theme: alt
      text: Reference
      link: /reference/attributes
---

## Example

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed class SignUp
{
    [Required, EmailAddress]
    public string? Email { get; init; }

    [Required, StringLength(3, 40)]
    public string? DisplayName { get; init; }

    [Range(18, 120)]
    public int Age { get; init; }
}
```

The generator writes `SignUpValidator` from these attributes. Validating
`new SignUp { Email = "not-an-email", DisplayName = "", Age = 12 }` with it returns three errors:

| `Field` | `Code` | `Message` |
| --- | --- | --- |
| `email` | `email` | email is not a valid email address. |
| `displayName` | `required` | displayName is required. |
| `age` | `range` | age must be between 18 and 120. |

## What it does

- The generator checks your declarations as it reads them. An attribute on the wrong kind of
  member, a regular expression that does not parse, or a range with its bounds reversed is a build
  error.
- Each error has a field path, a stable code, a message and a severity. Code that reacts to
  errors can key on the code, and translations can replace the message.
- The validators contain no reflection, so they work in trimmed and Native AOT applications.
- The attribute names match `System.ComponentModel.DataAnnotations`. Models that already use
  DataAnnotations attributes compile as they are.
- Companion packages validate ASP.NET Core minimal API arguments, check options when the host
  starts, and supply translated messages.

## Packages

| Package | Contents |
| --- | --- |
| [ValidationModules.Runtime](https://www.nuget.org/packages/ValidationModules.Runtime/) | Attributes, `IValidatorFor<T>`, results, and service registration. |
| [ValidationModules.SourceGenerator](https://www.nuget.org/packages/ValidationModules.SourceGenerator/) | The source generator and its analyzers. |
| [ValidationModules.AspNetCore](https://www.nuget.org/packages/ValidationModules.AspNetCore/) | An endpoint filter for minimal APIs and RFC 9457 problem details. |
| [ValidationModules.Options](https://www.nuget.org/packages/ValidationModules.Options/) | Options validation when the host starts. |
| [ValidationModules.Messages](https://www.nuget.org/packages/ValidationModules.Messages/) | Messages in German, Spanish, French, Japanese and Chinese. |
| [ValidationModules.SourceGenerator.Impl](https://www.nuget.org/packages/ValidationModules.SourceGenerator.Impl/) | The generator's source, for authors of other generators. |
