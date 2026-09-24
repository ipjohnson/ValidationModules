# Patterns

`[Pattern]` checks a string against a regular expression. It has two forms. The referenced form
points at a `[GeneratedRegex]` method that you declare. The inline form takes the expression as a
string. Prefer the referenced form. It works everywhere and adds almost nothing to a Native AOT
binary.

## The referenced form

Declare the expression with `[GeneratedRegex]`, then name the class and the method:

```csharp
using System.Text.RegularExpressions;
using ValidationModules.Constraints;

public sealed class Product
{
    [Pattern(typeof(ProductPatterns), nameof(ProductPatterns.Sku))]
    public string? Sku { get; init; }
}

public static partial class ProductPatterns
{
    public static readonly Regex Sku = SkuRegex();

    [GeneratedRegex("^[A-Z]{3}-[0-9]{4}$")]
    private static partial Regex SkuRegex();
}
```

The member must be static, accessible from the model, and of type `Regex`. It can be a method, a
property or a field. When it is a method, the generator calls it. The .NET regular expression
source generator writes the matching code, so nothing parses the expression at run time.

A member that is missing, not static, not accessible or not a `Regex` is reported as `VM1107`.

A failed match reports the code `pattern` with the message `sku is not in the required format.`

## The inline form

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed class Product
{
    [Pattern("^[A-Z]{3}-[0-9]{4}$")]
    public string? Sku { get; init; }
}
```

The generator stores a `new Regex(...)` in a static field of the validator, so the expression is
parsed once, when the validator type is first used. An expression that does not parse is reported
as `VM1106` at build time.

The inline form needs the regular expression parser and interpreter at run time, and they add to
the size of a Native AOT binary. For that reason the generator treats the inline form according to
`ValidationModules_PatternPolicy`:

| Policy | Effect on an inline pattern |
| --- | --- |
| `Auto` (default) | `VM1301` error when the project sets `PublishAot` or `IsAotCompatible` to `true`. Allowed otherwise. |
| `Error` | `VM1301` error in every project. |
| `Warn` | `VM1301` warning, and the pattern is compiled. |
| `Allow` | No diagnostic. |

Set the policy in the project file:

```xml
<PropertyGroup>
  <ValidationModules_PatternPolicy>Allow</ValidationModules_PatternPolicy>
</PropertyGroup>
```

A class library that ships to AOT applications can set `Error` so that the failure appears in its
own build rather than in an application's publish.

## Options and timeouts

`Options` passes `RegexOptions` to the inline form, for example `RegexOptions.IgnoreCase`.
`MatchTimeoutMilliseconds` sets a match timeout, which limits the time an expensive input can
take. Neither has any effect on the referenced form. Set them on the `[GeneratedRegex]` attribute
instead.

<!-- verify -->
```csharp
using System.Text.RegularExpressions;
using ValidationModules.Constraints;

public sealed class Account
{
    [Pattern(
        "^[a-z0-9_]{3,20}$",
        Options = RegexOptions.IgnoreCase,
        MatchTimeoutMilliseconds = 100
    )]
    public string? UserName { get; init; }
}
```

`RegexOptions.Compiled` is reported as `VM1302`. Leave it out.

## In a rules class

A rules class passes the regular expression as a method group, not as a lambda:

```csharp
using System.Text.RegularExpressions;
using ValidationModules;

public sealed class Product
{
    public string? Sku { get; init; }
}

public sealed partial class ProductRules : IValidationRulesFor<Product>
{
    public static void Describe(ValidationRules<Product> rules, Product x)
    {
        rules.Pattern(x.Sku, SkuRegex);
    }

    [GeneratedRegex("^[A-Z]{3}-[0-9]{4}$")]
    internal static partial Regex SkuRegex();
}
```

The method must be `internal` or `public`. The generator copies the rule into a separate generated
class, which cannot call a `private` method.

## Null and empty values

`[Pattern]` passes a `null` value, like every constraint except `[Required]`. An empty string is
matched against the expression like any other string. Add `[Required]` when the value must be
present.
