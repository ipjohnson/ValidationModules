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

The inline form needs the regular expression parser and interpreter at run time. They add about
360 KB to a Native AOT binary, once, however many inline patterns it has. For that reason the
generator treats the inline form according to `ValidationModules_PatternPolicy`. The
DataAnnotations `[RegularExpression]` compiles to the same field, so the policy applies to it as
well:

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
take. Neither has any effect on the referenced form, and setting either there is reported as
`VM1303`. Set them on the `[GeneratedRegex]` attribute instead.

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

When the timeout expires, the value fails the pattern and reports the code `pattern`, as a value
that does not match would. `Validate` and `IsValid` do not throw, so an ASP.NET Core endpoint
answers with a validation response rather than `500`. The same applies to a timeout declared on a
`[GeneratedRegex]`, or set for the whole process with the `REGEX_DEFAULT_MATCH_TIMEOUT` setting.

The timeout is from 1 to 2147483646 milliseconds. Leave it unset, or set it to `-1`, for no timeout.
Any other value is one the `Regex` constructor rejects, so the generator ignores it and reports
`VM1304`. The DataAnnotations `[RegularExpression]` compiles with its own
`MatchTimeoutInMilliseconds`, which is 2000 milliseconds unless it is set. At `-1` it has no
timeout, as in DataAnnotations.

`RegexOptions.Compiled` is removed from an inline pattern and reported as `VM1302`. Compiling the
expression would emit code at run time, so the inline form is always interpreted. Use the
referenced form for a matcher compiled at build time. In a Native AOT binary, an inline pattern
with `Options` or a timeout also keeps code that an inline pattern without them lets the trimmer
remove. That adds about 490 KB more, also once. A `[RegularExpression]` has a timeout unless
`MatchTimeoutInMilliseconds` is `-1`, so by default it adds about 840 KB in all. The referenced
form adds neither, with or without a timeout on its `[GeneratedRegex]`.

## In a rules class

A rules class passes the `[GeneratedRegex]` method as a method group. The generated code calls it
from another class, so the method must be `internal` or `public`. A `private` method is reported as
`VM3004`. A lambda that only calls the method, such as `() => SkuRegex()`, is read as the method:

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

The DataAnnotations `[RegularExpression]` passes an empty string without matching it, as
DataAnnotations does. An HTML form posts an optional field left blank as an empty string, so a
model moved from DataAnnotations keeps accepting it.
