# Registration

The generator writes one extension method per project that registers every validator the project
declares. Call it once when the application starts.

## The registration method

The method is named after the assembly:

| Assembly name | Method |
| --- | --- |
| `Shop` | `AddShopValidators()` |
| `Shop.Api` | `AddShopApiValidators()` |
| `my-app` | `AddMyAppValidators()` |
| `7Eleven` | `Add_7ElevenValidators()` |

The name is split at dots, dashes and underscores, each part starts with a capital letter, and the
separators are removed. A name that starts with a digit gets a leading underscore. The method is
declared in a class named `ShopValidationExtensions`, in the
`Microsoft.Extensions.DependencyInjection` namespace.

For each validated type `T`, the method registers:

| Service | Lifetime | Implementation |
| --- | --- | --- |
| `IValidatorFor<T>` | singleton | the generated validator |
| `ValidationRunner<T>` | scoped | runs every validator registered for `T` |
| `IValidatorFor<List<T>>` and `IValidatorFor<T[]>` | singleton | a `CollectionValidatorFor<T>`, which validates each element, with paths such as `[2].quantity` |
| `ValidationRunner<List<T>>` and `ValidationRunner<T[]>` | scoped | the runners for those collections |

It also registers the language packs the project compiles, the formatter that uses them, an
`IValidationFieldNamer` that matches the project's field naming, and what
[runtime polymorphism](./nesting#subtypes) needs when a property uses it.

The method is not idempotent. Calling it twice registers every validator twice, and each error is
then reported twice. A project with no validated types and no language packs gets no method.

## Resolve a validator

`IValidatorFor<T>` resolves to the last validator registered for `T`. While the generated validator
is the only one, that is enough:

```csharp
var validator = provider.GetRequiredService<IValidatorFor<SignUp>>();
ValidationResult result = validator.Validate(signUp);
```

`ValidationRunner<T>` runs every `IValidatorFor<T>` registered for the type, in registration order,
and merges their errors into one result. `ValidateAsync` also runs the asynchronous validators, as
described in [Async validation](./async). The runner is scoped. Resolve it from a scope, or inject
it into a scoped service such as an ASP.NET Core handler:

```csharp
using var scope = provider.CreateScope();
var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<SignUp>>();

ValidationResult result = runner.Validate(signUp);
```

A runner resolved this way also gives the pass the scope's `IServiceProvider`, which runtime
polymorphism and some rules classes need.

## Hand-written validators

Register a hand-written `IValidatorFor<T>` after the generated ones:

```csharp
services.AddShopValidators();
services.AddSingleton<IValidatorFor<SignUp>, ReservedNameValidator>();
```

`ValidationRunner<SignUp>` now runs the generated validator first and `ReservedNameValidator`
second. A single `IValidatorFor<SignUp>` resolves to `ReservedNameValidator` alone, so validate
through the runner.

A hand-written validator for a nested type also runs when that type is reached through a parent,
provided the parent's validator came from the container.

For a type that has only hand-written validators, register a runner for it yourself:

```csharp
services.AddSingleton<IValidatorFor<Invoice>, InvoiceValidator>();
services.AddValidationRunner<Invoice>();
services.AddCollectionValidatorsFor<Invoice>();
```

`AddCollectionValidatorsFor<T>` is needed only when lists or arrays of the type are validated, for
example as an ASP.NET Core request body. It registers the synchronous and asynchronous collection
validators and their runners. Calling it twice registers them twice. `AddValidationRunner<T>` can be
called more than once, because it adds nothing when a runner is already registered.

## Validators from other assemblies

There is no assembly scanning. Each project that declares validated types gets its own registration
method, and the application calls each one:

```csharp
services.AddShopValidators();
services.AddShopModelsValidators();
```

A class library that declares validated types needs both packages, because the generator runs in
each project separately and does not flow to the projects that reference it.

A nested type declared in another assembly is validated by that assembly's validators, which reach
the parent's validator through the container. Call that assembly's registration method. When it is
not called, a parent created by the container falls back to the generated validator for the nested
type.

A rules class can target a type declared in another assembly. The validator is then generated in
the rules class's project, and that project's registration method registers it.

## DependencyModules

A project that references `DependencyModules.Runtime` gets registration through its module entry
point. The generator adds a partial declaration to every class marked `[DependencyModule]`, and
that partial registers the project's validators with the module:

```csharp
using DependencyModules.Runtime;
using DependencyModules.Runtime.Attributes;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddModule<ApplicationModule>();

[DependencyModule]
public partial class ApplicationModule;
```

The entry point class must be `partial` and must not be nested in another type. Do not also call
`AddShopValidators()`, or every validator is registered twice. Hand-written validators can be
registered with DependencyModules' own service attributes, and they combine with the generated ones
in the same way.

An application built on Hardened works the same way. A class marked `[HardenedModule]` is an entry
point, and the generator registers the project's validators into it.

A project with no entry point, such as a library of models, gets a generated class named
`ValidationModule`. Its namespace is the assembly name, with any character that cannot appear in a
namespace replaced by `_`, so an assembly named `my-models` gets `my_models.ValidationModule`. It
implements `IDependencyModule`, so an application can add it with
`services.AddModule<ValidationModule>()`.

A project that declares more than one entry point registers its validators into each of them, and
the generator reports `VM6001`. Remove the attribute from the classes that are not applications, or
suppress the warning when the project contains two applications on purpose.

::: tip Upgrading from 1.0.0
In 1.0.0 every DependencyModules project got a `ValidationModule` class. From 1.1.0 a project with
an entry point no longer does, because the entry point registers the validators. Remove any
`AddModule<ValidationModule>()` call from such a project.
:::

## Choose the registration form

`ValidationModules_Registration` overrides the automatic choice:

| Value | Effect |
| --- | --- |
| `ServiceCollection` | The registration method only, even when DependencyModules is referenced. |
| `DependencyModules` | The DependencyModules form. |
| `None` | No registration code at all. Construct the validators yourself. |

The values are not case-sensitive. `Auto`, or no value, selects the form automatically. Any other
value does the same, and the generator reports `VM5004`.

## Without a container

Every generated validator has a public parameterless constructor:

```csharp
var validator = new SignUpValidator();
```

A validator created this way uses the generated validators for its nested types, and never runs a
hand-written validator for them. A validator with nested members also has a constructor that takes
the validators for each nested member, named after the member. An empty sequence falls back to the
generated validator:

```csharp
var validator = new OrderValidator(
    shipTo: [new AddressValidator(), new UkPostcodeValidator()],
    lines: []
);
```
