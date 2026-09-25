# Rules classes

A rules class declares a type's validation rules in C# instead of in attributes. Use one when a
rule compares two members, when a rule applies only in some states of the object, or when the
type is in an assembly you cannot add attributes to.

## Declare a rules class

<!-- verify -->
```csharp
using ValidationModules;

public sealed class Booking
{
    public string? Guest { get; init; }
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }
    public int Guests { get; init; }
    public bool IsBusiness { get; init; }
    public string? CompanyName { get; init; }
    public IReadOnlyList<string>? Requests { get; init; }
    public IReadOnlyList<Room>? Rooms { get; init; }
}

public sealed class Room
{
    public int Beds { get; init; }
}

public sealed class RoomRules : IValidationRulesFor<Room>
{
    public static void Describe(ValidationRules<Room> rules, Room x)
    {
        rules.Range(x.Beds, 1, 4);
    }
}

public sealed class BookingRules : IValidationRulesFor<Booking>
{
    public static void Describe(ValidationRules<Booking> rules, Booking x)
    {
        rules.Require(x.Guest).Length(2, 80);
        rules.Range(x.Guests, 1, 8);
        rules.Ensure(x.End > x.Start, message: "The stay must end after it starts.");

        if (x.IsBusiness)
        {
            rules.Require(x.CompanyName);
        }

        rules.Each(x.Requests).Length(1, 200);
        rules.Count(x.Rooms, 1, 4).Each();
    }
}
```

A rules class implements `IValidationRulesFor<T>` and declares a `public static void Describe`
method. The generator reads the body of `Describe` when the project builds and writes its rules into
`BookingValidator`, the same kind of validator it writes for attributes. Nothing calls `Describe`
at run time, and the rules class needs no registration.
`Describe` can also have an expression body, or implement the interface explicitly as
`static void IValidationRulesFor<Booking>.Describe(...)`.

Validating a booking with an empty guest name, 12 guests, an end date before the start date, no
company name, an empty request and a room with no beds returns:

| `Field` | `Code` | `Message` |
| --- | --- | --- |
| `guest` | `required` | guest is required. |
| `guests` | `range` | guests must be between 1 and 8. |
| `end` | `end_greater_than_start` | The stay must end after it starts. |
| `companyName` | `required` | companyName is required. |
| `requests[1]` | `string_length` | requests[1] must be between 1 and 200 characters. |
| `rooms[0].beds` | `range` | beds must be between 1 and 4. |

## Rules take values

`rules.Require(x.Guest)` passes the value of `x.Guest`. It is not a lambda. The parameter `x` stands
for the object being validated. The generator reads the member path `x.Guest` to name the field
`guest`, and the generated check reads the real value when the validator runs.

The value must be a member path on `x`, such as `x.Guest` or `x.Home?.PostalCode`. Any other
expression, such as `x.Guest!.Trim()`, is reported as `VM3007`. To use one anyway, name the field
yourself with `field:`:

```csharp
rules.Require(x.Guest?.Trim(), field: "guest");
```

## The rules

| Method | Checks | Code |
| --- | --- | --- |
| `Require(value)` | The value is not `null`. A string must also not be empty or whitespace. | `required` |
| `RequireAllowingEmpty(value)` | The string is not `null`. | `required` |
| `Length(value, min, max)` | The string's length is within the bounds. | `string_length` |
| `Range(value, min, max)` | The value is within the bounds, inclusive. | `range` |
| `RangeAtLeast(value, min)` | The value is at least `min`. | `range` |
| `RangeAtMost(value, max)` | The value is at most `max`. | `range` |
| `MultipleOf(value, divisor)` | The value divides by `divisor`. | `multiple_of` |
| `Pattern(value, regex)` | The string matches a regular expression. | `pattern` |
| `AllowedValues(value, allowed)` | The value is one of `allowed`. | `enum` |
| `Count(list, min, max)` | The number of items is within the bounds. | `array_bounds` |
| `Unique(items)` | No item appears twice. | `unique_items` |
| `Nested(value)` | Runs the validators for the member's type. | from those validators |
| `Nested(value, polymorphism)` | Runs the validators that `polymorphism` chooses for the value's actual type. | from those validators |
| `Each(list)` | Runs the rules that follow for every element. | from those rules |
| `Each(list, polymorphism)` | Runs the validators that `polymorphism` chooses for each element's actual type. | from those validators |
| `Ensure(condition)` | Any `bool` expression. | derived, see below |

Every rule except `Require`, `RequireAllowingEmpty` and `Ensure` passes a `null` value, and a
default `ImmutableArray<T>`. The [rules API reference](../reference/rules-api) lists each method's
overloads.

The range rules accept any value type that implements `IComparable<T>` and `IFormattable`, such as
`int`, `decimal`, `DateOnly` or `TimeSpan`, and the nullable form of each:

```csharp
rules.Range(x.Start, new DateOnly(2026, 1, 1), new DateOnly(2030, 12, 31));
```

`Pattern` takes a method group that returns a `Regex`, usually a `[GeneratedRegex]` method.
[Patterns](./patterns#in-a-rules-class) shows the form.

## Chains

A rule call can be followed by more rules for the same value:

```csharp
rules.Require(x.Guest).Length(2, 80);
```

A chain is one statement. When `Require` fails, the rest of its chain is skipped, so an empty guest
name reports `required` and not also `string_length`. Nothing else is skipped. Two separate
statements about the same member are checked independently.

The chained methods are `Require`, `RequireAllowingEmpty`, `Length`, `Pattern`, `Range`,
`RangeAtLeast`, `RangeAtMost`, `MultipleOf`, `Count`, `Unique`, `Each` and `Nested`. Each one
applies only to a value of the right type. `AllowedValues` can also be chained, with the values as
separate arguments. `For(value)` starts a chain without a rule of its own:

```csharp
rules.For(x.Code, field: "code").Length(3, 10).Pattern(Patterns.Code);
```

A chain cannot mix `Count` and `Unique`, because `Count` works on a list and `Unique` on any
enumerable. Write them as two statements.

## Ensure

`Ensure` takes any `bool` expression:

```csharp
rules.Ensure(x.End > x.Start);
```

The generator copies the expression into the validator, so it runs exactly as written. It is not
guarded against `null`. `rules.Ensure(x.Name.Length > 3)` throws when `Name` is `null`. Write
`x.Name is null || x.Name.Length > 3` instead.

When the expression is `false`, `Ensure` reports an error with these defaults:

- The field is the first member of `x` that the expression reads. `x.End > x.Start` reports at
  `end`.
- The code is derived from the expression. `x.End > x.Start` reports
  `end_greater_than_start`, and `x.Guests >= 1` reports `guests_greater_than_or_equal_1`.
- The message is the expression itself, as in `end > start.` Each member is written as its field
  name, so with `[JsonPropertyName("ends_at")]` on `End` it reads `ends_at > start.`

Each default can be replaced:

```csharp
rules.Ensure(
    x.End > x.Start,
    field: "end",
    code: "stay_order",
    message: "The stay must end after it starts.",
    severity: ValidationSeverity.Warning
);
```

A derived code changes when the expression changes. Editing `>` to `>=` turns
`end_greater_than_start` into `end_greater_than_or_equal_start`. The generator reports each derived
code as `VM3103`, an informational diagnostic. Pass `code:` when clients depend on the code.

A `message:` given here is authored text. Language packs do not replace it. See
[Messages and languages](./messages#authored-messages).

## Conditions and computed values

The body of `Describe` is ordinary C#. `if`, `else`, `switch` and local variables all work, and the
rules inside a branch apply only when that branch runs:

```csharp
public static void Describe(ValidationRules<Booking> rules, Booking x)
{
    var nights = x.End.DayNumber - x.Start.DayNumber;

    if (nights > 14)
    {
        rules.Require(x.CompanyName);
    }

    switch (x.Guests)
    {
        case > 4:
            rules.Count(x.Rooms, 2, 4);
            break;
    }
}
```

Every statement that is not a rule is copied into the validator as written, and it runs each time
the validator runs. Build anything expensive once, in a static field, rather than in `Describe`.

The generator cannot expand a rule inside a loop or a local function, and reports `VM3003`. A rule
inside a lambda is reported as `VM3002`, because the lambda captures `rules`. Use `Each` for
per-element rules, or report from a loop through `rules.Context`.

A bare `return;` ends the rules of that class early. The attribute checks and other rules classes
still run.

## Report from code

`rules.Context` reports an error whose field, code and message the body decides at run time. It
works inside loops:

```csharp
if (x.Requests is { } requests)
{
    for (var i = 0; i < requests.Count; i++)
    {
        if (requests[i].Length > 20)
        {
            rules.Context.Report(
                $"requests[{i}]",
                "request_too_long",
                "A request is limited to 20 characters."
            );
        }
    }
}
```

It does not work inside a lambda, an anonymous method, a local function or a query expression,
because the generated code passes the context by reference and none of those can capture it. The
generator reports `VM3003` there. Write the loop as a `foreach` instead.

`Report(field, code, message)` reports against a field. `ReportHere(code, message)` reports
against the object itself, with an empty field at the top level. The helpers such as
`ReportStringLength(field, min, max)` and `ReportRange(field, min, max)` produce the built-in
message for a built-in code, and each takes an optional `code:` and `severity:`.

`nameof(x.CompanyName)` becomes the member's field name, `companyName`, at build time, anywhere in
`Describe`, including inside messages and interpolated strings. `nameof(Booking.CompanyName)`
keeps the property name. A field written as a string is reported as written.

The generator also checks the result of every statement that returns a `ValidationFlow`, such as a
`rules.Context` report or a helper method that takes an `IValidationContextReporter`, and stops the
pass when the flow says to. A flow stored in a local variable is not checked.

## Apply a hand-written rule

`Apply` runs a static method with the signature of `RuleAction<T>`. The method receives the full
`ValidationContext`, so it can push path segments and read `Services`:

```csharp
public static class BookingChecks
{
    public static ValidationFlow RoomsHoldGuests(ref ValidationContext context, Booking value) =>
        value.Rooms is { } rooms && rooms.Sum(room => room.Beds) < value.Guests
            ? context.Report("rooms", "not_enough_beds", "The rooms do not have enough beds.")
            : ValidationFlow.Continue;
}
```

```csharp
rules.Apply(BookingChecks.RoomsHoldGuests);
```

Pass a method group that is `internal` or `public`. A lambda whose whole body calls one static
method, such as `(ref ValidationContext c, Booking v) => RoomsHoldGuests(ref c, v)`, is read as that
method, and any other lambda is reported as `VM3008`. `Apply` must be a top-level statement in
`Describe`, and applied rules run after every other rule on the type. Return the `ValidationFlow` that `Report` returned, so that a pass that
[stops at the first error](./errors#stop-at-the-first-error) can end there.

## Share rules between types

A fragment is a static `void` method that takes a `ValidationRules<T>` and adds rules to it. Call it
from `Describe` and pass `rules` and `x`:

<!-- verify -->
```csharp
using ValidationModules;

public interface IAudited
{
    string? CreatedBy { get; }
    int Version { get; }
}

public static class AuditRules
{
    public static void Standard<T>(ValidationRules<T> rules, T audited)
        where T : IAudited
    {
        rules.Require(audited.CreatedBy);
        rules.RangeAtLeast(audited.Version, 1);
    }
}

public sealed class Invoice : IAudited
{
    public string? CreatedBy { get; init; }
    public int Version { get; init; }
    public string? Number { get; init; }
}

public sealed class InvoiceRules : IValidationRulesFor<Invoice>
{
    public static void Describe(ValidationRules<Invoice> rules, Invoice x)
    {
        rules.Require(x.Number);
        AuditRules.Standard(rules, x);
    }
}
```

The generator expands the fragment for each type that calls it, so `audited.CreatedBy` reports at
`createdBy` for an `Invoice`, and `typeof(T)` there is `typeof(Invoice)`. `nameof(T)` is `"T"`, as
it is in C#. A type argument that the expansion cannot name, such as an anonymous type, is reported
as `VM3009`. A fragment can take extra parameters. A fragment can call other fragments, and a
generic fragment's call to another generic fragment is expanded for the same type. A fragment must
be source in the same project. A fragment in a referenced assembly is reported as `VM3005`.
`Nested`, `Each` and `Apply` belong in `Describe` itself, not in a fragment.

## Validate through an interface

`As<TFacet>(x)` runs the rules declared for an interface or base type that `x` implements, and
reports their errors at the current level:

<!-- verify -->
```csharp
using ValidationModules;

public interface IAudited
{
    string? CreatedBy { get; }
    int Version { get; }
}

public sealed class AuditedRules : IValidationRulesFor<IAudited>
{
    public static void Describe(ValidationRules<IAudited> rules, IAudited x)
    {
        rules.Require(x.CreatedBy);
        rules.RangeAtLeast(x.Version, 1);
    }
}

public sealed class Shipment : IAudited
{
    public string? CreatedBy { get; init; }
    public int Version { get; init; }
    public string? Carrier { get; init; }
}

public sealed class ShipmentRules : IValidationRulesFor<Shipment>
{
    public static void Describe(ValidationRules<Shipment> rules, Shipment x)
    {
        rules.Require(x.Carrier);
        rules.As<IAudited>(x);
    }
}
```

An empty `Shipment` reports `carrier`, `createdBy` and `version`, with no prefix. Constraint
attributes on an interface's properties apply to every type that implements it. When a rules class
calls `As` for the interface, the type's own checks leave those attributes to the interface's
validator, so each is checked once, where `As` runs. Under an `if`, they are checked only when the
condition holds. A base type passed to `As` is handled the same way. An interface with no rules at
all is reported as `VM3105`, and the type of `x` itself as `VM3110`.

When the interface is declared in another assembly, the validator resolves every
`IValidatorFor<IAudited>` registered in the container at run time, and runs them in registration
order as `ValidationRunner<T>` does. Validate such a type through `ValidationRunner<T>` resolved from
a scope, and call the other assembly's registration method.
[Registration](./registration#validators-from-other-assemblies) covers this.

## Nested objects and collections

`Nested(x.Home)` runs the validators for the member's type and prefixes their fields, as in
`home.postalCode`. `Each(x.Rooms)` does the same for every element of a list, as in
`rooms[0].beds`. On a list of strings, `Each` applies the rules chained after it to every element:

```csharp
rules.Each(x.Requests).Length(1, 200);
```

`Each` takes an `IReadOnlyList<T>` of strings or of a reference type, which `List<T>` and arrays
convert to. A `null` list and `null` elements are skipped. Use `Each`, not `Nested`, for a
collection. `Nested` on a collection, and a second `Nested` or `Each` in the chain after a descent,
are reported as `VM3001`. For a list of numbers or other value types, check the elements in a loop
and report through `rules.Context`. [Nested objects and
collections](./nesting) describes how paths are built.

When a member can hold a more derived type, pass a `Polymorphism` to run the validators for the
value's actual type. `Polymorphism` is in the `ValidationModules.Constraints` namespace:

<!-- verify -->
```csharp
using ValidationModules;
using ValidationModules.Constraints;

public class Animal
{
    [Required]
    public string? Name { get; init; }
}

public sealed class Dog : Animal
{
    [Required]
    public string? Breed { get; init; }
}

public sealed class Household
{
    public Animal? Pet { get; init; }
    public IReadOnlyList<Animal>? Pets { get; init; }
}

public sealed class HouseholdRules : IValidationRulesFor<Household>
{
    public static void Describe(ValidationRules<Household> rules, Household x)
    {
        rules.Nested(x.Pet, Polymorphism.CompileTime);
        rules.Each(x.Pets, Polymorphism.CompileTime);
    }
}
```

A household whose pet is a `Dog` with no breed reports `pet.breed`, because `CompileTime` runs the
`Dog` validator. A pet that is an `Animal` and not a `Dog` runs the `Animal` validator. Each element
of `Pets` runs the validator for its own type. `Polymorphism.Runtime` looks the validators up in the
container instead. [Subtypes](./nesting#subtypes) describes the modes, which work as they do on
`[ValidateNested]`.

Without the argument, `Nested` and `Each` run only the validators for the declared type, and a
descent into a type that is not sealed is reported as `VM3111`. Pass `Polymorphism.DeclaredOnly` to
keep that behaviour without the warning.

## Rules classes and attributes together

A type can have constraint attributes and a rules class. The generator merges them into one
validator. The attribute checks run first, then each rules class in the order of the class names,
then the `Apply` rules. A type can also have more than one rules class, and one class can implement
`IValidationRulesFor<T>` for several types.

A `[Required]` attribute does not suppress a rule in the rules class for the same member. The two
are independent.

A property with `[ValidateNested]` is already validated. `Nested` or `Each` on the same property in
a rules class would validate it again and report each nested error twice, so the generator reports
`VM3106` and drops the rules-class descent.

## Codes, messages and severity

The rule methods other than `Ensure` report the built-in code and message, at `Error` severity.
They take no `code:`, `message:` or `severity:` argument. To change any of these:

- Write the rule as an `Ensure` with `code:`, `message:` and `severity:`.
- Report through a `rules.Context` helper, which takes `code:` and `severity:`.
- Use a constraint attribute, which takes `Code` and `Message`.
- Keep the code and replace the message with a [message map or language pack](./messages).

## What the body can reference

The generator copies the body of `Describe` into a separate generated class. That class can reach
`internal` and `public` members, but not `private` ones. A `private const` is fine, because the
generator copies its value. Any other `private` member of the rules class that the body uses is
reported as `VM3004`. Make it `internal`.

The body cannot store the `rules` object, pass it to a method that is not a fragment, or capture it
in a lambda. Those uses are reported as `VM3002`. A `try`, `lock`, `using` or `goto` statement, or
an assignment to a member of `x`, is reported as `VM3001`. The [diagnostics
reference](../reference/diagnostics#rules-classes) lists each case with its fix.

## Debugging

A breakpoint in `Describe` never hits, because the method never runs. The generated file
`BookingRules_Rules.g.cs` holds the transcribed body. Set breakpoints there.
[How it works](./how-it-works#viewing-the-generated-code) shows how to find it.
