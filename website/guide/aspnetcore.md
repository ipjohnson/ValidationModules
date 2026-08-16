# ASP.NET Core

```shell
dotnet add package ValidationModules.AspNetCore
```

One filter per endpoint, and a failed request is answered before the handler runs:

```csharp
builder.Services.AddMyAppValidators();

var app = builder.Build();

app.MapPost("/orders", (CreateOrder order) => Results.Ok(new { accepted = true }))
   .Validate<CreateOrder>();
```

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "reference": ["reference is required."],
    "shipTo.postcode": ["postcode is required."],
    "lines[1].sku": ["sku is required."]
  },
  "validationCodes": {
    "reference": ["required"],
    "shipTo.postcode": ["required"],
    "lines[1].sku": ["required"]
  }
}
```

## Why the type is named rather than inferred

`.Validate<CreateOrder>()` says the type twice — once in the handler and once in the filter — and
that is deliberate.

The obvious filter reflects over the handler's parameters and validates whatever looks validatable.
It would work, it would read better, and it would put reflection on the one path that runs for every
request, in the package whose whole argument is that there isn't any. Naming the type keeps every
lookup a closed generic, which is what makes a published Native AOT binary work.

The argument itself is found by **pattern match, not by position**. Minimal API argument order is the
handler's business and changes whenever a parameter is added, so the filter scans for the first
`CreateOrder` — adding a `CancellationToken` cannot silently start validating the wrong thing.

Chain it when a handler has more than one thing worth checking:

```csharp
app.MapPost("/orders", (CreateOrder order, Coupon coupon) => Results.Ok())
   .Validate<CreateOrder>()
   .Validate<Coupon>();
```

Filters run in the order they were added, so the first failure answers and the second never runs.

## Business rules run too

The filter resolves [`ValidationRunner<T>`](/guide/registration#validationrunner-t) in preference to
`IValidatorFor<T>`, so a hand-written [async rule](/guide/async) is part of the same pass:

```csharp
builder.Services.AddScoped<IAsyncValidatorFor<CreateOrder>, ReferenceIsUnique>();
```

Structural constraints still run first, and the async rules only run if they found nothing — so a
uniqueness check never reaches the database for a field that was null.

If nothing at all is registered for `T`, the filter **throws** rather than letting the request
through. Validating nothing and reporting success is the one outcome worse than an exception.

## The codes, and why they are there

RFC 9457's `errors` object maps a field to human-readable strings. That is the wrong thing for a
client to branch on: messages are for people, they get reworded, and they localise.

`validationCodes` carries the same grouping over the stable vocabulary instead — `required`,
`string_length`, `pattern`, the [full list](/reference/codes). It is an extension member rather than
a replacement, so every existing client, test suite and Swagger UI still reads `errors` exactly as
before.

Turn it off if a strict schema rejects unknown members:

```csharp
builder.Services.AddValidationProblemDetails(options => options.IncludeCodes = false);
```

The same call adjusts the rest of the response:

| Option | Default |
|---|---|
| `Title` | `One or more validation errors occurred.` — the text ASP.NET Core itself uses |
| `Type` | the RFC 9110 §15.5.1 URI |
| `StatusCode` | `400` |
| `IncludeCodes` | `true` |
| `IncludeNonErrors` | `false` — warnings did not reject the request, so they do not explain it |

## Failures thrown further in

The filter covers what a handler was handed. A service that validates deeper in — `ValidateAndThrow`
inside a domain method — is covered by an exception handler that produces the same body:

```csharp
builder.Services.AddValidationProblemDetails();

var app = builder.Build();

app.UseExceptionHandler(_ => { });
```

Both paths share one mapping deliberately. A service whose validation failures are shaped one way
when caught early and another way when thrown late is a service whose clients need two parsers.

## Native AOT

Verified by publishing and serving, not by unit tests — `scripts/verify-aot.sh` builds a real
minimal API, posts an invalid body to it, and asserts the response carries the nested paths and the
codes.

That probe exists because of a fault it found. The filter originally returned
`Results.Problem(problem)`, which serialises through the *application's* `JsonSerializerOptions` — and
in a published AOT app those resolve through your `JsonSerializerContext`, which knows your DTOs and
has never heard of `ProblemDetails`. Every validation failure came back as an empty 500. Under the
JIT the reflection fallback hides it completely.

The response is now written through this package's own serialiser metadata, which also makes it
independent of however you have configured JSON. RFC 9457 fixes these member names, so there is
nothing there a naming policy should be reshaping.

## What this package does not do

- **MVC controllers and model binding.** The filter is a minimal API endpoint filter. A controller
  can call the validator directly, or map a result with `ValidationProblem.ToResult(result)`.
- **Automatic discovery.** There is no "validate every parameter that has a validator" mode, for the
  reason at the top of this page.
- **OpenAPI schema.** The constraints are not currently projected into a generated document.
