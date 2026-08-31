# Diagnostics

Every diagnostic this generator reports, why it exists, and what to do about it.

All of them are in category `ValidationModules.Usage`, so a blanket `.editorconfig` rule reaches the
whole set:

```ini
[*.cs]
dotnet_diagnostic.VM1201.severity = none
dotnet_analyzer_diagnostic.category-ValidationModules.Usage.severity = suggestion
```

Prefer silencing one id over the category. Several are errors because the alternative is generated
code that does not compile.

## Summary

| ID | Severity | |
|---|---|---|
| [VM1001](#vm1001) | Error | a string constraint on a non-string |
| [VM1002](#vm1002) | Error | `[ItemCount]` on a non-collection |
| [VM1003](#vm1003) | Error | `[Range]` on a type with no ordering |
| [VM1201](#vm1201) | Warning | `[Required]` on a non-nullable value type |
| [VM1106](#vm1106) | Error | a pattern that is not a valid regex |
| [VM1501](#vm1501) | Warning | `[ValidateNested]` target has no rules |
| [VM1101](#vm1101) | Error | lower bound exceeds upper bound |
| [VM1007](#vm1007) | Error | constrained property has no accessible getter |
| [VM2001](#vm2001) | Info | a DataAnnotations constraint is ignored by ValidationModules |
| [VM1302](#vm1302) | Warning | `RegexOptions.Compiled` is not meaningful |
| [VM1004](#vm1004) | Error | `[MultipleOf]` on a type with no arithmetic |
| [VM1104](#vm1104) | Error | a `[MultipleOf]` divisor that is zero or negative |
| [VM1105](#vm1105) | Error | a `[MultipleOf]` divisor that does not parse as the member's type |
| [VM1005](#vm1005) | Error | `[UniqueItems]` on a non-collection |
| [VM1202](#vm1202) | Warning | `[UniqueItems]` over elements with no equality of their own |
| [VM1102](#vm1102) | Warning | `[Range]` declares neither bound |
| [VM1006](#vm1006) | Error | `[EnumDefined]` was applied to a member whose type is not an enum. |
| [VM1401](#vm1401) | Error | a `When`/`Unless` naming a member the type does not declare |
| [VM1402](#vm1402) | Error | a `When`/`Unless` naming something that is not a predicate |
| [VM1009](#vm1009) | Warning | a derived property hides a base declaration's constraints |
| [VM1503](#vm1503) | Warning | a `[ValidateNested]` target is not sealed and declares no mode |
| [VM1504](#vm1504) | Error | `Polymorphism.Runtime` on a type that can have no subtypes |
| [VM1403](#vm1403) | Error | a constraint setting both `When` and `Unless` |
| [VM1301](#vm1301) | *policy* | an inline pattern roots the regex engine |
| [VM1107](#vm1107) | Error | referenced regex member is unusable |
| [VM5001](#vm5001) | Error | `ValidationModules.Runtime` is too old |
| [VM1008](#vm1008) | Warning | constraint on a record parameter without `property:` |
| [VM2002](#vm2002) | Info¹ | a custom `ValidationAttribute` is constructed once and invoked |
| [VM2003](#vm2003) | Warning | a cross-field DataAnnotations attribute is not compiled |
| [VM2004](#vm2004) | Info | a format DataAnnotations attribute is compiled with its BCL semantics |
| [VM2005](#vm2005) | Error | a length constraint on neither a string nor a collection |
| [VM1103](#vm1103) | Error | `[Range]` bounds do not parse as the member's type |
| [VM2006](#vm2006) | Info¹ | `IValidatableObject` runs after every other rule passes |
| [VM2007](#vm2007) | Warning | `[EnumDataType]` checks a runtime string conversion and is not compiled |
| [VM3001](#vm3001) | Error | a statement in `Describe` is not transcribable |
| [VM3007](#vm3007) | Error | a rule's value argument is not a member path on the subject |
| [VM3102](#vm3102) | Error | an `Ensure` has no inferable field and no `field:` |
| [VM1010](#vm1010) | Error | a generic type cannot have a generated validator |
| [VM2008](#vm2008) | Error | a `[CustomValidation]` target cannot be called |
| [VM2009](#vm2009) | Warning | resource-based `ErrorMessage` resolves reflectively |
| [VM1601](#vm1601) | Error | a custom constraint attribute's `IsValid` is missing or the wrong shape |
| [VM1602](#vm1602) | Error | an `IConstraintFor<T>` attribute does not fit the member, or mixes shapes |
| [VM1603](#vm1603) | Info | a `[PerValidationInstance]` constraint constructs an instance at every check |
| [VM3005](#vm3005) | Error | a fragment is compiled IL from a referenced assembly |
| [VM3006](#vm3006) | Error | a fragment call chain returns to where it started |
| [VM3002](#vm3002) | Error | the rules builder flows where the generator cannot follow |
| [VM3004](#vm3004) | Error | transcribed code references a member the companion file cannot reach |
| [VM3003](#vm3003) | Error | a rule declaration sits inside a loop, lambda, or local function |
| [VM3101](#vm3101) | Error | `Require` on a non-nullable value type can never fail |
| [VM3105](#vm3105) | Error | a facet validated with `As` declares no rules in this compilation |
| [VM3103](#vm3103) | Info | the code an `Ensure` derived from its condition |
| [VM3104](#vm3104) | Warning | a rule value unwraps a nullable member with `.Value` |
| [VM1502](#vm1502) | Warning | a `[ValidateNested]` target can never have a validator |
| [VM5002](#vm5002) | Error | an emit stage threw; the build fails instead of succeeding with source missing |
| [VM5003](#vm5003) | Warning | `.Validate<T>()` names a type with no validator in this compilation |

---

## Constraint diagnostics

### VM1001 {#vm1001}

**Error**: *`'[StringLength]' applies to strings; 'Age' is 'int'`*

A string constraint, `[StringLength]` or `[Pattern]`, on a member that is not a `string`.

```csharp
[StringLength(1, 10)] // VM1001
public int Age { get; init; }

[Pattern("^a$")] // VM1001
public int Age { get; init; }
```

You probably wanted `[Range]` for a number, or `[ItemCount]` for a collection.

### VM1002 {#vm1002}

**Error**: *`[ItemCount] applies to collections; 'Name' is 'string'`*

```csharp
[ItemCount(1, 10)] // VM1002
public string? Name { get; init; }
```

A `string` is deliberately **not** a collection here, even though it implements `IEnumerable<char>`.
Taking that reading would turn a length constraint into a per-character walk, so this is VM1002 and
`[StringLength]` is what you wanted.

### VM1003 {#vm1003}

**Error**: *`[Range] applies to numeric and date types; 'Name' is 'string'`*

`[Range]` emits a pair of comparisons, so the member's type has to support them. Integral,
floating-point, `decimal`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly` and `TimeSpan` all
qualify, as do their nullable forms.

### VM1201 {#vm1201}

**Warning**: *`'Age' is a non-nullable value type, so it is always present and [Required] can never fail`*

```csharp
[Required] // VM1201
public int Age { get; init; }
```

A warning rather than an error: the declaration is harmless, just inert. Making it an error would
break a build over a no-op.

Use `int?` if the value is genuinely optional, or `[Range]` if what you meant was "not zero".

### VM1106 {#vm1106}

**Error**: *`The pattern on 'Sku' is not a valid regular expression: …`*

The message is the regex engine's own. Re-describing it would produce something worse than what the
parser already says.

### VM1501 {#vm1501}

**Warning**: *`'Address' declares no constraints and no [GenerateValidator], so [ValidateNested] on 'Home' validates nothing and the descent is dropped`*

No validator exists for the nested type, so there is nothing for the descent to call and it is
dropped from the generated validator. A model that reads as validated and validates nothing is the
failure this library exists to make impossible, which is why the silence is stated here rather
than discovered.

**Warning rather than error**, unlike its neighbours. The result is a rule that does not run rather
than one that runs where it should not, and writing `[ValidateNested]` before the nested type's own
constraints is an ordinary order to work in.

It stays quiet when the target's rules come from a [rule class](/guide/rule-classes), when the
target carries `[GenerateValidator]`, when the target itself carries `[ValidateNested]` and so gets
a validator that descends further, and when the target comes from another assembly, which may carry
a validator generated over there that this compilation cannot see.

Mark the nested type `[GenerateValidator]` when its rules arrive from a
[rule class](/guide/rule-classes) rather than from its own attributes.

### VM1101 {#vm1101}

**Error**: *`The bounds on 'Name' are inverted, so the constraint can never be satisfied`*

```csharp
[StringLength(10, 1)] // VM1101
public string? Name { get; init; }
```

Applies to `[StringLength]` and `[ItemCount]`. Equal bounds are fine, since `[StringLength(2, 2)]`
is an exact length.

### VM1007 {#vm1007}

**Error**: *`'Name' has no accessible getter, so its constraints cannot be evaluated`*

```csharp
[Required] // VM1007
public string? Name { set { } }

[Required] // VM1007
public string? Name { private get; set; }
```

`internal` is fine, because the generated validator lands in the same assembly.

The unreadable property is dropped rather than emitted anyway, so the build fails on VM1007 alone
and not also on generated code that will not compile. Every other constraint on the type still
applies.

### VM1302 {#vm1302}

**Warning**: *`Patterns compile through [GeneratedRegex]; RegexOptions.Compiled on 'Sku' is ignored`*

```csharp
[Pattern("^a$", Options = RegexOptions.Compiled)] // VM1302
```

`RegexOptions.Compiled` emits IL through `Reflection.Emit`, which is the habit this library exists to
remove, and it does nothing here regardless. Other `RegexOptions` values are honoured.

### VM1301 {#vm1301}

**Severity depends on policy**: *`The pattern on 'Sku' is built from a string at run time, which roots the regex parser and interpreter …`*

An inline `[Pattern("…")]` in an AOT-facing project. Constructing a `Regex` from a string means the
parser and interpreter must be in the binary, which is about 450 KB, once.

```csharp
[Pattern("^[A-Z]{3}$")] // VM1301 under AOT
public string? Sku { get; init; }
```

Declare it with `[GeneratedRegex]` and point at it:

```csharp
public static partial class PetPatterns {
    [GeneratedRegex("^[A-Z]{3}$")]
    public static partial Regex Sku();
}

[Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
public string? Sku { get; init; }
```

| `ValidationModules_PatternPolicy` | Effect |
|---|---|
| *(unset)* | `Error` if `PublishAot` or `IsAotCompatible`, else `Allow` |
| `Allow` | silent |
| `Warn` | warning, constraint still emitted |
| `Error` | error, constraint dropped |

Under `Error` the constraint is dropped and the rest of the type is still emitted, so the build fails
with one useful diagnostic rather than two. [Patterns and regex](/guide/patterns) has the detail.

### VM1107 {#vm1107}

**Error**: *`'PetPatterns.Sku' is not static, so the pattern on 'Sku' cannot be emitted`*

The referenced member must exist, be static, take no parameters if it is a method, return `Regex`,
and be visible to the generated validator. The message names which of those failed:

| Reason |
|---|
| `does not exist` |
| `is not static` |
| `takes parameters` |
| `does not return Regex` / `is not a Regex` |
| `is not accessible` |
| `is not a method, property or field` |

### VM1004 {#vm1004}

**Error**: *`[MultipleOf] applies to integral, decimal and floating-point types; 'Name' is 'string'`*

The check is arithmetic, so the member's type has to support it. Every integral type, `decimal`,
`double` and `float` qualify, as do their nullable forms. Dates do not: `multipleOf` has no meaning
for them in OpenAPI either.

### VM1104 {#vm1104}

**Error**: *`The divisor on 'Quantity' is '0'; it must be greater than zero`*

```csharp
[MultipleOf(0)] // VM1104
public int Quantity { get; init; }
```

An error rather than a dropped rule, because of where the alternative fails. `value % 0` is CS0020
for an integral member and a `DivideByZeroException` for a decimal one. Leaving it to the emitter
puts the failure inside a generated file, which is the one place an error must not land. A negative divisor is caught here too; OpenAPI requires `multipleOf` to be positive, and
`% -5` answers the same question as `% 5` while reading as if it did not.

### VM1105 {#vm1105}

**Error**: *`The divisor on 'Quantity' does not parse as 'int'`*

The divisor is parsed at build time against the member's own type. This fires when it has no form
that type can be checked against, such as a string that is not a number or a fractional divisor on
an integral member:

```csharp
[MultipleOf("2.5")] // VM1105: 'Quantity' is int
public int Quantity { get; init; }
```

Dropping the fraction silently would emit `value % 2`, which is a different rule.

### VM1005 {#vm1005}

**Error**: *`[UniqueItems] applies to collections; 'Name' is 'string'`*

`string` is deliberately not a collection here, as it is not for `[ItemCount]`. Treating one as a
collection would turn this into a check for repeated characters.

### VM1202 {#vm1202}

**Warning**: *`'Sample.Tag' does not override Equals, so [UniqueItems] on 'Tags' compares elements by reference and two elements with equal contents both pass`*

```csharp
public class Tag { public string? Value { get; init; } }

public sealed record Order {
    [UniqueItems] // VM1202
    public List<Tag> Tags { get; init; } = [];
}
```

Uniqueness runs through `EqualityComparer<T>.Default`. For a class that overrides nothing that is
reference equality, so two elements with identical contents are both "unique" and the rule passes
for the wrong reason. That is worse than one that fails, because nothing says so.

Make it a `record`, override `Equals`, or implement `IEquatable<T>`; any of the three silences this
and makes the check mean what it reads as. Structs do not warn: `ValueType.Equals` compares fields,
which is slow but correct.

### VM1102 {#vm1102}

**Warning**: *`[Range] on 'Age' sets neither Min nor Max, so it can never fail`*

```csharp
[Range] // VM1102
public int Age { get; init; }
```

A warning rather than an error, for VM1201's reason: the declaration is inert rather than wrong.
Set `Min`, `Max`, or both.

### VM1006 {#vm1006}

**Error**: *`[EnumDefined] applies to enum types; 'Quantity' is 'int'`*

```csharp
[EnumDefined] // VM1006
public int Quantity { get; init; }
```

The check is a comparison against the members the type declares, so a type that declares none has
nothing to compare against. An enum with no members reports the same way, for the same reason: there
is no value it could accept.

Nullable enums are fine. A `PaymentMethod?` is checked when it has a value and passes when it does
not.

### VM1401 {#vm1401}

**Error**: *`'PolicyNumber' names 'IsAuto', which 'Claim' does not declare`*

```csharp
[Required(When = nameof(IsAuto))] // VM1401 if Claim has no IsAuto
public string? PolicyNumber { get; init; }
```

A condition names a member of the type being validated. Resolution walks the base chain, so a
predicate declared on a shared base works from every type that inherits it. That is also why a
condition on an *inherited* constraint resolves against the derived type rather than the one that
declared it.

### VM1402 {#vm1402}

**Error**: *`'Claim.IsAuto' cannot be used as a condition`*

Three shapes are accepted, and they are the three that cannot capture anything:

```csharp
public bool IsAuto { get; init; }                    // a bool property
public bool IsAuto() => …;                           // a parameterless bool method
public static bool IsAuto(Claim value) => …;         // a static bool method taking the model
```

The three shapes cannot capture anything, so self-containment holds here by construction rather
than by analysis. There is no `WhenType`, so shared logic is reached through a one-line forwarder
on the model.

### VM1009 {#vm1009}

**Warning**: *`'Name' hides 'Base.Name', so the 2 constraint(s) declared there no longer apply`*

```csharp
public class Base {
    [Required]
    [StringLength(1, 10)]
    public virtual string? Name { get; set; }
}

public class Derived : Base {
    [StringLength(1, 200)]
    public new string? Name { get; set; } // VM1009
}
```

Constraints are inherited, and the most-derived declaration of a property supplies **all** of that
property's constraints rather than some of them, because two `[StringLength]` bounds on one field
would be ambiguous and would report twice. So `new` silently drops what the base said, and this says
so.

An `override` is one property with two declarations rather than two properties, so it does not fire:
`ValidationConstraintAttribute` is `Inherited = true` and those declarations accumulate.

### VM1503 {#vm1503}

**Warning**: *`'Address' is not sealed, so a value of a more derived type may reach 'Home'`*

```csharp
public record Address { … }        // not sealed

public sealed record Person {
    [ValidateNested] // VM1503
    public Address? Home { get; init; }
}
```

A descent dispatches on the declared type, so a subtype's own rules are not reached unless you ask
for them. Say what should happen: seal the target, or pass a
[`Polymorphism`](./attributes#polymorphism) mode.

This fires widely on existing code, because `public record Address` without `sealed` is the common
idiom. That is intended: sealing is the default posture, not a workaround.

::: tip Why it keys on sealed rather than on subtypes
Deliberately a local fact about the type, never "are any subtypes visible from here". A diagnostic
keyed on visibility would appear when a hierarchy sat in one assembly and vanish when a type moved
to a package, reintroducing the layout-dependence that explicit modes exist to prevent.
:::

### VM1504 {#vm1504}

**Error**: *`'Address' is sealed, so its runtime type can never differ from its declared type`*

```csharp
[ValidateNested(Polymorphism.Runtime)] // VM1504
public Address? Home { get; init; }    // where Address is sealed
```

`Runtime` buys a container lookup for an answer the declared type already had. Use `DeclaredOnly`.

### VM1403 {#vm1403}

**Error**: *`'Required' on 'PolicyNumber' sets both When and Unless, which is ambiguous`*

Write two constraints, or one negated condition.

### VM5001 {#vm5001}

**Error**: *`The generated validators require ValidationModules.Runtime contract N or later; the referenced runtime is contract M`*

Version lockstep. The generator emits calls against a runtime surface, and a runtime older than that
surface would fail *inside generated code*, the worst place for an error to land. So the check runs
before any source is added and the build fails here instead.

Update the `ValidationModules.Runtime` package reference to match the generator.

### VM1008 {#vm1008}

**Warning**: *`'Required' is on a record parameter without the property: target, so it lands on the parameter and is never evaluated. Write [property: Required]`*

```csharp
public sealed record Pet([Required] string Name);              // VM1008
public sealed record Pet([property: Required] string Name);    // correct
```

Without this the failure is silent in every direction. The attribute binds to the primary
constructor's parameter, so the generated property carries no metadata, the type looks entirely
unconstrained, and **no validator is emitted at all**, not even an empty one. Nothing is registered,
so
`IValidatorFor<Pet>` does not resolve and a `ValidationRunner<Pet>` merging zero validators reports
every value as valid.

Reported before any property is read, precisely because the situation is one where no property
carries anything. One diagnostic per attribute, since each has to be fixed. The diagnostic is the
whole output, and no empty validator is emitted alongside it.

Scoped to the **primary** constructor. A constraint on an ordinary constructor's parameter is
equally inert, but `[property:]` is not legal there, so this advice would be wrong.

Write `[property: Required]`, or use a record with an explicit body:

```csharp
public sealed record Pet {
    [Required]
    public string? Name { get; init; }
}
```

---

## DataAnnotations diagnostics

¹ Under [`ValidationModules_DataAnnotations`](/reference/msbuild) `Ignore`, VM2002 and VM2006 keep
their Info severity but swap their message's tail: it says that *ValidationModules* is the one
ignoring the rule. With the front end deliberately off, an attribute this library leaves alone is
configuration working rather than a problem, and another validation system reading the same
attributes may still enforce it.

### VM2001 {#vm2001}

**Info**: *`'RequiredAttribute' on 'Name' is a DataAnnotations constraint, which ValidationModules is ignoring because ValidationModules_DataAnnotations is set to Ignore; another validation system may still enforce it`*

Reported once per ignored constraint, so switching the front end off cannot silently change what
this library validates. It names ValidationModules deliberately: the attribute itself stays in
the compilation, and DataAnnotations, a different validator, or a test harness may still read
it. To have this library enforce the rule, remove the `Ignore` setting or move it to
[native constraints](/guide/constraints).

### VM2002 {#vm2002}

**Info** (**Info** with an ignoring tail under `Ignore`¹): *`'EvenNumberAttribute' on 'Age' derives from ValidationAttribute, so its check is user code. It is constructed once and invoked with DataAnnotations semantics, so this property pays DataAnnotations' costs: a ValidationContext per check, and a box if the value is a value type`*

A custom `ValidationAttribute` subclass is [invoked, not compiled](/guide/data-annotations#custom-rules-are-invoked):
constructed once from its compile-time-constant arguments into a static field, then run through
`GetValidationResult` exactly as `Validator.TryValidateObject` would run it. The Info exists for
the cost model, since this is the one place a property's validation allocates on a passing value.
A [rule class](/guide/rule-classes) remains the zero-cost home for the same logic.

The rare attribute whose arguments cannot be rendered (a broken compilation) reports the same id
back at **Warning** with the old not-enforced message.

### VM2003 {#vm2003}

**Warning**: *`'CompareAttribute' on 'Confirm' compares against another member, which a per-property constraint cannot express`*

```csharp
[Compare(nameof(Password))] // VM2003
public string? Confirm { get; set; }
```

Use `rules.Ensure` in a [rule class](/guide/rule-classes), the declaration form that *can* span
two properties:

```csharp
rules.Ensure(x.Password == x.Confirm, code: "password_mismatch");
```

### VM2004 {#vm2004}

**Info**: *`'EmailAddressAttribute' on 'Email' compiles to the DataAnnotations check: the value must contain exactly one '@', neither first nor last, and no line breaks - 'a@b' passes, as RFC 5322 permits. Declare a [Pattern] instead if you want a stricter rule`*

Covers `[EmailAddress]`, `[Phone]`, `[Url]`, `[CreditCard]`, `[Base64String]` and
`[FileExtensions]`, each compiled to
[the BCL's exact check](/guide/data-annotations#the-format-validators).

Info rather than Warning, because there is nothing to fix: the attribute is enforced, with the
same semantics every other DataAnnotations consumer gives it, which Microsoft documents as frozen
by design. The message exists because those semantics are looser than the attribute names suggest.
An `a@b` really does pass `[EmailAddress]`, and RFC 5322 agrees it should. The exact compiled check
is stated once, at the site that declared it, where an author who wanted something
stricter will actually read it.

### VM2005 {#vm2005}

**Error**: *`'MinLengthAttribute' applies to strings and collections; 'Age' is 'int'`*

`[MinLength]`, `[MaxLength]` and `[Length]` apply to both strings and collections in DataAnnotations,
so the member's type decides which constraint each becomes. A member that is neither has no reading.

### VM1103 {#vm1103}

**Error**: *`The bounds on 'Born' do not parse as 'System.DateOnly'`*

```csharp
[Range("not-a-date", "2100-01-01")] // VM1103
public DateOnly Born { get; init; }

[Range("abc", "def")] // VM1103
public decimal Price { get; init; }

[Range("2000-01-01", "2100-01-01")] // VM1103
public int Age { get; init; }
```

A bound written as a string is parsed against the member's own type at generation time, which is
what `RangeAttribute`'s documentation has always promised. It is emitted as a constructor call rather
than a quoted literal. A bound that does not parse is reported here, at the declaration, rather than
becoming a comparison between a `DateOnly` and a `string` inside a generated file.

The constraint is dropped when its bounds do not parse, so the build fails on VM1103 alone and not
also on generated code that will not compile.

`[Range]` on a member with no ordering at all is [VM1003](#vm1003), which fires first. Saying it
twice would be worse than saying it once.

::: tip What the emitted bound looks like
`[Range("2000-01-15", "2100-12-31")]` on a `DateOnly` becomes
`new global::System.DateOnly(2000, 1, 15)` and `new global::System.DateOnly(2100, 12, 31)`, in both
the comparison and the message, so the two cannot disagree about what the bound is. A `DateTime`
bound is `DateTimeKind.Unspecified`: a bound written `"2000-01-01"` carries no zone, and anchoring it
to whatever the build machine happened to be in would make the same source mean two things.
:::

### VM2006 {#vm2006}

**Info** (**Info** with an ignoring tail under `Ignore`¹): *`'Customer' implements IValidatableObject; the generated validator calls its Validate method after every other rule on the type has passed, exactly as Validator.TryValidateObject sequences it, and the type keeps no boolean fast path`*

Object-level validation is [compiled with DataAnnotations' own sequencing](/guide/data-annotations#custom-rules-are-invoked):
last, and only when the pass is otherwise clean. The boolean fast path cannot know "the whole pass
was clean", so the type falls back to the interface default `IsValid`. That is correct but not
free, the same trade a type carrying `rules.Apply(…)` already makes.

### VM2007 {#vm2007}

**Warning**: *`'EnumDataTypeAttribute' on 'Day' checks that a loosely-typed value parses as an enum, a runtime conversion this library does not compile. It is not enforced; type the member as the enum and use [EnumDefined]`*

```csharp
[EnumDataType(typeof(DayOfWeek))] // VM2007 - not enforced
public string? Day { get; set; }
```

`[EnumDataType]` validates that a string or number *parses* as a member of the named enum, which
is the same runtime string conversion [VM2008](#vm2008)'s narrowing refuses. The native answer is
to type the member as the enum - the deserializer then owns the parse - and constrain it with
[`[EnumDefined]`](/reference/attributes#enumdefined), which checks membership without boxing or
reflection.

### VM2008 {#vm2008}

**Error**: *`'CustomValidationAttribute' on 'Name' cannot be compiled: 'Sample.Checks.Verify' is not a public static method taking one or two parameters`*

`[CustomValidation]` names its method as a string, which DataAnnotations resolves reflectively per
validation. This generator resolves it once, at build time, and emits a direct call, so a target
that cannot be called is a build error naming the reason rather than a rule that silently never
runs.
Accepted shapes are DataAnnotations' own: public static, returning `ValidationResult`, taking the
value alone or the value and a `ValidationContext`.

One deliberate narrowing: the value parameter must accept the member's type as declared, or be
`object`. DataAnnotations would attempt a runtime string conversion; this library will not convert
silently, and the message says what to change.

### VM2009 {#vm2009}

**Warning**: *`'EvenNumberAttribute' on 'Age' sets ErrorMessageResourceType, which DataAnnotations resolves with reflection when the message is formatted`*

The one part of an invoked attribute the trimmer can break: resource-based messages reflect over
the resource type at format time, and a trimmed publish may have removed the property. Set
`ErrorMessage`, or keep the resource type rooted.

### VM1601 {#vm1601}

**Error**: *`'SkuAttribute' on 'Code' cannot be compiled: IsValid's first parameter is 'int', which cannot accept this member's 'string?'`*

A [`CustomConstraintAttribute`](/guide/custom-constraints) subclass whose contract does not hold:
no public static bool `IsValid`, a first parameter that cannot accept the member, extra
parameters that do not line up with the constructor positionally and by type, or a custom
property setter, which a static check has no way to receive. Erroring beats an argument that
silently never arrives.

Catching the shape at build time is the feature. The invoked DataAnnotations form discovers the
same mistakes at run time, or never.

### VM1602 {#vm1602}

**Error**: *`'EvenAttribute' on 'Code' cannot be compiled: it implements IConstraintFor<int>, and none of those accepts this member's 'string?'`*

An attribute implementing [`IConstraintFor<T>`](/guide/custom-constraints#when-the-check-needs-an-instance)
that cannot be compiled, with the reason in the tail: no implemented instantiation accepts the
member's type, more than one does (implement the member's own type, since an exact instantiation
always wins outright), an argument in the declaration is not a renderable constant, the attribute
class is generic, or the class also derives from `CustomConstraintAttribute`, which is two native
shapes disagreeing about who runs the check.

Deriving from DataAnnotations' `ValidationAttribute` *and* implementing the interface is not an
error. It is the migration story, and the interface wins.

### VM1603 {#vm1603}

**Info**: *`'StampedAttribute' is marked [PerValidationInstance], so checking 'Sequence' constructs a new instance on every validation pass, passing values included - the allocation a shared instance would not cost`*

Nothing is wrong: the class asked for per-check isolation and gets it. But a clean pass otherwise
allocates nothing, and this is the one constraint cost that breaks that, so it is stated at every
site that pays it rather than only on the class that caused it.

---

## Rule class diagnostics

A `Describe` body is [read, never run](/guide/rule-classes), so a statement the generator cannot
carry has to break the build. The generated validator would otherwise check less than the
body says. Almost everything transcribes; these are the exceptions, and every one is an error.

### VM3001 {#vm3001}

**Error**: *`'PetRules.Describe' contains a TryStatement, which the generator does not transcribe`*

```csharp
public static void Describe(ValidationRules<Pet> rules, Pet x) {
    try { rules.Require(x.Name); } catch { }   // VM3001
    x.Name = "fixed";                          // VM3001: validation does not mutate its subject
    if (x.Age > 20) {
        rules.Apply(Checks.Senior);            // VM3001: Apply runs last, unconditionally
    }
}
```

The blacklist is short and v1-deliberate: `goto`, `try`/`catch`, `lock`, `using` statements,
assignment to the subject, and `Apply` anywhere but the top of the body. Locals, `if`/`else`,
`switch`, loops-as-computation, helpers and the reporter tier all transcribe.

### VM3007 {#vm3007}

**Error**: *`A rule's value argument in 'PetRules' must be a member path on the subject parameter, so the error has a field to be pathed against; anything else needs field:`*

```csharp
rules.Require(x.Name!.Trim());          // VM3007
rules.Range(x.Nights + 1, 1, 30);       // VM3007
rules.Require(x.Home?.PostalCode);      // fine: ?. is the nested-path spelling
```

The member path is what supplies the field name, taking `[JsonPropertyName]` first and then the
naming policy. Naming the error `name` for `x.Name!.Trim()` would be a guess; pass `field:` when the
value genuinely is not a path.

### VM3102 {#vm3102}

**Error**: *`The condition in 'PetRules.Describe' reads no property of the subject, so the rule has no field to report against. Anchor it by reading the property it is about, or pass field:`*

```csharp
rules.Ensure(1 < 2);                    // VM3102
rules.Ensure(1 < 2, field: "nights");   // fine: field: anchors it
rules.Ensure(x.Nights <= 7);            // fine: anchored to nights
```

An `Ensure` reports against the first property its condition reads. A condition that reads none
needs `field:`. The sanctioned case is a fragment computing over its extra parameters.

### VM3005 {#vm3005}

**Error**: *`Fragment 'SharedRules.Standard' is compiled IL from a referenced assembly; fragments must be part of this compilation - use a shared project or a source-only package`*

A [fragment](/guide/rule-classes#fragments) is expanded from syntax, and a referenced assembly
ships IL. The symbol has no body to read, so the same-compilation rule is a fact rather than a
policy, and a plain `ProjectReference` is on the wrong side of it. Share fragments through a
shared project (`.shproj` or linked `Compile` items) or a source-only package.

### VM3006 {#vm3006}

**Error**: *`Fragments may call fragments, but this chain returns to where it started: Left -> Right -> Left`*

Fragment expansion follows calls; a cycle would follow them forever. The message names the chain.

### VM3002 {#vm3002}

**Error**: *`The builder declares rules only where the generator can read them; here it would store it, capture it, return it, or pass it to anything the generator cannot read, which would validate nothing at runtime`*

```csharp
var chain = rules.Require(x.Name);                  // VM3002
Func<PropertyRules<Pet, string?>> f = () => rules.Require(x.Name);  // VM3002
Helper(rules);                                      // VM3002 unless Helper is a fragment
```

The anti-silent-drop rule. `ValidationRules<T>` is inert, so a rule call the generator cannot see
would transcribe into a call on a builder that validates nothing. Every unfollowable flow is an
error instead. A `static`, `void`, same-compilation method receiving the builder is a fragment and
is followed; everything else is this.

### VM3004 {#vm3004}

**Error**: *`'Max' is not accessible from the companion file 'ModelRules.Describe' is transcribed into. Make it internal`*

```csharp
public sealed class ModelRules : IValidationRulesFor<Model> {
    private static readonly int Max = 10;

    public static void Describe(ValidationRules<Model> rules, Model x) {
        rules.Ensure(x.Count <= Max);   // VM3004
    }
}
```

The body is transcribed into `{RulesClass}_Rules`, a companion class carrying the declaring file's
`using` directives, which is what lets `x.Status == Status.Active` resolve at all. A non-private
member is qualified automatically as `ModelRules.Max` and read as itself. A `private` one is out of
reach, so make it `internal`.

A `private const` of any type needs no change. C# bakes a constant into every use site already, so
the value is carried across as a literal with the suffix and precision that preserve both its
value and its type (`1.50m` keeps its scale, a `double` keeps all seventeen digits, an enum comes
back as a cast, and `NaN` and the infinities are named).

Inside a generic fragment this also covers a member the concrete target implements **explicitly**:
`audited.CreatedBy` binds through the constraint interface, but the emitted method's subject is
the concrete type, where an explicit implementation is not reachable by name.

### VM3003 {#vm3003}

**Error**: *`'PetRules.Describe' declares a rule inside a scope the generator cannot expand it in. Use Each for collections - a collection of strings chains element rules, Each(x.Steps).Length(5, 500) - or report per element through rules.Context`*

```csharp
foreach (var toy in x.Toys) {
    rules.Require(toy.Name);            // VM3003
}

for (var i = 0; i < x.Steps.Count; i++) {
    rules.Length(x.Steps[i], 5, 500);   // VM3003 - write rules.Each(x.Steps).Length(5, 500)
}
```

Islands need generator-computed identity, meaning a field and a rendered message, and a loop gives
them none. Collections are `Each`'s job: a collection of objects descends into the element type's
validator, and a collection of strings chains element rules with indexed paths. For anything else
per-element, loop with the [reporter tier](/guide/rule-classes#reporter) and a computed field
string.

### VM3101 {#vm3101}

**Error**: *`'x.Nights' is a non-nullable value type and can never be missing, so this rule can never fail`*

```csharp
rules.Require(x.Nights);   // VM3101, the only error on the line
```

A non-nullable value type fits none of `Require`'s typed overloads - inference does not unwrap
`Nullable`, and a dedicated non-nullable overload would collide with the reference-type one - so
an `object?` catch-all binds the spelling for exactly this diagnosis. Typed arguments never
reach it. Constrain the value instead, or make the property nullable.

### VM3105 {#vm3105}

**Error**: *`'IAudited' is validated as a facet here, but nothing in this compilation declares rules for it, so this would check nothing. Give the facet constraint attributes or a rules class`*

`rules.As<IAudited>(x)` binds to the facet's own generated validator. A facet declared in this
compilation with nothing declaring rules for it would make the `As` a silent no-op, which is the
failure this library refuses everywhere else. Declare the facet's rules in a rules class targeting
it, as `AuditRules : IValidationRulesFor<IAudited>`. That is the sound pairing, since an interface's
*attribute* constraints already reach every implementer through constraint inheritance, and an `As`
on top of those would report every facet error twice.

### VM3103 {#vm3103}

**Info**: *`This rule reports code 'start_less_than_end', derived from 'start < end.'. Pass code: to pin it against a change to the condition`*

An `Ensure` without an explicit `code:`, stating what it derived.

Info because there is nothing to fix. Deriving a code is the designed behaviour rather than a
fallback, so a warning would imply the author erred by not passing `code:`. The diagnostic exists
because a derived code is the one part of a rules class you cannot read off the source, and it is
worth seeing where the rule is written. Silent when `code:` was passed, since the code is then in
the source already.

[Error codes](/reference/codes#why-ensure-derives-its-code) has the derivation and the operator
spellings.

### VM3104 {#vm3104}

**Warning**: *`'x.BatteryKwh.Value' unwraps a nullable member. The rule takes the nullable directly, and the field path is derived from the member - write 'x.BatteryKwh'`*

```csharp
rules.Range(x.BatteryKwh.Value, 10, 300);   // VM3104 - write x.BatteryKwh
```

Every rule takes the nullable directly, so the unwrap is never needed - and without this
correction it was never harmless: the derived field path kept the `.Value` hop, so the wire
carried `batteryKwh.value` and the composed message named `value`.

The reader corrects the rule - it compiles against the member itself, guard, path and all - and
warns rather than erroring, because failing a build over a mistake it just fixed would be spite.
The source should still drop the `.Value` so it says what is generated. See
[nullable members in rule classes](/guide/rule-classes#the-vocabulary).

### VM1010 {#vm1010}

**Error**: *`'Envelope' is generic, and a validator for it could not be registered`*

```csharp
public sealed record Envelope<T> {      // VM1010
    [Required] public string? TraceId { get; init; }
    public T? Payload { get; init; }
}
```

The validator class itself would be fine, since `EnvelopeValidator<T> : IValidatorFor<Envelope<T>>`
is ordinary C#. Registering it is not. A container's open-generic support matches `Foo<>` to `Bar<>`,
and here the type parameter sits *inside* another construction, so `IValidatorFor<Envelope<T>>` has
no open form to register. Closing it per construction needs `MakeGenericType`, which this library
does not use anywhere.

Declare the constraints on a closed type:

```csharp
public sealed record OrderEnvelope {
    [Required] public string? TraceId { get; init; }
    [ValidateNested] public Order? Payload { get; init; }
}
```

Or leave the envelope unconstrained and validate the payload on its own. `IValidatorFor<Order>` is
resolvable, and a handler that already has the payload in hand rarely needs the wrapper validated.

::: tip Why this is an error rather than a silent skip
Emitting the validator and omitting it from `AddXValidators()` was the alternative. Resolving
`IValidatorFor<Envelope<Order>>` would then find nothing and the value would go unvalidated while
every other constraint still reported, which reads exactly like validation working. Before this
diagnostic existed the generator emitted a *non-generic* validator referencing `T`, so the build
failed with several CS0246 inside a generated file and nothing pointing at the cause.
:::

### VM1502 {#vm1502}

**Warning**: *`'System.Collections.Generic.List<Section>' is not a type a validator can be generated for, so [ValidateNested] on 'Sections' is dropped; model the inner collection as a property of a type that declares its own rules`*

The descent's target can never carry a generated validator: a constructed generic like the
`List<Section>` element of a `List<List<Section>>`, an array element like `Section[]`, or a
nullable element like `Money?`. Validators are named `<Type>Validator` over plain declared types,
so there is no class this descent could call, and the descent is dropped.

```csharp
public sealed record Document {
    [ValidateNested] // VM1502
    public List<List<Section>> Sections { get; init; } = [];
}
```

The fix is the remodelling [nesting](/guide/nesting#collections-of-collections) recommends: give
the inner collection a type of its own, and nest that.

Distinct from [VM1501](#vm1501), which is about a plain type that merely has no rules *yet*. This
target could never have any.

### VM5002 {#vm5002}

**Error**: *`Emitting the validator for 'Sample.Batch' threw ArgumentException: …. The build is failed so the missing generated source cannot ship silently; please report this`*

An emit stage threw an unhandled exception. Roslyn's own answer to that is a `CS8785` *warning*
with every source the stage would have added dropped - and in a class library holding only models,
nothing references a generated symbol, so the build succeeds with zero validators and every model
silently validates nothing. This diagnostic is the backstop that turns any such defect into a
build failure naming what was being emitted.

There is nothing to fix in your code; the message exists to be pasted into an issue. Every other
validator in the compilation is still generated.

### VM5003 {#vm5003}

**Warning**: *`'Coupon' has no constraints, no [GenerateValidator], and no rules class or hand-written validator in this compilation, so .Validate<Coupon>() will fail when the endpoint is built. Add constraints or [GenerateValidator] - or, if its rules arrive from another assembly, ignore this and the startup check will agree`*

```csharp
app.MapPost("/coupons", (Coupon c) => Results.Ok())
   .Validate<Coupon>();   // VM5003 when Coupon declares no rules here
```

The build-time version of the [endpoint filter's startup check](/guide/aspnetcore): the same
verdict, delivered where the call was written instead of when the endpoint is built. A
`List<T>` or `T[]` argument is judged by its element, since a collection body validates
element-wise.

A **warning** for the cross-assembly reason [VM1501](#vm1501) set: the type may get its validator
from a rules class in another assembly, which this compilation cannot see. The startup check
remains the authority; this is the earlier, cheaper signal.
