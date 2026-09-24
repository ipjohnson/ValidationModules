# Async validation

A rule that needs I/O, such as a database lookup, goes in a class that implements
`IAsyncValidatorFor<T>`. `ValidationRunner<T>.ValidateAsync` runs these validators after the other
validators for the type have passed.

## Write an async validator

```csharp
public interface IAsyncValidatorFor<in T>
{
    ValueTask ValidateAsync(
        ValidationContext context,
        T value,
        CancellationToken cancellationToken = default
    );
}
```

The validator receives the context by value and reports through it. It can take services through
its constructor:

<!-- verify -->
```csharp
using ValidationModules;
using ValidationModules.Constraints;

public sealed class Account
{
    [Required, StringLength(3, 20)]
    public string? Handle { get; init; }
}

public interface IHandleDirectory
{
    ValueTask<bool> IsTakenAsync(string handle, CancellationToken cancellationToken);
}

public sealed class HandleIsFree(IHandleDirectory directory) : IAsyncValidatorFor<Account>
{
    public async ValueTask ValidateAsync(
        ValidationContext context,
        Account value,
        CancellationToken cancellationToken = default
    )
    {
        if (value.Handle is { } handle && await directory.IsTakenAsync(handle, cancellationToken))
        {
            context.Report("handle", "handle_taken", "That handle is already taken.");
        }
    }
}
```

## Register and run it

Register the validator, usually as scoped so that it can use scoped services, and validate through
the runner:

```csharp
services.AddShopValidators();
services.AddScoped<IAsyncValidatorFor<Account>, HandleIsFree>();
```

```csharp
using var scope = provider.CreateScope();
var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<Account>>();

ValidationResult result = await runner.ValidateAsync(account, cancellationToken);
```

| Account | Errors from `ValidateAsync` |
| --- | --- |
| handle `taken` | `handle`: `handle_taken` |
| handle empty | `handle`: `required` |
| handle `fresh` | none |

The empty handle fails the generated `[Required]` check, so the async validator does not run for
it.

## Order

`ValidateAsync` works in two stages:

1. It runs every `IValidatorFor<T>` registered for the type, in registration order.
2. If none of them reported an error with `Error` severity, it awaits every `IAsyncValidatorFor<T>`,
   one at a time, in registration order. Warnings do not stop this stage.

Skipping the second stage avoids a database call for a value that is already known to be invalid.
The errors from both stages end up in one `ValidationResult`.

`runner.Validate(value)` runs only the first stage. It never runs an async validator.

The runner passes the `CancellationToken` to each async validator. It does not check the token
itself.

## Field names

The example reports the field as `handle`, the name the generated checks use. It could also pass
`nameof(Account.Handle)`, which gives `Handle`. When the pass carries a service provider, as it
does through a runner resolved from the container, a field name without a dot or a bracket is
converted to the project's field naming, so `Handle` becomes `handle`. Without a service provider,
as in a unit test, the name stays as written.

## Contexts and concurrency

The context an async validator receives belongs to one validation pass, and it is not safe to use
from several tasks at once. Run lookups concurrently if you need to, then report their failures one
after another once they have finished. To validate a child object, push a context for it and finish
with it before pushing the next. Reporting through a context after a sibling was pushed throws an
`InvalidOperationException`.

## Collections

The generated registration method registers `CollectionAsyncValidatorFor<T>` for `List<T>` and
`T[]`. It runs the async validators for every element of a list, with paths such as
`[2].handle`. As with a single value, the async stage runs only when no element failed the first
stage.

## ASP.NET Core

The endpoint filter from `ValidationModules.AspNetCore` validates through
`ValidationRunner<T>.ValidateAsync`, so async validators run for every request. It passes the
request's cancellation token. See [ASP.NET Core](./aspnetcore).
