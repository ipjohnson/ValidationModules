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
    [Required, StringLength(40, Min = 3)]
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
- The options validator that `AddValidatedOptions` registers runs the `IValidatorFor<T>` validators
  registered for the type, without a service provider. It does not run async validators. An options
  class that uses `Polymorphism.Runtime`, or a rules class over an interface from another assembly,
  makes startup fail with an `InvalidOperationException`, because those need a service provider.
- When no validator is registered for the options type, validation fails with a message that
  begins `No IValidatorFor<HubOptions> is registered.` and tells you to call the generated
  `Add<Assembly>Validators()` method. This catches a forgotten registration call.
- `AddValidatedOptions<T>(name)` registers and validates named options. Each call validates only the
  name it registered. Calling it twice for the same name validates twice, and each failure is listed
  twice.
- The options class needs a public parameterless constructor, as the options system requires.

The package depends on `ValidationModules.Runtime` and `Microsoft.Extensions.Options`.
`BindConfiguration` comes from `Microsoft.Extensions.Options.ConfigurationExtensions`, which
`Microsoft.Extensions.Hosting` brings in, so a hosted application already has it.
