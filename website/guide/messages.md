# Messages and translation

An error carries data: the field, the [code](/reference/codes), the attempted value, and the
constraint's own template and arguments. The message is rendered when something reads it, rather
than when the failure happened:

<!-- verify:models -->
```csharp
var result = new PetValidator().Validate(new Pet());
var error = result.Errors[0];

Console.WriteLine(error.Field);      // name
Console.WriteLine(error.Code);       // required
Console.WriteLine(error.Message);    // name is required. (rendered by this read)
```

That ordering is the feature. A `ValidationResult` is in no language until a reader picks one, so
the same result can render the default English into a log line, a translation into a 400 body,
and the code and arguments verbatim to a client that renders its own text. None of that
revalidates, and the pass never knew those readers existed.

Nothing about this costs the paths that matter: a clean pass allocates nothing, as ever, and a
failing pass stores references instead of composing prose, so it got cheaper. Only a reader that
wants prose pays for rendering it.

## Start in one language

Do nothing. Every constraint renders the same default text it always has, such as
`name is required.` and `age must be between 0 and 30.`, from templates that live once in
`ValidationMessageTemplates`. The defaults compose from the *leaf* of the field path (an error at
`toys[3].name` reads "name is required.", because the path is already in `Field` and prose is not
an address label), format their arguments invariantly, and never include the attempted value.

## Add languages

Reference `ValidationModules.Messages`. The package carries five languages - `de`, `es`, `fr`,
`ja` and `zh` - as `*.validation-messages.json` files that compile into your assembly at your
build. If you call your assembly's `Add…Validators()` and validate through the endpoint filter,
you are already done: the registration registers each compiled pack as an
`IValidationLanguagePack` and TryAdds a `LanguagePackFormatter` over all of them, and the
formatter reads `CultureInfo.CurrentUICulture` per render - which localization middleware sets
per request.

`<ValidationModulesLanguages>` in the csproj filters the bundle:

| Value | Compiles |
|---|---|
| unset, or `all` | every language the package carries |
| `fr;de` (a semicolon list) | exactly those |
| `none` | nothing - the package is off without being removed |

The filter runs before the generator ever sees a file, so an excluded language is absent from the
binary rather than carried and ignored.

## Write a pack

Drop a `*.validation-messages.json` anywhere in the project. No `<AdditionalFiles>` entry is
needed - the build reads every file with that suffix - and the file compiles into the same
`IValidationLanguagePack` shape the package's own languages use, validated at build time
([VM0100–VM0105](/reference/diagnostics)) rather than parsed at startup.

```json
{
    "culture": "fr",
    "templates": {
        "required": "{field} est obligatoire.",
        "string_length.at_most": "{field} doit contenir au plus {0} caractères.",
        "string_length.at_most_singular": "{field} doit contenir au plus {0} caractère.",
        "date_order": "la date de fin doit suivre la date de début."
    }
}
```

The parts, precisely:

- **`culture` decides the culture.** The file name is convention (`fr.validation-messages.json`,
  or `overrides.fr.validation-messages.json` for a partial override); when name and member
  disagree, the member wins and the build warns (VM0104).
- **`templates` is keyed by the stable vocabulary.** A key is a wire code - built-in like
  `required`, or your own like `date_order` from an `Ensure(code: …)` - or a *shape key* beneath
  the codes whose sentence varies with their arguments: `string_length.between`,
  `string_length.at_most_singular`, `range.greater_than`, `enum.denied`, and so on. The singular
  variants exist because "at most 1 characters" is wrong in most languages; the renderer picks
  the shape, your pack words it. A key the shape inventory does not know warns at build (VM0101).
- **Holes are `{field}` and the positional `{0}`, `{1}`.** `{field}` is the error's field-path
  leaf; the positions are the constraint's own arguments, in the same order the default English
  uses them. A hole past the shape's argument count is a build error (VM0102), not a format
  exception in one culture at runtime.
- **Cover as much or as little as you like.** A pack with one entry rewords one message;
  everything unmatched keeps its default render. VM0105 reports coverage as an Info if you want
  the inventory.

## What wins over what

Every registered pack feeds one merged table per requested culture, and the rules are:

1. **The requested culture beats its parents.** An `fr-CA` request layers `fr-CA` packs over
   `fr` packs; a culture with no pack at all falls through to the default English render.
2. **Later registration beats earlier, per key.** The package's props register its languages
   before your project items, so an app-local file always lands later and wins the keys it
   declares - the person closest to the user has the last word, with no configuration. Across
   assemblies, registration order is the order the composition root called the `Add…Validators()`
   methods, exactly as it is for validators.
3. **The shape key beats the code within a layer.** A `string_length.at_most` entry outranks a
   `string_length` entry from the same pack; a later pack that rewrites the whole code takes all
   of its shapes.
4. **A custom `Message` beats every pack entry.** An attribute's `Message = "…"` and an
   `Ensure`'s explicit `message:` are the application's own words, and no pack replaces them -
   whatever keys the pack carries for that code. Give the rule its own `Code` and word that code
   per culture if the custom text should translate; a hand-written validator's
   `Report(field, code, message)` works exactly that way, since its finished string is still
   replaceable by a bare code-level entry. To pin a hand-written message the way an attribute's
   `Message` is pinned, report it through `ReportAuthored`.

## Outside ASP.NET Core

Nothing above is tied to HTTP. In a plain class library or worker, resolve the formatter and hand
it to the read:

<!-- verify:models -->
```csharp
var services = new ServiceCollection()
    .AddSampleValidators()          // your assembly's generated registration
    .BuildServiceProvider();

var validator = services.GetRequiredService<IValidatorFor<Pet>>();
var formatter = services.GetRequiredService<ValidationMessageFormatter>();

var result = validator.Validate(new Pet());

foreach (var error in result.Errors) {
    Console.WriteLine(error.ToMessage(formatter));   // rendered in CurrentUICulture
}
```

`ValidationError.ToMessage(formatter)` is the single-error read; `error.Message` stays the
default render. Without a container, construct the formatter directly:
`new LanguagePackFormatter(packs)` over the `IValidationLanguagePack` instances you choose.

## Branch when you need to

`ValidationMessageMap` is a formatter dispatched by code: map exactly the codes you care about,
and everything else keeps its default. Each entry is a `ValidationMessageMap.MessageRenderer` -
plain C# over the error:

```csharp
public static class FrenchMessages {
    public static readonly ValidationMessageFormatter Instance = new ValidationMessageMap()
        .Map(ValidationCodes.Required,     static (in ValidationError e) => $"{e.Field} est obligatoire.")
        .Map(ValidationCodes.StringLength, static (in ValidationError e) =>
            $"{e.Field} doit contenir entre {e.MessageInfo!.Args[0]} et {e.MessageInfo.Args[1]} caractères.")
        .Map("date_order",                 static (in ValidationError _) =>
            "la date de fin doit suivre la date de début.");
}
```

Three things worth noticing. The translations are C#, so a hole that references nothing is a
compile error rather than a runtime format exception in one culture, and pluralization or
grammatical agreement is an ordinary conditional. Those are the places template dialects break
down - and the reason to prefer the map over a pack when the wording needs logic. User-defined
codes dispatch exactly like built-ins, whether from an `Ensure(code: "date_order")` or a custom
constraint, because the map is keyed by the wire code and nothing else. The map holds no culture.
Read `CultureInfo.CurrentUICulture` inside a delegate, or build one map per culture and pick at
the boundary. Both work, which is why neither is imposed.

Apply a formatter where reading happens:

```csharp
// One error:
error.ToMessage(FrenchMessages.Instance);

// The HTTP boundary: the errors object localises per request, the codes stay put:
builder.Services.AddValidationProblemDetails(options =>
    options.MessageFormatter = FrenchMessages.Instance);
```

Setting `ValidationProblemOptions.MessageFormatter` is optional when a language pack is
registered: the endpoint filter fills in the container's `ValidationMessageFormatter` when the
options carry none, which is what "reference the package and you are done" means. An explicit
formatter always wins. The `validationCodes` extension is deliberately untouched by any
formatter. It is the stable vocabulary, and rendering is what it must not depend on.

For a team with a translation pipeline, a resx- or `IStringLocalizer`-backed formatter is a small
`ValidationMessageFormatter` subclass; the map is the direct form, not the only one. One naming
note: `ValidationMessageFormatter` is an abstract class, and the only abstraction here you
implement against that is not `I`-prefixed - there is no `IValidationMessageFormatter` to find,
unlike `IValidatorFor`, `IValidationRulesFor` and `IValidationLanguagePack`.

## Or let the client translate

`error.Code` and `error.MessageInfo?.Args` are the machine-readable failure. A `string_length`
with `[3, 50]` is everything a front end needs to render its own text in its own language. Teams
that already localise in the client can ignore server-side rendering entirely; the
[codes](/reference/codes) were always the contract, and now the arguments travel beside them.

## The attempted value, and who may show it

Every generated constraint site captures the failing member into `ValidationError.Value`. What it
does **not** do is render it: not in `Message`, not in `ToString()`, not in
`ValidationException.Message`, not in a problem-details body. A formatter is the one surface that
may, which makes echoing a value an explicit decision at a named place:

```csharp
// Development only: messages that name the offending value.
if (builder.Environment.IsDevelopment()) {
    builder.Services.AddValidationProblemDetails(options =>
        options.MessageFormatter = new ValidationMessageMap()
            .Map(ValidationCodes.Pattern, static (in ValidationError e) =>
                $"'{e.Value}' is not in the required format."));
}
```

Production keeps the redacted defaults; Development names the value; the validators are identical.
For builds that must not carry values at all, set
[`ValidationModules_CaptureValues=false`](/reference/msbuild#validationmodules-capturevalues). The
generator then emits no capture, so the value's absence is a property of the compiled binary rather
than a runtime promise.

## What stays untranslatable

An error whose `MessageInfo` is null carries only its finished text: failures mapped from another
engine, DataAnnotations' invoked user code (`IValidatableObject`, custom attributes,
`[CustomValidation]`), and hand-written `Report(field, code, message)` calls. A pack or a map can
still rewrite them by code - the template then renders with `{field}` only, argument holes
verbatim - but there are no arguments to build from. `Ensure`'s rendered conditions are
compile-time source and stay literal; give one a `code:` and the map takes it from there, which
is the [documented route](/guide/rule-classes#ensure) anyway.

Two DataAnnotations notes, because migrating models bring their messages with them: an
`ErrorMessage` template has everything but its display name baked in at build time, and a
resource-backed message (`ErrorMessageResourceType`/`Name`) compiles to a property read performed
per render - an `IValidationMessageProvider` wrapping the resx accessor in a
`DelegateMessageProvider` - so culture fallback and satellite assemblies work and nothing
resolves reflectively. See [DataAnnotations](/guide/data-annotations#messages).
