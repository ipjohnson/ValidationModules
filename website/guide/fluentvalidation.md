# Coming from FluentValidation

Most FluentValidation concepts have a direct counterpart here. The main difference is when the
validator is built. FluentValidation builds it at run time, from the rules your constructor
registers. ValidationModules builds it at compile time, from a rules class that the source
generator reads and never runs.

## Validators

A FluentValidation validator derives from `AbstractValidator<T>` and registers rules in its
constructor with lambdas:

```csharp
public sealed class BookingValidator : AbstractValidator<Booking>
{
    public BookingValidator()
    {
        RuleFor(x => x.Guest).NotEmpty().Length(2, 80);
        RuleFor(x => x.Guests).InclusiveBetween(1, 8);
        RuleFor(x => x.End).GreaterThan(x => x.Start);
    }
}
```

The equivalent here is a rules class. `Describe` is static, and its rule methods take values rather
than lambdas:

<!-- verify -->
```csharp
using ValidationModules;

public sealed class Booking
{
    public string? Guest { get; init; }
    public int Guests { get; init; }
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }
}

public sealed class BookingRules : IValidationRulesFor<Booking>
{
    public static void Describe(ValidationRules<Booking> rules, Booking x)
    {
        rules.Require(x.Guest).Length(2, 80);
        rules.Range(x.Guests, 1, 8);
        rules.Ensure(x.End > x.Start);
    }
}
```

The generator writes `BookingValidator` from this class. [Rules classes](./rule-classes) covers the
details.

## Rules

| FluentValidation | ValidationModules |
| --- | --- |
| `RuleFor(x => x.Name)` | `rules.Require(x.Name)`, or any rule method that takes the value |
| `.NotNull()` | `.RequireAllowingEmpty()` on a string, `.Require()` on anything else |
| `.NotEmpty()` on a string | `.Require()`, which also rejects whitespace |
| `.Length(min, max)` | `.Length(min, max)` |
| `.MinimumLength(n)`, `.MaximumLength(n)` | `.Length(min: n)`, `.Length(max: n)` |
| `.InclusiveBetween(a, b)` | `.Range(a, b)` |
| `.GreaterThanOrEqualTo(n)`, `.LessThanOrEqualTo(n)` | `.RangeAtLeast(n)`, `.RangeAtMost(n)` |
| `.GreaterThan(n)`, `.LessThan(n)` | `rules.Ensure(x.Value > n)`, or `[Range]` with `ExclusiveMin` or `ExclusiveMax` |
| `.Matches(regex)` | `.Pattern(MyRegex)` with a `[GeneratedRegex]` method |
| `.EmailAddress()` | `[EmailAddress]` on the property |
| `.IsInEnum()` | `[EnumDefined]` on the property |
| `.Must(value => ...)`, `.Must((model, value) => ...)` | `rules.Ensure(condition)` |
| `.Custom((value, context) => ...)` | `rules.Context.Report(...)`, or `rules.Apply(method)` |
| `.SetValidator(new AddressValidator())`, `ChildRules` | `rules.Nested(x.Address)` or `[ValidateNested]` |
| `.SetInheritanceValidator(...)` | `rules.Nested(x.Payment, Polymorphism.CompileTime)` or `[ValidateNested(Polymorphism.CompileTime)]` |
| `RuleForEach(x => x.Lines)` | `rules.Each(x.Lines)` |
| `.When(p)`, `.Unless(p)` | `if (p) { ... }` in `Describe`, or `When =` and `Unless =` on an attribute |
| `Include(otherValidator)` | A fragment, or `rules.As<TFacet>(x)` |
| `.WithErrorCode("x")` | `code:` on `Ensure`, `Code =` on an attribute |
| `.WithMessage("...")` | `message:` on `Ensure`, `Message =` on an attribute |
| `.WithSeverity(Severity.Warning)` | `severity:` on `Ensure` |
| `.OverridePropertyName("x")`, `.WithName("x")` | `field:` on the rule method |
| `CascadeMode.Stop` | `Require` stops its own chain. `ValidateFirst` stops the whole pass. |
| `MustAsync`, `ValidateAsync` | An `IAsyncValidatorFor<T>`, run by `ValidationRunner<T>.ValidateAsync` |

## Two semantic differences

### Ensure takes a bool, not a delegate

`Must` takes a lambda that FluentValidation calls for each value. `Ensure` takes a `bool`
expression. The generator copies the expression into the validator, where it runs each time the
validator runs. Because the expression is copied into another class:

- `Describe` is static, so the expression cannot use instance state.
- A `private` member of the rules class is not reachable from the generated class. The generator
  reports `VM3004`. Make the member `internal`.
- The expression is not guarded against `null`. Write `x.Name is null || x.Name.Length > 3`.

A rule that needs an injected service or other state belongs in a hand-written `IValidatorFor<T>`
or `IAsyncValidatorFor<T>`, registered in the container.

### Messages have no placeholders

FluentValidation messages can contain `{PropertyName}`, `{PropertyValue}` and other placeholders.
Here a message you write is literal text. The only placeholder is `{field}` on attributes.

Each error carries its `Field` and `Code` separately. Build user-facing text from the code, with a
[formatter or a language pack](./messages), rather than by parsing the message.

## Other differences

| FluentValidation | ValidationModules |
| --- | --- |
| `IValidator<T>` | `IValidatorFor<T>` |
| `AddValidatorsFromAssemblyContaining<T>()` | The generated `Add<Assembly>Validators()`, one per project |
| `ValidationResult.Errors` of `ValidationFailure` | `ValidationResult.Errors` of `ValidationError` |
| `PropertyName`, `ErrorCode`, `ErrorMessage`, `AttemptedValue` | `Field`, `Code`, `Message`, `Value` |
| `ValidateAndThrow` | `ValidateAndThrow`, which throws `ValidationException` |
| Rule sets | Not supported. Use `if` statements, or separate model types. |
| `LanguageManager` | [Language packs](./messages#language-packs) |
| ASP.NET Core automatic validation | `.Validate<T>()` on a minimal API endpoint. See [ASP.NET Core](./aspnetcore). |

The validators contain no expression trees or compiled delegates, which is what lets them run under
Native AOT.
