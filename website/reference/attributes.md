# Attributes

This page describes each attribute in the `ValidationModules.Constraints` namespace. For how the
attributes fit together, see [Constraint attributes](../guide/constraints).

In the messages below, `{field}` is the last segment of the error's field path. Numbers and dates in
a message are formatted with the invariant culture.

## Properties every constraint has

Every constraint attribute derives from `ValidationConstraintAttribute`, which has four properties:

| Property | Effect |
| --- | --- |
| `Code` | Replaces the error code. The default message is kept. |
| `Message` | Replaces the message with literal text. `{field}` is the only placeholder. On a built-in attribute or a `CustomConstraintAttribute`, the text is authored, so language packs do not replace it. |
| `When` | The name of a member of the model. The constraint applies only when it is `true`. |
| `Unless` | The name of a member of the model. The constraint applies only when it is `false`. |

`When` and `Unless` accept a `bool` property, a parameterless method that returns `bool`, or a
static method that takes the model and returns `bool`. A constraint cannot set both.

`ValidationModules_CodeNamespace` adds a prefix to a code set with `Code` on a built-in attribute, a
`CustomConstraintAttribute` or an attribute that implements `IConstraintFor<T>`, as in
`myapp.weight_out_of_range`. Built-in codes are never prefixed.

Every constraint except `[Required]` passes a `null` value. When `[Required]` fails, the other
constraints on the property are skipped.

## Presence

### [Required]

```csharp
public RequiredAttribute();

public bool AllowEmptyStrings { get; init; }
```

`[Required]` passes when the value is not `null`. On a `string`, the value must also not be empty or
whitespace, unless `AllowEmptyStrings` is `true`. On a collection only `null` fails.

| Code | Message |
| --- | --- |
| `required` | `{field} is required.` |

On a property of a non-nullable value type, such as `int`, `[Required]` can never fail. The
generator reports `VM1201` and drops it.

## Strings

### [StringLength]

```csharp
public StringLengthAttribute();

public StringLengthAttribute(int min = 0, int max = int.MaxValue);

public int Min { get; init; }
public int Max { get; init; }
```

`[StringLength]` passes when the length of the string is between `Min` and `Max`, inclusive. Applies
to `string` only. The length is `string.Length`, which counts UTF-16 code units, so a character
outside the Basic Multilingual Plane, such as most emoji, counts as two.

::: warning
The first constructor argument is the minimum. `[StringLength(50)]` means at least 50 characters.
Write `[StringLength(max: 50)]` for a maximum.
:::

| Code | Message |
| --- | --- |
| `string_length` | `{field} must be between {0} and {1} characters.` |
| `string_length` | `{field} must be at least {0} characters.` when only `Min` is set |
| `string_length` | `{field} must be at most {0} characters.` when only `Max` is set |

When the deciding bound is 1, the message says `character`. Diagnostics: `VM1001` on a property that
is not a `string`, and `VM1101` when `Min` is greater than `Max`.

### [Pattern]

```csharp
public PatternAttribute(string pattern);

public PatternAttribute(Type regexProvider, string regexMember);

public RegexOptions Options { get; init; }
public int MatchTimeoutMilliseconds { get; init; }
```

`[Pattern]` passes when the regular expression matches the string. The match can be anywhere in the
value, so anchor the expression with `^` and `$` to match all of it. Applies to `string` only.

The first constructor takes the expression. The second names a static member of type `Regex` on
another type, usually a `[GeneratedRegex]` method. `Options` and `MatchTimeoutMilliseconds` apply
to the first form only. See [Patterns](../guide/patterns).

| Code | Message |
| --- | --- |
| `pattern` | `{field} is not in the required format.` |

Diagnostics: `VM1001` on a property that is not a `string`, `VM1106` when the expression does not
parse, `VM1107` when the referenced member cannot be used, `VM1301` for an inline expression under
the pattern policy, and `VM1302` when `Options` includes `RegexOptions.Compiled`.

### [EmailAddress]

```csharp
public EmailAddressAttribute();
```

`[EmailAddress]` passes when the string contains exactly one `@`, which is neither the first nor the
last character, and no line breaks. This is the same rule as the DataAnnotations attribute. It
accepts `a@b`.

| Code | Message |
| --- | --- |
| `email` | `{field} is not a valid email address.` |

### [Phone]

```csharp
public PhoneAttribute();
```

`[Phone]` applies the DataAnnotations phone rule. `+` signs and a trailing extension such as `ext.
12` or `x12` are removed, and the rest must contain a digit and only digits, whitespace, `-`, `.`,
`(` and `)`.

| Code | Message |
| --- | --- |
| `phone` | `{field} is not a valid phone number.` |

### [Url]

```csharp
public UrlAttribute();
```

On a `string`, `[Url]` passes when the value starts with `http://`, `https://` or `ftp://`, ignoring
case. The rest of the value is not checked. On a `Uri`, passes when the URI is absolute and its
scheme is http, https or ftp.

| Code | Message |
| --- | --- |
| `url` | `{field} is not a valid http, https or ftp URL.` |

### [CreditCard]

```csharp
public CreditCardAttribute();
```

`[CreditCard]` passes when the digits pass the Luhn checksum. Spaces and dashes are ignored, and any
other character fails. An empty string passes, so combine it with `[Required]`.

| Code | Message |
| --- | --- |
| `credit_card` | `{field} is not a valid credit card number.` |

### [Base64String]

```csharp
public Base64StringAttribute();
```

`[Base64String]` passes when the string is valid Base64. Whitespace is allowed.

| Code | Message |
| --- | --- |
| `base64` | `{field} is not a valid Base64 string.` |

### [FileExtensions]

```csharp
public FileExtensionsAttribute();

public string? Extensions { get; init; }
```

`[FileExtensions]` passes when the file name's extension is in `Extensions`, a comma-separated list
such as `"pdf,docx"`. The comparison ignores case. The default list is `png,jpg,jpeg,gif`. Spaces
and dots in the list are removed, so `tar.gz` is read as `targz`.

| Code | Message |
| --- | --- |
| `file_extension` | `{field} must have one of these file extensions: {0}.` with the list, as in `.pdf, .docx` |

## Numbers, dates and times

### [Range]

```csharp
public RangeAttribute();

public RangeAttribute(int min, int max);

public RangeAttribute(long min, long max);

public RangeAttribute(double min, double max);

public RangeAttribute(string min, string max);

public object? Min { get; init; }
public object? Max { get; init; }
public bool ExclusiveMin { get; init; }
public bool ExclusiveMax { get; init; }
```

`[Range]` passes when the value is between `Min` and `Max`. Both bounds are inclusive unless
`ExclusiveMin` or `ExclusiveMax` is set, and either can be left out. Applies to the integral types,
`float`, `double`, `decimal`, `DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan`, `DateTimeOffset`, and
their nullable forms.

The bounds are converted to the property's type at build time. String bounds are parsed with the
invariant culture, as in `[Range("2024-01-01", "2030-12-31")]` on a `DateOnly`. Write `DateTime` and
`DateOnly` bounds without a time zone. Give `DateTimeOffset` bounds an explicit offset, as in
`2024-01-01T00:00:00+00:00`. A `DateTimeOffset` bound without one takes the offset of the machine
that builds the project.

| Code | Message |
| --- | --- |
| `range` | `{field} must be between {0} and {1}.` |
| `range` | `{field} must be greater than {0} and at most {1}.` with `ExclusiveMin` |
| `range` | `{field} must be at least {0} and less than {1}.` with `ExclusiveMax` |
| `range` | `{field} must be greater than {0} and less than {1}.` with both |
| `range` | `{field} must be at least {0}.` or `{field} must be greater than {0}.` with only `Min` |
| `range` | `{field} must be at most {0}.` or `{field} must be less than {0}.` with only `Max` |

Diagnostics: `VM1003` on a type with no ordering, `VM1102` when neither bound is set, and `VM1103`
when a bound does not parse as the property's type. No diagnostic catches bounds in the wrong
order, and such a constraint always fails.

### [MultipleOf]

```csharp
public MultipleOfAttribute(int divisor);

public MultipleOfAttribute(long divisor);

public MultipleOfAttribute(double divisor);

public MultipleOfAttribute(string divisor);

public object Divisor { get; }
```

`[MultipleOf]` passes when the value divides by the divisor with no remainder. Applies to the
integral types, `decimal`, `double` and `float`. For `double` and `float`, the check converts the
value to `decimal` first, so `0.3` is a multiple of `0.1`, and a value too large for `decimal`
fails. On an integral property the divisor must be a whole number. Use the `string` constructor for
an exact decimal divisor on a `decimal` property, as in `[MultipleOf("0.05")]`.

| Code | Message |
| --- | --- |
| `multiple_of` | `{field} must be a multiple of {0}.` |

Diagnostics: `VM1004` on a type that is not numeric, `VM1104` when the divisor is zero or negative,
and `VM1105` when it does not fit the property's type.

## Values and enums

### [AllowedValues]

```csharp
public AllowedValuesAttribute(params object[] values);

public object[] Values { get; }
public StringComparison Comparison { get; init; }
```

`[AllowedValues]` passes when the value equals one of `Values`. On an enum property the values can
be enum members. Strings are compared ordinally and case-sensitively. `Comparison` is accepted but
not applied in this version.

| Code | Message |
| --- | --- |
| `enum` | `{field} must be one of: {0}.` with the values, as in `active, pending` |

### [DeniedValues]

```csharp
public DeniedValuesAttribute(params object[] values);

public object[] Values { get; }
```

`[DeniedValues]` passes when the value equals none of `Values`.

| Code | Message |
| --- | --- |
| `enum` | `{field} must not be one of: {0}.` |

### [EnumDefined]

```csharp
public EnumDefinedAttribute();
```

`[EnumDefined]` passes when the value is a member the enum declares. On a `[Flags]` enum, passes
when the value is a combination of declared flags, and `0` always passes. The check compares against
the members known at build time and does not call `Enum.IsDefined`.

| Code | Message |
| --- | --- |
| `enum` | `{field} must be one of: {0}.` with the member names |
| `enum` | `{field} must be a combination of: {0}.` for a `[Flags]` enum |

Diagnostics: `VM1006` on a property that is not an enum, or an enum with no members.

## Collections

### [ItemCount]

```csharp
public ItemCountAttribute();

public ItemCountAttribute(int min = 0, int max = int.MaxValue);

public int Min { get; init; }
public int Max { get; init; }
```

`[ItemCount]` passes when the number of items is between `Min` and `Max`, inclusive. The first
argument is the minimum. Applies to arrays and to collections with a `Count` property, including
dictionaries.

| Code | Message |
| --- | --- |
| `array_bounds` | `{field} must be between {0} and {1} items.` |
| `array_bounds` | `{field} must be at least {0} items.` when only `Min` is set |
| `array_bounds` | `{field} must be at most {0} items.` when only `Max` is set |

When the deciding bound is 1, the message says `item`. Diagnostics: `VM1002` on a property that is
not a collection, and `VM1101` when `Min` is greater than `Max`.

### [UniqueItems]

```csharp
public UniqueItemsAttribute();
```

`[UniqueItems]` passes when no item appears twice, compared with the element type's default
equality. Strings are compared ordinally. Applies to arrays and collections.

| Code | Message |
| --- | --- |
| `unique_items` | `{field} must not contain duplicate items.` |

Diagnostics: `VM1005` on a property that is not a collection, and `VM1202` when the element type
compares by reference, so that two items with equal contents both pass. Make the element a record,
override `Equals`, or implement `IEquatable<T>`.

## Nesting

### [ValidateNested]

```csharp
public ValidateNestedAttribute();

public ValidateNestedAttribute(Polymorphism polymorphism);

public Polymorphism Polymorphism { get; }
```

`[ValidateNested]` runs the validators for the property's type on its value, or on every element of
a collection, or on every value of a dictionary. Errors are reported under the property's path, as
in `shipTo.postcode`, `lines[1].sku` or `addresses[work].postcode`. A `null` value is skipped.
`When` and `Unless` decide whether the descent happens.

`Polymorphism` chooses the validators when the value can be of a derived type:

| Value | Validators |
| --- | --- |
| `Polymorphism.DeclaredOnly` | The declared type's validators only. |
| `Polymorphism.CompileTime` | The validators of the value's actual type, chosen from the subtypes in the same project. |
| `Polymorphism.Runtime` | The validators registered in the container for the value's actual type. The pass needs a service provider. |

Diagnostics: `VM1501` when the type has no rules, `VM1502` when no validator can exist for the type,
`VM1503` when the type is not sealed and no `Polymorphism` is given, and `VM1504` for `Runtime` on a
sealed or value type. See [Nested objects and collections](../guide/nesting).

## Types

### [GenerateValidator]

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
public GenerateValidatorAttribute();
```

`[GenerateValidator]` makes the generator write a validator for a type that has no constraints of
its own. When the type has no rules from anywhere else, such as a rules class, the validator passes
every value. Use it for a type that needs a registered `IValidatorFor<T>`, such as the target of
`[ValidateNested]`, of `.Validate<T>()` in ASP.NET Core, or of hand-written rules.

### [PerValidationInstance]

```csharp
[AttributeUsage(AttributeTargets.Class)]
public PerValidationInstanceAttribute();
```

`[PerValidationInstance]` goes on an attribute class that implements `IConstraintFor<T>`. The
generator then creates a new instance of the attribute for every check, instead of one shared
instance. Each use is reported as `VM1603`. See [Custom
constraints](../guide/custom-constraints#a-check-with-state).

## Base classes

`ValidationConstraintAttribute` is the base class of every constraint attribute, and the source of
the four properties at the top of this page.

`CustomConstraintAttribute` is the base class for a constraint with a static `IsValid` method. See
[Custom constraints](../guide/custom-constraints#a-static-check).
