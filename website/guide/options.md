# Options validation

```shell
dotnet add package ValidationModules.Options
```

Configuration deserves the same rules a request body gets, checked before the host serves
anything. `AddValidatedOptions<T>()` wires the generated validators into the options pipeline:

```csharp
builder.Services.AddMyAppValidators();
builder.Services.AddValidatedOptions<HubOptions>().BindConfiguration("Hub");
```

```csharp
public sealed class HubOptions {
    [Required]
    [StringLength(min: 3, max: 40)]
    public string? HubName { get; set; }

    [Range(1, 500)]
    public int MaxBatchSize { get; set; }
}
```

With a bad `appsettings.json`, the host refuses to start:

```
Microsoft.Extensions.Options.OptionsValidationException:
  hubName [string_length] hubName must be between 3 and 40 characters.
  maxBatchSize [range] maxBatchSize must be between 1 and 500.
```

## What it is made of

`AddValidatedOptions<T>()` is `AddOptions<T>()` plus two things: an `IValidateOptions<T>` that
delegates to every registered `IValidatorFor<T>` and renders failures as `field [code] message`,
and `ValidateOnStart()`, which makes the host run the validation when it starts rather than on
the first `IOptions<T>.Value` read. It returns the `OptionsBuilder<T>`, so binding chains as
usual.

This is the ValidationModules counterpart to .NET 8's `[OptionsValidator]`: one set of
constraints on the model validates the configuration section, the request body, and anything
else that hands the type to a validator - rather than one vocabulary for options and another for
everything else.

## The edges

- **Structural rules only.** An [`IAsyncValidatorFor<T>`](/guide/async) needs I/O and a scope,
  and `IValidateOptions<T>` offers neither. Configuration validation is the structural kind.
- **Named options validate per registration.** `AddValidatedOptions<HubOptions>("secondary")`
  judges the `secondary` instance and leaves others alone, the same shape
  `ValidateDataAnnotations()` has.
- **No registered validator fails validation outright.** Asking for validated options and
  consulting nothing would be a silent no-op, so the failure message names the generated
  `Add<Assembly>Validators()` call that is missing.
- **Not idempotent**, like every registration here: calling it twice for one name registers the
  bridge twice and each error reports twice.
