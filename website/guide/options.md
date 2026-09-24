# Options

`ValidationModules.Options` validates an options class when the host starts. An application whose
configuration breaks a rule stops at startup instead of running with bad settings.

```shell
dotnet add package ValidationModules.Options
```

## Validate an options class

Declare the rules on the options class, as on any model:

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed class HubOptions
{
    [Required, StringLength(3, 40)]
    public string? HubName { get; set; }

    [Range(1, 500)]
    public int MaxBatchSize { get; set; }
}
```

Register the validators, then register the options with `AddValidatedOptions`:

```csharp
builder.Services.AddShopValidators();
builder.Services.AddValidatedOptions<HubOptions>().BindConfiguration("Hub");
```

`AddValidatedOptions<T>()` adds the options, connects the registered `IValidatorFor<T>` validators
to the options system, and calls `ValidateOnStart()`. It returns the `OptionsBuilder<T>`, so the
configuration is bound in the same statement with `BindConfiguration`, `Bind` or `Configure`.
`AddValidatedOptions` does not bind anything by itself.

## What a failure looks like

With `HubName` set to `ab` and `MaxBatchSize` set to `9000`, starting the host throws an
`OptionsValidationException` with this message:

```text
hubName [string_length] hubName must be between 3 and 40 characters.; maxBatchSize [range] maxBatchSize must be between 1 and 500.
```

Each error is written as field, code and message. The field is the generator's field name, such as
`hubName`, not the configuration key `Hub:HubName`.

## Details

- Only errors with `Error` severity fail the options. Warnings pass.
- The bridge runs the `IValidatorFor<T>` validators registered for the type, without a service
  provider. It does not run async validators, runtime polymorphism, or rules classes that use an
  interface from another assembly.
- When no validator is registered for the options type, validation fails with a message that names
  the missing registration method. This catches a forgotten `Add...Validators()` call.
- `AddValidatedOptions<T>(name)` registers and validates named options. Each call validates only the
  name it registered.
- The options class needs a public parameterless constructor, as the options system requires.

The package depends on `Microsoft.Extensions.Options` only. `BindConfiguration` comes from
`Microsoft.Extensions.Options.ConfigurationExtensions`, which `Microsoft.Extensions.Hosting` brings
in, so a hosted application already has it.
