# Getting started

This page takes one model from no validation to a checked result. It uses a console application
named `Shop`. The steps are the same in any project that targets .NET 8 or later.

## Install the packages

```shell
dotnet add package ValidationModules.Runtime
dotnet add package ValidationModules.SourceGenerator
```

`ValidationModules.Runtime` contains the attributes and the types your code calls. Its only
dependency is `Microsoft.Extensions.DependencyInjection.Abstractions`.
`ValidationModules.SourceGenerator` contains the source generator. It is a development dependency,
so it does not flow to projects that reference yours. Add it to every project that declares
validated types.

This example builds its own service container, so a console application also needs the container
package. ASP.NET Core and other hosted applications already have it.

```shell
dotnet add package Microsoft.Extensions.DependencyInjection
```

## Declare the rules

Put constraint attributes on the members you want checked:

<!-- verify -->
```csharp
using ValidationModules.Constraints;

namespace Shop;

public sealed class SignUp
{
    [Required, EmailAddress]
    public string? Email { get; init; }

    [Required, StringLength(40, Min = 3)]
    public string? DisplayName { get; init; }

    [Range(18, 120)]
    public int Age { get; init; }
}
```

The attributes are in the `ValidationModules.Constraints` namespace. Build the project. The
generator writes a class named `SignUpValidator` in the `Shop` namespace, which implements
`IValidatorFor<SignUp>`.

[Constraint attributes](./constraints) lists every attribute. [Rules classes](./rule-classes)
covers rules that attributes cannot express, such as a comparison between two members.

## Register the validators

The generator also writes one extension method on `IServiceCollection` that registers every
validator in the project. The method is named after the assembly. For an assembly named `Shop`, it
is `AddShopValidators()`:

```csharp
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddShopValidators();
```

The method is in the `Microsoft.Extensions.DependencyInjection` namespace, so it appears next to
the other `Add` methods. Call it once. [Registration](./registration) describes what it registers.

## Validate

Resolve `IValidatorFor<SignUp>` and call `Validate`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Shop;
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

if (!result.IsValid)
{
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"{error.Field}: {error.Code}: {error.Message}");
    }
}
```

`Validate` is an extension method in the `ValidationModules` namespace. Without
`using ValidationModules;`, the call fails with error `CS7036`, because the compiler finds only the
validator's own `Validate` method, which takes a context. The program prints:

```text
email: email: email is not a valid email address.
displayName: required: displayName is required.
age: range: age must be between 18 and 120.
```

`DisplayName` has two constraints, and only `[Required]` reported. When `[Required]` fails, the
other constraints on that member are skipped.

`IValidatorFor<SignUp>` resolves to a single validator. That is enough while the generated
validator is the only one for the type. Once you add a hand-written validator for the same type,
resolve `ValidationRunner<SignUp>` instead, which runs all of them. [Registration](./registration)
explains why.

## Read the result

`Validate` returns a `ValidationResult`. `Errors` lists every failure in the order the checks ran.
`IsValid` is `false` when at least one of them has `Error` severity. Warnings do not change it.
Each `ValidationError` has four parts:

| Property | Example | Use |
| --- | --- | --- |
| `Field` | `displayName` | The path to the member, in camelCase by default. |
| `Code` | `required` | A stable identifier for the kind of failure. |
| `Message` | `displayName is required.` | Text for a person to read. |
| `Severity` | `Error` | `Error`, `Warning` or `Info`. |

Write application logic and translations against `Code`. The message text can change between
versions, and it can be replaced by a translation. [Results and errors](./errors) covers the
result in detail.

## Validate without a container

The generated validator has a public parameterless constructor, so code without a service
container can create it directly:

<!-- verify -->
```csharp
using ValidationModules;
using ValidationModules.Constraints;

var validator = new SignUpValidator();
bool ok = validator.IsValid(new SignUp { Email = "ann@example.com", Age = 30 });

public sealed class SignUp
{
    [Required, EmailAddress]
    public string? Email { get; init; }

    [Range(18, 120)]
    public int Age { get; init; }
}
```

`IsValid` runs the same checks as `Validate` and stops at the first failure. Use it when only the
answer matters.

A validator created with `new` uses the generated validators for its nested members. A validator
resolved from the container uses every `IValidatorFor<T>` registered for them, including
hand-written ones. [Registration](./registration) explains how the two combine.

## Next steps

- [How it works](./how-it-works) shows the code the generator writes.
- [ASP.NET Core](./aspnetcore) validates minimal API arguments before the handler runs.
- [Diagnostics](../reference/diagnostics) lists the build errors and warnings the generator reports.
