# Testing

A generated validator is an ordinary class with a public parameterless constructor. A test can
create one, validate a value, and assert on the errors. The examples use xUnit, and the same
approach works with any test framework.

## Test a model's rules

```csharp
using ValidationModules;
using ValidationModules.Constraints;
using Xunit;

public sealed class SignUpTests
{
    [Fact]
    public void An_underage_sign_up_fails_on_age()
    {
        var result = new SignUpValidator().Validate(
            new SignUp { Email = "ann@example.com", Age = 12 }
        );

        var error = Assert.Single(result.Errors);
        Assert.Equal("age", error.Field);
        Assert.Equal(ValidationCodes.Range, error.Code);
    }
}

public sealed class SignUp
{
    [Required, EmailAddress]
    public string? Email { get; init; }

    [Range(18, 120)]
    public int Age { get; init; }
}
```

Assert on `Field` and `Code`. The message text is for people, and a formatter or a language pack
can replace it, so a test that compares messages breaks for reasons unrelated to the rule.

`ValidationCodes` holds a constant for every built-in code. A code set with `Code =` on an
attribute, or derived by `Ensure` in a rules class, is a plain string. [Validation
codes](../reference/codes) lists the built-in ones.

## Test everything registered for a type

A type can have more than one validator: the generated one and any hand-written ones. Resolving a
single `IValidatorFor<T>` from the container returns only the last one registered. To test what the
application runs, register the validators the way the application does and resolve
`ValidationRunner<T>`, which runs all of them. The runner is registered as scoped, so resolve it
from a scope:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ValidationModules;

var services = new ServiceCollection();
services.AddShopValidators();
services.AddSingleton<IValidatorFor<SignUp>, ReservedNameValidator>();

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<SignUp>>();

var result = runner.Validate(new SignUp { Email = "ann@example.com", DisplayName = "admin" });
```

[Registration](./registration) explains how validators for the same type combine.

## Test a hand-written validator

A hand-written `IValidatorFor<T>` gets the same `Validate` extension method, so it is tested the
same way:

```csharp
using ValidationModules;
using Xunit;

public sealed class ReservedNameValidatorTests
{
    [Fact]
    public void Admin_is_reserved()
    {
        var result = new ReservedNameValidator().Validate(new SignUp { DisplayName = "admin" });

        Assert.Equal("reserved_name", Assert.Single(result.Errors).Code);
    }
}

public sealed class ReservedNameValidator : IValidatorFor<SignUp>
{
    public ValidationFlow Validate(ref ValidationContext context, SignUp value) =>
        value.DisplayName == "admin"
            ? context.Report("displayName", "reserved_name", "That name is reserved.")
            : ValidationFlow.Continue;
}

public sealed class SignUp
{
    public string? DisplayName { get; init; }
}
```

## Debug a rule

The generator reads a rules class and never calls it, so a breakpoint in `Describe` does not hit.
Set breakpoints in the generated validator instead. [How it
works](./how-it-works#viewing-the-generated-code) shows where to find it.
