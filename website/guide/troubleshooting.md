# Troubleshooting

This page lists problems that come up in practice, with their causes. Build errors and warnings with
an id of the form `VM####` are covered in the [diagnostics reference](../reference/diagnostics).

## The registration method does not exist

`services.AddShopValidators()` does not compile:

- The project does not reference `ValidationModules.SourceGenerator`. The generator package does not
  flow from a referenced project, so every project that declares validated types needs it.
- The name is different. It comes from the assembly name, so check `AssemblyName` in the project
  file. [Registration](./registration#the-registration-method) gives the rules.
- The project declares no validated types, so there is nothing to register.
- The generator is referenced as a project rather than a package. A `ProjectReference` to the
  generator needs `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`.

## No service for `IValidatorFor<T>`

- The registration method for the assembly that declares `T` was not called. Each project has its
  own method.
- `T` has no rules, so no validator was generated. Add constraints, a rules class, or
  `[GenerateValidator]`.
- `ValidationModules_DataAnnotations` is set to `Ignore`, and `T` has only DataAnnotations
  attributes, so no validator was generated for it.

## Error CS7036 on Validate

`new SignUpValidator().Validate(signUp)` fails with error `CS7036`, which says no argument was given
for the parameter `value` of `SignUpValidator.Validate(ref ValidationContext, SignUp)`. The file
does not import the `ValidationModules` namespace. `Validate(value)` is an extension method in that
namespace. Without it, the compiler finds only the validator's own `Validate` method, which takes a
context. Add `using ValidationModules;`.

## A rule is not checked

- The value is `null`. Every constraint except `[Required]` passes `null`.
- The attribute is on a field or a static property. The generator reads instance properties only,
  and reports `VM1011`.
- The attribute is on a positional record parameter without the `property:` target. The generator
  reports `VM1008`.
- A derived class hides the property with `new`, which replaces the base property's constraints.
  The generator reports `VM1009`.
- The nested object has no `[ValidateNested]`, or the nested type has no rules (`VM1501`), or it is
  declared in another assembly and this project can reach no validator for it (`VM1505`).
- A `When` or `Unless` condition excluded it.
- The generator reported an error or warning for the constraint and dropped it. Check the build
  output for `VM` diagnostics.

## Error CS1729 or CS1739 on [StringLength]

`[StringLength(3, 40)]` fails with error `CS1729`, and `[StringLength(min: 3)]` with error `CS1739`.
`[StringLength]` reads its arguments as the DataAnnotations attribute of the same name does: the one
positional argument is the maximum, and the minimum is the named `Min`. Write
`[StringLength(40, Min = 3)]` or `[StringLength(Min = 3)]`.

Earlier versions took the minimum first. A one-argument `[StringLength(50)]` written against them
meant at least 50 characters, and it now means at most 50, so search for that form when upgrading.

## [Pattern] accepts values it should not

`[Pattern]` passes when the expression matches anywhere in the value. Anchor it with `^` and `$` to
match the whole value.

## Every error appears twice

The validators are registered twice. Either the registration method is called twice, or a
DependencyModules application calls it as well as adding the module that already registers the
validators. Call it once.

## A hand-written validator does not run

- The code resolves a single `IValidatorFor<T>`, which returns only the last validator registered.
  Resolve `ValidationRunner<T>` instead.
- The validator was created with `new`. A validator created that way does not use the container, so
  it never runs hand-written validators for nested types.
- It is an `IAsyncValidatorFor<T>`, and the code calls `Validate` rather than `ValidateAsync`, or an
  earlier error stopped the async stage. See [Async validation](./async#order).

## Cannot resolve scoped service ValidationRunner

`ValidationRunner<T>` is registered as scoped. Resolve it from a scope created with
`provider.CreateScope()`, or inject it into a scoped service or a request handler.

## The validation pass carries no services

An `InvalidOperationException` that says the validation pass carries no services comes from a
property with `[ValidateNested(Polymorphism.Runtime)]`. The validators for the value's actual type
are looked up in the container during validation, and `validator.Validate(value)` has no container.
Validate through `ValidationRunner<T>` resolved from a scope, or pass the provider to a
`ValidationErrorCollector`.

## No IValidatorFor is registered, compose the validators from another assembly

A rules class that calls `rules.As<TFacet>(x)` with an interface from another assembly throws an
`InvalidOperationException` that names that assembly and its registration method. It has two causes.
Either that registration method was not called, or the pass has no service provider, as with
`validator.Validate(value)`, even though the method was called. Call the method, and validate
through `ValidationRunner<T>` resolved from a scope.

## Validation nested more than 64 levels deep

The object being validated contains a cycle, such as an object that is its own child, or it is
nested more than 64 levels deep. The limit cannot be raised.

## An endpoint fails on the first request

`.Validate<T>()` checks its setup when the endpoints are built, which happens on the first request.
It throws when the handler has no parameter of type `T`, or when no validator is registered for `T`.
Endpoints are built together, so every endpoint fails. See [ASP.NET
Core](./aspnetcore#what-the-filter-does).

## Messages are always in English

`ValidationError.Message` always returns the default English text. Format the error with
`error.ToMessage(formatter)`, where the formatter is the `ValidationMessageFormatter` registered for
your language packs. Also check that `CultureInfo.CurrentUICulture` is set, and that the application
does not use invariant globalization. See [Messages and languages](./messages).

## Field names do not match the JSON

Field names are camelCase by default and ignore the application's JSON options. Put
`[JsonPropertyName]` on the property, or set `ValidationModules_FieldNaming`. See
[Field names](./errors#field-names).

## A language pack has no effect

- The file name must end in `.validation-messages.json`.
- `CultureInfo.CurrentUICulture` must be the pack's culture or one of its child cultures, and the
  application must not use invariant globalization.
- The message must come through a formatter, with `error.ToMessage(formatter)`, or through the
  ASP.NET Core problem details response. `error.Message` stays in English.
- Authored messages are not replaced. See [Authored messages](./messages#authored-messages).
- The generator is referenced as a project rather than a package, so the package's build targets
  that pick up pack files are not imported. List the files yourself:

  ```xml
  <ItemGroup>
    <AdditionalFiles Include="Messages/*.validation-messages.json" />
  </ItemGroup>
  ```

## An MSBuild property has no effect

- The value is not one the property accepts. The generator then uses the default and reports
  `VM5004`, which lists the values the property accepts. Case does not matter.
- The generator is referenced as a project rather than a package. The package's build targets
  declare which properties the generator can read, and a `ProjectReference` does not import them.
  Declare the property yourself:

  ```xml
  <ItemGroup>
    <CompilerVisibleProperty Include="ValidationModules_FieldNaming" />
  </ItemGroup>
  ```

## A breakpoint in Describe never hits

The generator reads `Describe` and never calls it. Set the breakpoint in the generated code. See
[How it works](./how-it-works#viewing-the-generated-code).

## Error CS0104: ambiguous reference

`Required`, `Range` and several other attribute names exist in both `ValidationModules.Constraints`
and `System.ComponentModel.DataAnnotations`, and `ValidationResult` and `ValidationContext` exist in
both `ValidationModules` and `System.ComponentModel.DataAnnotations`. Import one of each pair per
file, or alias one. See [DataAnnotations](./data-annotations#names-shared-by-both-namespaces).
