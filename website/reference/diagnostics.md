# Diagnostics

The generator reports problems in your declarations as diagnostics with ids from `VM1001` to
`VM6001`. They appear in the IDE as you type and in the build output. This page lists each one, what
causes it, and what to do.

The first digit of the id names the area:

| Ids | Area |
| --- | --- |
| `VM1xxx` | Constraint attributes |
| `VM2xxx` | DataAnnotations attributes |
| `VM3xxx` | Rules classes |
| `VM4xxx` | Language packs |
| `VM5xxx` | The generator and the runtime package |
| `VM6xxx` | Registration |

## Changing a severity

Set the severity of one id in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.VM1201.severity = none
```

A `[*.cs]` section applies only to diagnostics reported in C# files. The language pack diagnostics
are reported at the JSON file, and `VM5001`, `VM5002` and `VM6001` have no location, so set their
severity in a `.globalconfig` file, or list them in `<NoWarn>`:

```ini
is_global = true
dotnet_diagnostic.VM4006.severity = warning
```

To silence one declaration, put `#pragma warning disable VM1201` before it and
`#pragma warning restore VM1201` after it.

Every id is in the category `ValidationModules.Usage`, but a category-wide rule such as
`dotnet_analyzer_diagnostic.category-ValidationModules.Usage.severity` reaches only `VM5003`. The
source generator reports every other id, and category rules apply only to analyzers. Set the
severity of each id separately, or list it in `<NoWarn>`.

Many errors mean the generator dropped the constraint it describes. Silencing such an error gives
a build that passes without that check. Fix the declaration instead.

## Constraint attributes

### VM1001

**Severity:** Error

A string constraint is on a property that is not a `string`. This covers `[StringLength]`,
`[Pattern]`, `[EmailAddress]`, `[Phone]`, `[Url]`, `[CreditCard]`, `[Base64String]`,
`[FileExtensions]` and the DataAnnotations `[RegularExpression]`. `[Url]` also accepts a `Uri`. The
constraint is dropped. To check the elements of a collection of strings, use `Each` in a
[rules class](../guide/rule-classes#nested-objects-and-collections).

### VM1002

**Severity:** Error

`[ItemCount]` is on a property that is not a collection. Use `[StringLength]` for a string or
`[Range]` for a number.

### VM1003

**Severity:** Error

`[Range]` is on a type that has no ordering, such as `string`, `bool` or an enum. Use
`[StringLength]` for the length of a string, and `[EnumDefined]` or `[AllowedValues]` for an enum.

### VM1004

**Severity:** Error

`[MultipleOf]` is on a property that is not a number.

### VM1005

**Severity:** Error

`[UniqueItems]` is on a property that is not a collection.

### VM1006

**Severity:** Error

`[EnumDefined]` is on a property that is not an enum, or on an enum that declares no members.

### VM1007

**Severity:** Error

A constrained property has no getter that the validator can call, for example a `private`
getter. The property is skipped. Make the getter `public` or `internal`.

### VM1008

**Severity:** Warning

A constraint is on a positional record parameter without the `property:` target. It
applies to the constructor parameter and has no effect. Write `[property: Required]`.

### VM1009

**Severity:** Warning

A property declared with `new` hides a base property that has constraints. The base
property's constraints no longer apply, because the most derived declaration supplies all of them.
Repeat the constraints you still want on the new property, or rename one of the two.

### VM1010

**Severity:** Error

A generic type declares constraints. Its validator could not be registered without
`MakeGenericType`, so none is generated. Declare the constraints on a closed type, or validate the
generic type's payload and leave the generic type without constraints.

### VM1011

**Severity:** Warning

A constraint is on a field or a static property. The generator reads instance properties only, so
the constraint is never evaluated. Declare the member as an instance property, as the message shows.

### VM1101

**Severity:** Error

A `[StringLength]` or `[ItemCount]` minimum is greater than its maximum, so the constraint can never
pass. The DataAnnotations `[StringLength]` and `[Length]` are checked too. The first argument of
`[StringLength]` and `[ItemCount]` is the minimum.

### VM1102

**Severity:** Warning

`[Range]` sets neither a minimum nor a maximum, so it can never fail. The constraint is
dropped. Set `Min`, `Max` or both.

### VM1103

**Severity:** Error

A `[Range]` bound does not parse as the property's type, for example a date on an `int`, or
`"25:00:00"` on a `TimeOnly`. The constraint is dropped.

### VM1104

**Severity:** Error

The divisor of `[MultipleOf]`, or a constant divisor of `rules.MultipleOf`, is zero or negative.
The constraint is dropped.

### VM1105

**Severity:** Error

The `[MultipleOf]` divisor does not fit the property's type, for example `2.5` on an `int`.
The constraint is dropped.

### VM1106

**Severity:** Error

The expression in `[Pattern]` or `[RegularExpression]` is not a valid regular expression. The
message includes the parser's explanation. The constraint is dropped.

### VM1107

**Severity:** Error

The member named by `[Pattern(typeof(T), "Member")]` cannot be used. The message says whether
it does not exist, is not static, is not accessible, takes parameters, or is not a `Regex`. Point it
at a static `Regex` method, property or field that is `internal` or `public`.

### VM1201

**Severity:** Warning

`[Required]` is on a non-nullable value type, such as `int` or `Guid`. The property always
has a value, so the constraint can never fail and is dropped. Make the property nullable, or check
its value with `[Range]` or `[EnumDefined]`.

### VM1202

**Severity:** Warning

`[UniqueItems]` is on a collection whose elements compare by reference, so two elements
with the same contents are both accepted. Make the element type a record, override `Equals`, or
implement `IEquatable<T>`.

### VM1301

**Severity:** Error or warning

An inline `[Pattern("...")]`, or a DataAnnotations `[RegularExpression]`, is in a project whose
pattern policy rejects it. By default that is a project with `PublishAot` or `IsAotCompatible` set
to `true`, and the diagnostic is an error that drops the constraint. Both compile to an expression
parsed at run time, which adds the regular expression interpreter to a Native AOT binary. Declare
the expression with `[GeneratedRegex]` and point at it with `[Pattern(typeof(T), nameof(T.Member))]`,
or set `ValidationModules_PatternPolicy` to `Allow`. For `[RegularExpression]`, the message prints
the expression anchored and made optional, because that attribute matches the whole value and
passes an empty string. See [Patterns](../guide/patterns).

### VM1302

**Severity:** Warning

An inline `[Pattern]` sets `Options` to include `RegexOptions.Compiled`. The generator removes it,
because compiling the expression would emit code at run time, and the inline pattern is
interpreted. Remove it from `Options`. For a matcher compiled at build time, declare the expression
with `[GeneratedRegex]` and point at it with `[Pattern(typeof(T), nameof(T.Member))]`.

### VM1303

**Severity:** Warning

`[Pattern(typeof(T), nameof(T.Member))]` sets `Options` or `MatchTimeoutMilliseconds`. The
referenced regex was built with its own options and timeout, so the setting has no effect. Remove
it and declare it on the `[GeneratedRegex]` instead. The message prints that declaration, merged
into the member's own `[GeneratedRegex]` when it has one. `RegexOptions.Compiled` is reported here
rather than as `VM1302`, and only needs removing.

### VM1401

**Severity:** Error

`When` or `Unless` names a member the model does not declare. Use `nameof` to catch
misspellings.

### VM1402

**Severity:** Error

The member named by `When` or `Unless` is not a `bool` property, a parameterless method that
returns `bool`, or a static method that takes the model and returns `bool`.

### VM1403

**Severity:** Error

A constraint sets both `When` and `Unless`. Write two constraints, or one condition that
combines both.

### VM1501

**Severity:** Warning

The type of a `[ValidateNested]` property declares no rules, so there is nothing to
validate and the descent is dropped. Give the type constraints, a rules class or
`[GenerateValidator]`, or remove `[ValidateNested]`.

### VM1502

**Severity:** Warning

The type of a `[ValidateNested]` property is one no validator can be generated for, such as
a list of lists or a list of nullable values. The descent is dropped. Wrap the inner collection in a
type that has its own rules.

### VM1503

**Severity:** Warning

The type of a `[ValidateNested]` property is not sealed, so a value of a derived type may
reach it, and no `Polymorphism` is given. Seal the type, or pass `Polymorphism.DeclaredOnly`,
`Polymorphism.CompileTime` or `Polymorphism.Runtime`. See [Subtypes](../guide/nesting#subtypes).

### VM1504

**Severity:** Error

`Polymorphism.Runtime` is on a sealed type or a value type, whose actual type can never differ
from its declared type. Use `Polymorphism.DeclaredOnly`.

### VM1601

**Severity:** Error

A `CustomConstraintAttribute` subclass cannot be compiled. The message gives the reason: no
`public static bool IsValid` that takes the value, `IsValid` parameters that do not match the
constructor's, a constructor argument that is not a constant, or a property set at the point of use
that `IsValid` cannot receive. See [Custom constraints](../guide/custom-constraints#a-static-check).

### VM1602

**Severity:** Error

An attribute that implements `IConstraintFor<T>` cannot be compiled. The message gives the reason:
it also derives from `CustomConstraintAttribute`, it is generic, none of its `IConstraintFor<T>`
interfaces accepts the property's type, more than one does, or an argument is not a constant.

### VM1603

**Severity:** Info

The attribute class is marked `[PerValidationInstance]`, so every check creates a new instance
of it.

## DataAnnotations

### VM2001

**Severity:** Info

`ValidationModules_DataAnnotations` is set to `Ignore`, and the generator ignores this
DataAnnotations attribute. Another validation system may still enforce it.

### VM2002

**Severity:** Info

A custom `ValidationAttribute` runs its own code. The generator creates it once and calls it
with a DataAnnotations `ValidationContext` for each check. It is a warning, and the attribute is
dropped, when the attribute's arguments cannot be written into generated code.

### VM2003

**Severity:** Warning

`[Compare]` compares two members and is not compiled. Use `Ensure` in a
[rules class](../guide/rule-classes#ensure).

### VM2004

**Severity:** Info

A DataAnnotations format attribute, such as `[EmailAddress]` or `[Url]`, is compiled with the
DataAnnotations rule. The message states the rule. Use `[Pattern]` for a stricter one.

### VM2005

**Severity:** Error

`[MinLength]`, `[MaxLength]` or `[Length]` is on a property that is neither a string nor a
collection.

### VM2006

**Severity:** Info

The model implements `IValidatableObject`. Its `Validate` method runs after every other rule,
and only when nothing has been reported in the pass so far, warnings included.

### VM2007

**Severity:** Warning

`[EnumDataType]` is not compiled. Type the property as the enum and use `[EnumDefined]`.

### VM2008

**Severity:** Error

`[CustomValidation]` names a method that cannot be called. The method must be public and
static, take the value and optionally a `ValidationContext`, and return a DataAnnotations
`ValidationResult`. The message gives the reason.

### VM2009

**Severity:** Warning

A custom `ValidationAttribute` sets `ErrorMessageResourceType`. DataAnnotations reads the
resource with reflection, which trimming can break. Set `ErrorMessage`, or keep the resource type
from being trimmed.

## Rules classes

### VM3001

**Severity:** Error

`Describe` contains something the generator cannot copy into the validator. The message names it.
The cases are a `try`, `lock`, `using` or `goto` statement, a `return` with a value, an assignment
to a member of `x`, `Apply` anywhere but the top level of `Describe`, `Require` chained after
`Each`, `Nested`, `Each` or `Apply` inside a fragment, and a rule call that does not compile.

Pass `Apply` a method group. A lambda passed to `Apply` produces generated code that does not
compile in this version, and no diagnostic reports it. `Nested` or `Each` chained after `Each` is
not supported either. See [the rules API reference](./rules-api#collections).

### VM3002

**Severity:** Error

The `rules` object is used in a way the generator cannot follow: stored in a variable,
captured in a lambda, returned, or passed to a method that is not a fragment. The same error covers
a fragment or `As` given something other than `x`.

### VM3003

**Severity:** Error

A rule is declared inside a loop or a local function. A local function also gives `VM3002`, and a
rule inside a lambda gives `VM3002` alone. Use `Each` for per-element rules, or report from the loop
through `rules.Context`.

### VM3004

**Severity:** Error

The body of `Describe` uses a member that the generated class cannot reach, such as a
`private` method or field of the rules class. Make it `internal`. A `private const` is allowed.

### VM3005

**Severity:** Error

A fragment is declared in a referenced assembly. Fragments must be source in the same
project. Share them through a shared project or a source-only package.

### VM3006

**Severity:** Error

Fragments call each other in a cycle. The message shows the cycle.

### VM3007

**Severity:** Error

A rule's value is not a member path on `x`, for example `x.Name.Trim()`, so the error has no
field. Pass a member path, or give the field with `field:`.

### VM3101

**Severity:** Error

`Require` is applied to a non-nullable value type, which can never be missing. Use a range
rule, or make the property nullable.

### VM3102

**Severity:** Error

An `Ensure` condition reads no member of `x`, so it has no field to report against. Read the
member the rule is about, or pass `field:`.

### VM3103

**Severity:** Info

An `Ensure` without `code:` reports a code derived from its condition. The message states the
code. Pass `code:` to keep the code fixed when the condition changes.

### VM3104

**Severity:** Warning

A rule unwraps a nullable member with `.Value`. The rule accepts the nullable directly, and
the generator corrects the call. Remove `.Value`.

### VM3105

**Severity:** Error

`As<TFacet>` names an interface or base type that has no rules in this project. Give it
constraint attributes or a rules class.

## Language packs

These diagnostics point at the JSON file.

### VM4001

**Severity:** Error

The language pack is not valid JSON, or it has no `culture`. The file is skipped.

### VM4002

**Severity:** Warning

A key names a shape that does not exist, such as `string_length.atmost`. The message
suggests the nearest shape. The entry is skipped.

### VM4003

**Severity:** Error

A template uses an argument number that its shape does not have. The entry is skipped.

### VM4004

**Severity:** Error

A key appears more than once. The entries after the first are skipped.

### VM4005

**Severity:** Warning

The culture in the file name differs from the `culture` in the file. The file's `culture`
is used.

### VM4006

**Severity:** Info

The pack does not cover every shape. The message lists the missing keys. Messages for those
shapes stay in English. To require complete packs, raise it to a warning or an error in a
`.globalconfig` file, as shown under [Changing a severity](#changing-a-severity).

## Generator and runtime

### VM5001

**Severity:** Error

The referenced `ValidationModules.Runtime` is older than the generated code requires, or it
is missing. Reference `ValidationModules.Runtime` at the same version as
`ValidationModules.SourceGenerator`. Other compile errors in generated files may appear with this
one, so fix it first.

### VM5002

**Severity:** Error

The generator failed while writing code. The build fails so that a validator cannot go
missing without notice. The message names the stage and the exception. Please
[report it](https://github.com/ipjohnson/ValidationModules/issues). Until it is fixed, change the
construct the message names. One known cause is `Each` over a collection of collections in a rules
class.

### VM5003

**Severity:** Warning

`.Validate<T>()` names a type in this project that has no constraints, no
`[GenerateValidator]`, no rules class and no hand-written validator, so the endpoint would fail when
it is built. Add rules or `[GenerateValidator]`. When the rules come from another assembly, the
warning does not apply. This is the one diagnostic that an analyzer reports rather than the
generator.

## Registration

### VM6001

**Severity:** Warning

The project declares more than one module entry point, marked `[DependencyModule]` or
`[HardenedModule]`, and the validators are registered into each of them. Remove the attribute from
the classes that are not applications. When the project contains two applications on purpose, add
`VM6001` to `<NoWarn>`.

## Ids before 1.0.0

Before 1.0.0 the ids were numbered `VM0001` to `VM0108`. A `.editorconfig` or `<NoWarn>` entry that
uses an old id has no effect now. Replace it with the new id:

| Before 1.0.0 | Now | Before 1.0.0 | Now |
| --- | --- | --- | --- |
| `VM0001` | `VM1001` | `VM0065` | `VM1103` |
| `VM0002` | `VM1002` | `VM0067` | `VM2006` |
| `VM0003` | `VM1003` | `VM0068` | `VM2007` |
| `VM0004` | `VM1201` | `VM0070` | `VM3001` |
| `VM0006` | `VM1106` | `VM0071` | `VM3007` |
| `VM0007` | `VM1501` | `VM0075` | `VM3102` |
| `VM0008` | `VM1101` | `VM0079` | `VM1010` |
| `VM0009` | `VM1007` | `VM0080` | `VM2008` |
| `VM0010` | `VM2001` | `VM0081` | `VM2009` |
| `VM0016` | `VM1302` | `VM0082` | `VM1601` |
| `VM0017` | `VM1301` | `VM0083` | `VM1602` |
| `VM0018` | `VM1107` | `VM0084` | `VM1603` |
| `VM0021` | `VM1004` | `VM0085` | `VM3005` |
| `VM0022` | `VM1104` | `VM0086` | `VM3006` |
| `VM0023` | `VM1105` | `VM0087` | `VM3002` |
| `VM0024` | `VM1005` | `VM0088` | `VM3004` |
| `VM0025` | `VM1202` | `VM0089` | `VM3003` |
| `VM0026` | `VM1102` | `VM0090` | `VM3101` |
| `VM0027` | `VM1006` | `VM0091` | `VM3105` |
| `VM0028` | `VM1401` | `VM0092` | `VM3103` |
| `VM0029` | `VM1402` | `VM0093` | `VM3104` |
| `VM0030` | `VM1009` | `VM0100` | `VM4001` |
| `VM0031` | `VM1503` | `VM0101` | `VM4002` |
| `VM0032` | `VM1504` | `VM0102` | `VM4003` |
| `VM0033` | `VM1403` | `VM0103` | `VM4004` |
| `VM0040` | `VM5001` | `VM0104` | `VM4005` |
| `VM0051` | `VM1008` | `VM0105` | `VM4006` |
| `VM0060` | `VM2002` | `VM0106` | `VM1502` |
| `VM0061` | `VM2003` | `VM0107` | `VM5002` |
| `VM0063` | `VM2004` | `VM0108` | `VM5003` |
| `VM0064` | `VM2005` |  | |

`VM6001` was added in 1.1.0 and has no earlier id.
