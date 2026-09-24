# Messages and languages

Every error has a default English message. You can set the text for one rule, replace the text for
a code with a formatter, or translate every message with a language pack. The error's `Code` stays
the same in every case.

## Default messages

The default messages come from templates in `ValidationMessageTemplates`. `{field}` is the last
segment of the error's field path, and `{0}` and `{1}` are the rule's arguments, formatted with the
invariant culture.

| Shape key | Template |
| --- | --- |
| `required` | `{field} is required.` |
| `string_length.between` | `{field} must be between {0} and {1} characters.` |
| `string_length.at_least` | `{field} must be at least {0} characters.` |
| `string_length.at_most` | `{field} must be at most {0} characters.` |
| `array_bounds.between` | `{field} must be between {0} and {1} items.` |
| `array_bounds.at_least` | `{field} must be at least {0} items.` |
| `array_bounds.at_most` | `{field} must be at most {0} items.` |
| `range.between` | `{field} must be between {0} and {1}.` |
| `range.greater_and_at_most` | `{field} must be greater than {0} and at most {1}.` |
| `range.at_least_and_less` | `{field} must be at least {0} and less than {1}.` |
| `range.greater_and_less` | `{field} must be greater than {0} and less than {1}.` |
| `range.at_least` | `{field} must be at least {0}.` |
| `range.greater_than` | `{field} must be greater than {0}.` |
| `range.at_most` | `{field} must be at most {0}.` |
| `range.less_than` | `{field} must be less than {0}.` |
| `multiple_of` | `{field} must be a multiple of {0}.` |
| `unique_items` | `{field} must not contain duplicate items.` |
| `pattern` | `{field} is not in the required format.` |
| `enum` | `{field} must be one of: {0}.` |
| `enum.denied` | `{field} must not be one of: {0}.` |
| `enum.flags` | `{field} must be a combination of: {0}.` |
| `email` | `{field} is not a valid email address.` |
| `phone` | `{field} is not a valid phone number.` |
| `url` | `{field} is not a valid http, https or ftp URL.` |
| `credit_card` | `{field} is not a valid credit card number.` |
| `base64` | `{field} is not a valid Base64 string.` |
| `file_extension` | `{field} must have one of these file extensions: {0}.` |
| `custom` | `{field} is invalid.` |

The length and count shapes also have a `_singular` form, such as `string_length.at_most_singular`,
used when the deciding bound is 1: `code must be at most 1 character.` That makes 34 shape keys in
all. `ValidationMessageTemplates.KnownKeys` lists them, and `ValidationMessageTemplates.KeyOf`
returns the shape key of a template.

An `Ensure` in a rules class has no template. Its default message is the text of its condition.

## Set the text for one rule

`Message` on a constraint attribute, and `message:` on `Ensure`, replace the default text for that
one rule:

```csharp
[StringLength(3, 3, Message = "The code has three letters.")]
public string? Code { get; init; }
```

This is authored text. `MessageIsAuthored` is `true` on the error, and language packs leave it
unchanged. See [Authored messages](#authored-messages).

## Formatters

`error.Message` always returns the default English text. To show other text, pass the error to a
`ValidationMessageFormatter`:

```csharp
string text = error.ToMessage(formatter);
```

`ValidationMessageMap` is a formatter that maps codes to text. A code with no mapping keeps its
default message:

```csharp
var messages = new ValidationMessageMap()
    .Map(ValidationCodes.Required, static (in ValidationError e) => $"Please enter the {e.Field}.")
    .Map(ValidationCodes.Range, static (in ValidationError e) => $"{e.Value} is out of range.");
```

Each mapping is a `ValidationMessageMap.MessageRenderer`, which takes the error by `in` reference,
so a lambda needs the `in` modifier. A formatter is the only way for the captured `Value` to reach a
message. The map replaces authored text as well, for the codes it maps. A map has no notion of
culture. To translate with one, read `CultureInfo.CurrentUICulture` in each renderer, or keep one
map per culture.

For full control, derive from `ValidationMessageFormatter` and override `Format`. The error's
`MessageInfo` holds the template and its arguments. `MessageInfo.Render(in error, template,
culture)` fills another template with the same arguments, and it can format them for a culture:

```csharp
public sealed class LocalFormatter : ValidationMessageFormatter
{
    public override string Format(in ValidationError error) =>
        error.MessageInfo is { } info
            ? info.Render(in error, info.Template, CultureInfo.CurrentCulture)
            : error.Message;
}
```

## Language packs

A language pack is a JSON file whose name ends in `.validation-messages.json`. The generator
compiles every such file in the project, so a pack needs no project file entry:

```json
{
  "culture": "fr",
  "templates": {
    "required": "{field} est obligatoire.",
    "range.between": "{field} doit être compris entre {0} et {1}.",
    "stay_order": "Le séjour doit se terminer après son début."
  }
}
```

A key in `templates` is one of three things:

- a shape key from the table above, such as `range.between`
- a bare code, such as `range`, which covers every shape of that code
- one of your own codes, such as `stay_order`

Each pack becomes a class that implements `IValidationLanguagePack`. The generated registration
method registers the packs, and registers a `LanguagePackFormatter` as the
`ValidationMessageFormatter` service. To show translated messages, resolve the formatter and set
the UI culture:

```csharp
var formatter = provider.GetRequiredService<ValidationMessageFormatter>();

CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-CA");

foreach (var error in result.Errors)
{
    Console.WriteLine($"{error.Field}: {error.ToMessage(formatter)}");
}
```

```text
name: name est obligatoire.
nights: nights doit être compris entre 1 et 30.
code: The code has three letters.
```

The formatter reads `CultureInfo.CurrentUICulture` for every message. It tries the culture, then
its parents, so `fr-CA` uses a `fr` pack. A culture with no pack keeps the default English. The
last message above is authored, so the pack does not change it.

A formatter you register before calling the registration method takes the place of the
`LanguagePackFormatter`. The ASP.NET Core integration uses the registered formatter for problem
details responses.

When several packs cover the same culture, a pack registered later wins for each key it defines.
Packs in your project are registered after the packs from `ValidationModules.Messages`, so your own
file can reword a few messages and inherit the rest. Within one pack, a shape key such as
`range.between` takes precedence over the bare code `range`.

`IValidationLanguagePack` has two members, `Culture` and `Templates`, so a pack can also be a class
of your own, for example one that loads its templates from a database. `new
LanguagePackFormatter(packs)` builds a formatter from any set of packs, without a container.

The generator checks each pack at build time:

| Diagnostic | Meaning |
| --- | --- |
| `VM4001` | The file is not valid JSON, or it has no `culture`. The file is skipped. |
| `VM4002` | A key names a shape that does not exist. The entry is skipped. |
| `VM4003` | A template uses more arguments than its shape has. The entry is skipped. |
| `VM4004` | A key appears twice. The later entries are skipped. |
| `VM4005` | The culture in the file name differs from the `culture` in the file. |
| `VM4006` | Information: the pack leaves out some of the 34 shapes, whose messages stay in English. |

## Built-in languages

`ValidationModules.Messages` contains packs for German (`de`), Spanish (`es`), French (`fr`),
Japanese (`ja`) and Chinese (`zh`). The package holds JSON files, not an assembly. Your project's
generator compiles them, so it still needs the generator package:

```shell
dotnet add package ValidationModules.Messages
```

All five languages are compiled by default. `ValidationModulesLanguages` selects some of them, or
none:

```xml
<PropertyGroup>
  <ValidationModulesLanguages>fr;de</ValidationModulesLanguages>
</PropertyGroup>
```

A language that is not selected is not compiled into the assembly.

## Authored messages

The text of these errors is authored, and `MessageIsAuthored` is `true`:

- `Message` on a built-in attribute, and `Message` or `DefaultMessage` on a
  `CustomConstraintAttribute`
- an `Ensure` with `message:`
- a hand-written `ReportAuthored` call
- `ErrorMessage` on a built-in DataAnnotations attribute

`LanguagePackFormatter` returns authored text unchanged. `ValidationMessageMap` does not check the
flag, and replaces the text of any code it maps.

Other text you write is not authored, and a language pack with an entry for its code replaces it.
That covers a `Report` call in a hand-written validator or through `rules.Context`, the `Message` of
an `IConstraintFor<T>` attribute that uses the default `Validate`, and the messages of custom
DataAnnotations attributes, `[CustomValidation]` methods and `IValidatableObject`, which all report
the code `custom`. Every pack in `ValidationModules.Messages` has an entry for `custom`, so with
that package installed those messages become its general sentence, such as `{field} n'est pas
valide.` in French. A pack entry that replaces a message without template arguments can use only
`{field}`.

## Messages with arguments from code

A hand-written validator can report a message built from a template and its arguments, so that
formatters and language packs treat it like a built-in message. `ValidationMessageTemplates` has a
field for each built-in template, and `ValidationMessageTemplates.TemplatesByKey` maps each shape
key to its template:

```csharp
private static readonly ValidationMessageInfo TooHeavy = new(
    ValidationMessageTemplates.RangeAtMost,
    25
);

public ValidationFlow Validate(ref ValidationContext context, Pallet value) =>
    value.WeightKg > 25
        ? context.Report("weightKg", ValidationCodes.Range, value.WeightKg, TooHeavy)
        : ValidationFlow.Continue;
```

This reports `weightKg must be at most 25.`, and a pack's `range.at_most` entry translates it. The
`Report` helpers, such as `ReportRangeAtMost`, build the same kind of message for you.

## Messages for DataAnnotations resources

A DataAnnotations attribute that takes its message from a resource, with `ErrorMessageResourceType`,
is compiled to a `ValidationMessageInfo` whose `Provider` reads the resource property each time the
message is rendered. The provider is a `DelegateMessageProvider`, which implements
`IValidationMessageProvider`. This keeps resource messages working without reflection. You do not
create these types yourself unless you build a `ValidationMessageInfo` by hand. A resource message
is not authored, so a language pack entry for its shape replaces it.
