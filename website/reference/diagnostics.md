# Diagnostics

Every diagnostic this generator reports, why it exists, and what to do about it.

All of them are in category `ValidationModules.Usage`, so a blanket `.editorconfig` rule reaches the
whole set:

```ini
[*.cs]
dotnet_diagnostic.VM0004.severity = none
dotnet_analyzer_diagnostic.category-ValidationModules.Usage.severity = suggestion
```

Prefer silencing one id over the category. Several are errors because the alternative is generated
code that does not compile.

::: warning One of these never fires
[VM0007](#vm0007) is declared and released, and nothing in the product reports it. It is documented
here as declared and marked **not reported**, because a rule you expect to catch a mistake and which
silently does not is worse than one you know is missing.
:::

## Summary

| ID | Severity | |
|---|---|---|
| [VM0001](#vm0001) | Error | a string constraint on a non-string |
| [VM0002](#vm0002) | Error | `[ItemCount]` on a non-collection |
| [VM0003](#vm0003) | Error | `[Range]` on a type with no ordering |
| [VM0004](#vm0004) | Warning | `[Required]` on a non-nullable value type |
| [VM0006](#vm0006) | Error | a pattern that is not a valid regex |
| [VM0007](#vm0007) | Warning | `[ValidateNested]` target has no rules — **not reported** |
| [VM0008](#vm0008) | Error | lower bound exceeds upper bound |
| [VM0009](#vm0009) | Error | constrained property has no accessible getter |
| [VM0010](#vm0010) | Warning | a DataAnnotations constraint was skipped |
| [VM0016](#vm0016) | Warning | `RegexOptions.Compiled` is not meaningful |
| [VM0017](#vm0017) | *policy* | an inline pattern roots the regex engine |
| [VM0018](#vm0018) | Error | referenced regex member is unusable |
| [VM0019](#vm0019) | Error | profile attribution is declared, and profiles are not implemented |
| [VM0040](#vm0040) | Error | `ValidationModules.Runtime` is too old |
| [VM0051](#vm0051) | Warning | constraint on a record parameter without `property:` |
| [VM0060](#vm0060) | Warning | a custom `ValidationAttribute` is not compiled |
| [VM0061](#vm0061) | Warning | a cross-field DataAnnotations attribute is not compiled |
| [VM0063](#vm0063) | Warning | a format DataAnnotations attribute is not compiled |
| [VM0064](#vm0064) | Error | a length constraint on neither a string nor a collection |
| [VM0065](#vm0065) | Error | `[Range]` bounds do not parse as the member's type |
| [VM0067](#vm0067) | Warning | `IValidatableObject` is not called |
| [VM0070](#vm0070) | Error | a statement in `Describe` is not a rule declaration |
| [VM0071](#vm0071) | Error | a rule selector is not a property path |
| [VM0072](#vm0072) | Error | a predicate captures state |
| [VM0075](#vm0075) | Error | an `Ensure` has no field |

---

## Constraint diagnostics

### VM0001 {#vm0001}

**Error** — *`'[StringLength]' applies to strings; 'Age' is 'int'`*

A string constraint — `[StringLength]` or `[Pattern]` — on a member that is not a `string`.

```csharp
[StringLength(1, 10)] // VM0001
public int Age { get; init; }

[Pattern("^a$")] // VM0001
public int Age { get; init; }
```

You probably wanted `[Range]` for a number, or `[ItemCount]` for a collection.

### VM0002 {#vm0002}

**Error** — *`[ItemCount] applies to collections; 'Name' is 'string'`*

```csharp
[ItemCount(1, 10)] // VM0002
public string? Name { get; init; }
```

A `string` is deliberately **not** a collection here, even though it implements `IEnumerable<char>`.
Taking that reading would turn a length constraint into a per-character walk, so this is VM0002 and
`[StringLength]` is what you wanted.

### VM0003 {#vm0003}

**Error** — *`[Range] applies to numeric and date types; 'Name' is 'string'`*

`[Range]` emits a pair of comparisons, so the member's type has to support them. Integral,
floating-point, `decimal`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly` and `TimeSpan` all
qualify, as do their nullable forms.

### VM0004 {#vm0004}

**Warning** — *`'Age' is a non-nullable value type, so it is always present and [Required] can never fail`*

```csharp
[Required] // VM0004
public int Age { get; init; }
```

A warning rather than an error: the declaration is harmless, just inert. Making it an error would
break a build over a no-op.

Use `int?` if the value is genuinely optional, or `[Range]` if what you meant was "not zero".

### VM0006 {#vm0006}

**Error** — *`The pattern on 'Sku' is not a valid regular expression: …`*

The message is the regex engine's own. Re-describing it would produce something worse than what the
parser already says.

### VM0007 {#vm0007}

**Warning** — *`'Address' declares no constraints and no [GenerateValidator], so [ValidateNested] on 'Home' validates nothing`*

::: danger Not reported
This descriptor is declared and released, and nothing constructs it. `[ValidateNested]` on a type
with no rules is currently silent — no validator exists for the nested type, so the descent is
skipped and nothing tells you.
:::

Mark the nested type `[GenerateValidator]` when its rules arrive from a
[rule class](/guide/rule-classes) rather than from its own attributes.

### VM0008 {#vm0008}

**Error** — *`The bounds on 'Name' are inverted, so the constraint can never be satisfied`*

```csharp
[StringLength(10, 1)] // VM0008
public string? Name { get; init; }
```

Applies to `[StringLength]` and `[ItemCount]`. Equal bounds are fine — `[StringLength(2, 2)]` is an
exact length.

### VM0009 {#vm0009}

**Error** — *`'Name' has no accessible getter, so its constraints cannot be evaluated`*

```csharp
[Required] // VM0009
public string? Name { set { } }

[Required] // VM0009
public string? Name { private get; set; }
```

`internal` is fine — the generated validator lands in the same assembly.

The unreadable property is dropped rather than emitted anyway, so the build fails on VM0009 alone
and not also on generated code that will not compile. Every other constraint on the type still
applies.

### VM0016 {#vm0016}

**Warning** — *`Patterns compile through [GeneratedRegex]; RegexOptions.Compiled on 'Sku' is ignored`*

```csharp
[Pattern("^a$", Options = RegexOptions.Compiled)] // VM0016
```

`RegexOptions.Compiled` emits IL through `Reflection.Emit`, which is the habit this library exists to
remove — and it does nothing here regardless. Other `RegexOptions` values are honoured.

### VM0017 {#vm0017}

**Severity depends on policy** — *`The pattern on 'Sku' is built from a string at run time, which roots the regex parser and interpreter …`*

An inline `[Pattern("…")]` in an AOT-facing project. Constructing a `Regex` from a string means the
parser and interpreter must be in the binary — about 450 KB, once.

```csharp
[Pattern("^[A-Z]{3}$")] // VM0017 under AOT
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

### VM0018 {#vm0018}

**Error** — *`'PetPatterns.Sku' is not static, so the pattern on 'Sku' cannot be emitted`*

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

### VM0019 {#vm0019}

**Error** — *`'Required' declares a profile on 'Tag', and profiles are not implemented — profile arguments are ignored, so every rule is enforced in every profile including the ones it excludes`*

```csharp
[Required(FromProfile = typeof(V2))] // VM0019
public string? Tag { get; init; }
```

Also fires on assembly-level `[DefaultValidationProfile]`.

Profiles are Stage 3 of the plan and are not built. The declaration surface shipped ahead of the
implementation — `ValidationConstraintAttribute` carries `FromProfile`, `UntilProfile` and
`Profiles`, and `IValidationProfile` exists in the runtime — so the code compiles, reads exactly as
the design describes, and does something else.

**An error rather than a warning, because of which way it fails.** A rule that never fires costs a
caller nothing. This is the opposite: a rule written to apply only from V2 is enforced under V1 as
well, rejecting data the caller was entitled to send. A warning is a thing a build ships with.

Remove the profile argument until the feature lands. Declaring `IValidationProfile` types is
harmless; attaching a rule to one is what does not work.

::: tip Why not VM0011–VM0015
Plan §11 reserves those for profile *semantics* — a profile argument that is not a profile, a range
that admits nothing, a cyclic chain. Those describe a feature that exists. This one says it does
not, so it sits with VM0016–VM0018, the "this does not do what you think" family.
:::

### VM0040 {#vm0040}

**Error** — *`The generated validators require ValidationModules.Runtime contract N or later; the referenced runtime is contract M`*

Version lockstep. The generator emits calls against a runtime surface, and a runtime older than that
surface would fail *inside generated code* — the worst place for an error to land. So the check runs
before any source is added and the build fails here instead.

Update the `ValidationModules.Runtime` package reference to match the generator.

### VM0051 {#vm0051}

**Warning** — *`'Required' is on a record parameter without the property: target, so it lands on the parameter and is never evaluated. Write [property: Required]`*

```csharp
public record Pet([Required] string Name);              // VM0051
public record Pet([property: Required] string Name);    // correct
```

Without this the failure is silent in every direction. The attribute binds to the primary
constructor's parameter, so the generated property carries no metadata, the type looks entirely
unconstrained, and **no validator is emitted at all** — not an empty one. Nothing is registered, so
`IValidatorFor<Pet>` does not resolve and a `ValidationRunner<Pet>` merging zero validators reports
every value as valid.

Reported before any property is read, precisely because the situation is one where no property
carries anything. One diagnostic per attribute, since each has to be fixed. The diagnostic is the
whole output — no empty validator is emitted alongside it.

Scoped to the **primary** constructor. A constraint on an ordinary constructor's parameter is
equally inert, but `[property:]` is not legal there, so this advice would be wrong.

Write `[property: Required]`, or use a record with an explicit body:

```csharp
public record Pet {
    [Required]
    public string? Name { get; init; }
}
```

---

## DataAnnotations diagnostics

### VM0010 {#vm0010}

**Warning** — *`'RequiredAttribute' on 'Name' is a DataAnnotations constraint and ValidationModules_DataAnnotations is set to Ignore, so it is not enforced`*

Reported once per skipped constraint, so switching the front end off cannot silently unvalidate a
model. Remove the `Ignore` setting, or move the rules to
[native constraints](/guide/constraints).

### VM0060 {#vm0060}

**Warning** — *`'EvenNumberAttribute' on 'Age' derives from ValidationAttribute and carries arbitrary code, which cannot be compiled`*

A custom `ValidationAttribute` subclass, or `[CustomValidation]`. Its `IsValid` is arbitrary code
that only exists at run time; compiling it would mean invoking it, which is the reflection this
library exists to avoid.

Move the rule to a [rule class](/guide/rule-classes) or an
[`IAsyncValidatorFor<T>`](/guide/async).

### VM0061 {#vm0061}

**Warning** — *`'CompareAttribute' on 'Confirm' compares against another member, which a per-property constraint cannot express`*

```csharp
[Compare(nameof(Password))] // VM0061
public string? Confirm { get; set; }
```

Use `rules.Ensure`, which is the declaration form that *can* span two properties:

```csharp
rules.Ensure(x => x.Password == x.Confirm, code: "password_mismatch");
```

### VM0063 {#vm0063}

**Warning** — *`'EmailAddressAttribute' on 'Email' is not enforced. Its DataAnnotations implementation is more lenient than most callers expect; declare a [Pattern] whose behaviour is visible in your own source`*

Covers `[EmailAddress]`, `[Phone]`, `[Url]`, `[CreditCard]`, `[Base64String]` and
`[FileExtensions]`.

These are skipped rather than approximated on purpose. `EmailAddressAttribute` accepts anything with
exactly one `@` not at either end — `a@b` passes — which is far more lenient than almost anyone
declaring it believes they asked for. Reproducing that faithfully ships a surprise; reproducing it
strictly changes what an existing model means.

### VM0064 {#vm0064}

**Error** — *`'MinLengthAttribute' applies to strings and collections; 'Age' is 'int'`*

`[MinLength]`, `[MaxLength]` and `[Length]` apply to both strings and collections in DataAnnotations,
so the member's type decides which constraint each becomes. A member that is neither has no reading.

### VM0065 {#vm0065}

**Error** — *`The bounds on 'Born' do not parse as 'System.DateOnly'`*

```csharp
[Range("not-a-date", "2100-01-01")] // VM0065
public DateOnly Born { get; init; }

[Range("abc", "def")] // VM0065
public decimal Price { get; init; }

[Range("2000-01-01", "2100-01-01")] // VM0065
public int Age { get; init; }
```

A bound written as a string is parsed against the member's own type at generation time — which is
what `RangeAttribute`'s documentation has always promised — and emitted as a constructor call rather
than a quoted literal. A bound that does not parse is reported here, at the declaration, rather than
becoming a comparison between a `DateOnly` and a `string` inside a generated file.

The constraint is dropped when its bounds do not parse, so the build fails on VM0065 alone and not
also on generated code that will not compile.

`[Range]` on a member with no ordering at all is [VM0003](#vm0003), which fires first — saying it
twice would be worse than saying it once.

::: tip What the emitted bound looks like
`[Range("2000-01-15", "2100-12-31")]` on a `DateOnly` becomes
`new global::System.DateOnly(2000, 1, 15)` and `new global::System.DateOnly(2100, 12, 31)`, in both
the comparison and the message — so the two cannot disagree about what the bound is. A `DateTime`
bound is `DateTimeKind.Unspecified`: a bound written `"2000-01-01"` carries no zone, and anchoring it
to whatever the build machine happened to be in would make the same source mean two things.
:::

### VM0067 {#vm0067}

**Warning** — *`'Customer' implements IValidatableObject; its Validate method is not called by the generated validator`*

The type is still validated for everything else it declares. Dropping it entirely would be a worse
answer than validating what can be validated and saying what was left out.

---

## Rule class diagnostics

These are errors rather than warnings, and for a reason particular to
[rule classes](/guide/rule-classes): a `Describe` body compiles and *runs*. Under
`DescribedValidator<T>` a statement the generator cannot read would work perfectly. Quietly skipping
it would produce two engines that disagree.

### VM0070 {#vm0070}

**Error** — *`Only rule declarations on the builder are allowed in 'PetRules.Describe'; this statement is not one and is not compiled`*

```csharp
public void Describe(ValidationRules<Pet> rules) {
    var minimum = 2;                          // VM0070
    if (Environment.IsProduction) { … }       // VM0070
    foreach (var name in Names) { … }         // VM0070
    Helper();                                 // VM0070
}
```

The body is a whitelisted DSL, not general C#. Move the computation outside `Describe`, or express
the rule with `rules.Ensure` / `rules.Apply`.

### VM0071 {#vm0071}

**Error** — *`A rule selector in 'PetRules.Describe' must read a property of its parameter, so the error has a field to be pathed against`*

```csharp
rules.Required(x => x.Name!.Trim());          // VM0071
rules.Range(x => x.Nights + 1, 1, 30);        // VM0071
```

The selector's source text is what supplies the field name. Naming the error `name` for
`x => x.Name!.Trim()` would be a guess.

### VM0072 {#vm0072}

**Error** — *`A predicate in 'PetRules.Describe' may read only its own parameter and static or constant state; this one captures something else and cannot be compiled`*

```csharp
private readonly int _limit = 7;

rules.Ensure(x => x.Nights <= _limit);        // VM0072
rules.Ensure(x => x.Nights <= Limit);         // fine — const or static
```

The generator lifts a predicate into a static method; the runtime holds it as a delegate. A delegate
can close over the rules class instance and a static method cannot, so a capture is the one construct
that would genuinely compile on one path and not the other.

### VM0075 {#vm0075}

**Error** — *`The predicate in 'PetRules.Describe' reads no property of its parameter, so the rule has no property to be anchored to. Rewrite it to read the property it is about; field: renames the error but does not anchor the rule, so passing it does not resolve this`*

```csharp
rules.Ensure(x => true);                      // VM0075
rules.Ensure(x => true, field: "nights");     // VM0075 as well
```

A rule is emitted inside its anchored property's chain so both engines agree on ordering, and a rule
belonging to no property has nowhere to go. `field:` renames the error; it does not anchor the rule.

Write a predicate that reads the property the rule is about:

```csharp
rules.Ensure(x => x.Nights <= 7);
```

::: tip Where the two engines diverge
This is the one place they legitimately do: `DescribedValidator<T>` accepts `Ensure(x => true,
field: "nights")` and runs it, because an explicit field means it never consults the anchor. The
generated path is the stricter of the two, which is the safe direction — the build fails rather than
two deployments disagreeing.
:::
