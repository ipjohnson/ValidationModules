![ValidationModules](https://raw.githubusercontent.com/ipjohnson/ValidationModules/main/assets/logo-readme.svg)

# ValidationModules

[![NuGet](https://img.shields.io/nuget/v/ValidationModules.Runtime.svg)](https://www.nuget.org/packages/ValidationModules.Runtime/)
[![Build](https://github.com/ipjohnson/ValidationModules/actions/workflows/build-package.yaml/badge.svg)](https://github.com/ipjohnson/ValidationModules/actions/workflows/build-package.yaml)
[![Coverage](https://raw.githubusercontent.com/ipjohnson/ValidationModules/badges/coverage.svg)](https://github.com/ipjohnson/ValidationModules/actions/workflows/build-package.yaml)

ValidationModules writes the validators for your .NET types at build time. You declare rules with
attributes on a model, or in a rules class. A source generator turns them into a validator class
made of ordinary C# checks. The validators read members directly instead of through reflection,
so they work in trimmed and Native AOT applications.

Documentation: https://ipjohnson.github.io/ValidationModules/

## Install

```shell
dotnet add package ValidationModules.Runtime
dotnet add package ValidationModules.SourceGenerator
```

The runtime targets .NET 8 and .NET 10.

## Example

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

The generator writes a class named `SignUpValidator` that checks these rules. It also writes one
extension method that registers every validator in the project. The method is named after the
assembly, so a project named `Shop` gets `AddShopValidators()`.

```csharp
using Microsoft.Extensions.DependencyInjection;
using ValidationModules;

var services = new ServiceCollection();
services.AddShopValidators();

using var provider = services.BuildServiceProvider();
var validator = provider.GetRequiredService<IValidatorFor<SignUp>>();

var result = validator.Validate(
    new SignUp
    {
        Email = "not-an-email",
        DisplayName = "",
        Age = 12,
    }
);

foreach (var error in result.Errors)
{
    Console.WriteLine($"{error.Field}: {error.Code}: {error.Message}");
}
```

```text
email: email: email is not a valid email address.
displayName: required: displayName is required.
age: range: age must be between 18 and 120.
```

Every error has a `Field` path, a `Code`, a `Message`, and a `Severity`. The codes are stable, so
application logic and translations should key on `Code` rather than on the message text.

## Rules that span members

An attribute sees one member. A rules class can compare several:

```csharp
using ValidationModules;

public sealed class BookingRules : IValidationRulesFor<Booking>
{
    public static void Describe(ValidationRules<Booking> rules, Booking x)
    {
        rules.Require(x.Guest).Length(1, 80);
        rules.Range(x.Guests, 1, 8);
        rules.Ensure(x.End > x.Start, message: "The stay must end after it starts.");
    }
}
```

The generator reads `Describe` at build time and writes its rules into `BookingValidator`. The
method itself is never called.

## Compared with DataAnnotations

- Most attributes share their names with `System.ComponentModel.DataAnnotations`, such as
  `[Required]`, `[StringLength]`, `[Range]` and `[EmailAddress]`.
- DataAnnotations finds and runs the attributes with reflection when you validate. Here the checks
  are compiled into the validator.
- An attribute on the wrong kind of member is a build error. `[Range]` on a member whose type has
  no ordering reports `VM1003`.
- A model that already uses DataAnnotations attributes can be compiled as it is.
- `[StringLength]` takes the minimum first. Where DataAnnotations has `[StringLength(50)]`, write
  `[StringLength(max: 50)]`.

## Packages

| Package | Use it for |
| --- | --- |
| `ValidationModules.Runtime` | Attributes, the validator interfaces, results, and service registration. Always required. |
| `ValidationModules.SourceGenerator` | The source generator and its analyzers. Always required. |
| `ValidationModules.AspNetCore` | Validating minimal API arguments and returning RFC 9457 problem details. |
| `ValidationModules.Options` | Validating options classes when the host starts. |
| `ValidationModules.Messages` | Error messages in German, Spanish, French, Japanese and Chinese. |
| `ValidationModules.SourceGenerator.Impl` | The generator's source, for building your own generator on top of it. |

## Documentation

- [Getting started](https://ipjohnson.github.io/ValidationModules/guide/getting-started)
- [Constraint attributes](https://ipjohnson.github.io/ValidationModules/guide/constraints)
- [Rules classes](https://ipjohnson.github.io/ValidationModules/guide/rule-classes)
- [ASP.NET Core](https://ipjohnson.github.io/ValidationModules/guide/aspnetcore)
- [Diagnostics reference](https://ipjohnson.github.io/ValidationModules/reference/diagnostics)

## Contributing

[AGENTS.md](https://github.com/ipjohnson/ValidationModules/blob/main/AGENTS.md) describes how to
build and test the repository, and the conventions the code follows.

## License

MIT. See [LICENSE.txt](https://github.com/ipjohnson/ValidationModules/blob/main/LICENSE.txt).
