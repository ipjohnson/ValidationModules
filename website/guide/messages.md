# Messages and translation

An error carries data — the field, the [code](/reference/codes), the attempted value, and the
constraint's own template and arguments. The message is rendered when something reads it, not
when the failure happened:

<!-- verify:models -->
```csharp
var result = new PetValidator().Validate(new Pet());
var error = result.Errors[0];

Console.WriteLine(error.Field);      // name
Console.WriteLine(error.Code);       // required
Console.WriteLine(error.Message);    // name is required. — rendered by this read
```

That ordering is the feature. A `ValidationResult` is in no language until a reader picks one, so
the same result can render the default English into a log line, a translation into a 400 body,
and the code and arguments verbatim to a client that renders its own text — without revalidating
and without the pass having known any of those readers existed.

Nothing about this costs the paths that matter: a clean pass allocates nothing, as ever, and a
failing pass now stores references instead of composing prose — it got cheaper, and the render is
paid only by readers that actually want prose. `docs/structured-errors.md` in the repository has
the measurements.

## Start in one language

Do nothing. Every constraint renders the same default text it always has — `name is required.`,
`age must be between 0 and 30.` — from templates that live once, in
`ValidationMessageTemplates`. The defaults compose from the *leaf* of the field path (an error at
`toys[3].name` reads "name is required.", because the path is already in `Field` and prose is not
an address label), format their arguments invariantly, and never include the attempted value.

## Branch when you need to

`ValidationMessageMap` is a formatter dispatched by code: map exactly the codes you care about,
and everything else keeps its default.

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
grammatical agreement is an ordinary conditional — the places template dialects break down.
User-defined codes — an `Ensure(code: "date_order")`, a custom constraint's code — dispatch
exactly like built-ins, because the map is keyed by the wire code and nothing else. And the map
holds no culture: read `CultureInfo.CurrentUICulture` inside a delegate, or build one map per
culture and pick at the boundary — both work, which is why neither is imposed.

Apply a formatter where reading happens:

```csharp
// One error:
error.ToMessage(FrenchMessages.Instance);

// The HTTP boundary — the errors object localises per request, the codes stay put:
builder.Services.AddValidationProblemDetails(options =>
    options.MessageFormatter = FrenchMessages.Instance);
```

The filter runs inside the request, after localization middleware has set the culture, so a map
that reads the ambient culture localises per request with nothing else configured. The
`validationCodes` extension is deliberately untouched by any formatter — it is the stable
vocabulary, and rendering is exactly what it must not depend on.

For a team with a translation pipeline, a resx- or `IStringLocalizer`-backed formatter is a small
`ValidationMessageFormatter` subclass; the map is the direct form, not the only one.

## Or let the client translate

`error.Code` and `error.MessageInfo?.Args` are the machine-readable failure — `string_length`
with `[3, 50]` is everything a front end needs to render its own text in its own language. Teams
that already localise in the client can ignore server-side rendering entirely; the
[codes](/reference/codes) were always the contract, and now the arguments travel beside them.

## The attempted value, and who may show it

Every generated constraint site captures the failing member into `ValidationError.Value`. What it
does **not** do is render it: not in `Message`, not in `ToString()`, not in
`ValidationException.Message`, not in a problem-details body. A formatter is the one surface that
may — which makes echoing a value an explicit decision at a named place:

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
[`ValidationModules_CaptureValues=false`](/reference/msbuild#validationmodules-capturevalues) —
the generator then emits no capture, so the value's absence is a property of the compiled binary
rather than a runtime promise.

## What stays untranslatable

An error whose `MessageInfo` is null carries only its finished text: failures mapped from another
engine, DataAnnotations' invoked user code (`IValidatableObject`, custom attributes,
`[CustomValidation]`), and hand-written `Report(field, code, message)` calls. A formatter can
still rewrite them by code — it just has no arguments to build from. `Ensure`'s rendered
conditions are compile-time source and stay literal; give one a `code:` and the map takes it from
there, which is the [documented route](/guide/rule-classes#ensure) anyway.

Two DataAnnotations notes, because migrating models bring their messages with them: an
`ErrorMessage` template has everything but its display name baked in at build time, and a
resource-backed message (`ErrorMessageResourceType`/`Name`) compiles to a property read performed
per render — culture fallback and satellite assemblies work, nothing resolves reflectively. See
[DataAnnotations](/guide/data-annotations#messages).
