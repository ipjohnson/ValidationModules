# ASP.NET Core

```shell
dotnet add package ValidationModules.AspNetCore
dotnet add package ValidationModules.SourceGenerator
```

`ValidationModules.Runtime` arrives with the first. The generator is a separate reference because it
is what emits `AddMyAppValidators()` below. Without it there is nothing to register, and the call
does not exist. See [getting started](/guide/getting-started#install) for the `PrivateAssets`
this reference wants.

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
    "": ["a bulk order must list its lines."],
    "reference": ["reference is required."],
    "shipTo.postcode": ["postcode is required."],
    "lines[1].sku": ["sku is required."]
  },
  "validationCodes": {
    "": ["bulk_needs_lines"],
    "reference": ["required"],
    "shipTo.postcode": ["required"],
    "lines[1].sku": ["required"]
  }
}
```

The empty key is the object itself. A failure reported against the value rather than one of its
members has no field name to key on, so it lands under `""` in both dictionaries. A rule comparing
two fields and a `ReportHere` in a rules class both do this. MVC does the same with model-level errors, and a client
that indexes `errors` by field name should expect it.

To cover every route in a group rather than one endpoint at a time, the same call takes a
`RouteGroupBuilder`. It attaches to the routes in the group that can receive a `T` and leaves the
rest alone:

```csharp
var orders = app.MapGroup("/orders").Validate<CreateOrder>();

orders.MapPost("/", (CreateOrder order) => Results.Ok());
orders.MapPost("/draft", (CreateOrder order) => Results.Accepted());
```

## Why the type is named rather than inferred

`.Validate<CreateOrder>()` says the type twice, once in the handler and once in the filter. That is
deliberate.

The obvious filter reflects over the handler's parameters and validates whatever looks validatable.
It would work, it would read better, and it would put reflection on the one path that runs for every
request, in the package whose whole argument is that there isn't any. Naming the type keeps every
lookup a closed generic, which is what makes a published Native AOT binary work.

The argument itself is found by **pattern match, not by position**. Minimal API argument order is the
handler's business and changes whenever a parameter is added, so the filter scans for the first
`CreateOrder`, so adding a `CancellationToken` cannot silently start validating the wrong thing.

Chain it when a handler has more than one thing worth checking:

```csharp
app.MapPost("/orders", (CreateOrder order, [AsParameters] CouponQuery coupon) => Results.Ok())
   .Validate<CreateOrder>()
   .Validate<CouponQuery>();
```

Filters run in the order they were added, so the first failure answers and the second never runs.
A body invalid in both ways takes two round trips to discover.

Note that only one parameter can be inferred from the body. Two plain complex parameters is not a
chaining limitation but a minimal-API one: it fails to bind at all, with *"Failure to infer one or
more parameters"*. The second validated type has to come from somewhere else, such as
`[AsParameters]`, the route, the query string, or a service.

## Business rules run too

The filter resolves [`ValidationRunner<T>`](/guide/registration#validationrunner-t) in preference to
`IValidatorFor<T>`, so a hand-written [async rule](/guide/async) is part of the same pass:

```csharp
builder.Services.AddScoped<IAsyncValidatorFor<CreateOrder>, ReferenceIsUnique>();
```

Structural constraints still run first, and the async rules only run if they found nothing, so a
uniqueness check never reaches the database for a field that was null.

If nothing at all is registered for `T`, the filter **throws** rather than letting the request
through. Validating nothing and reporting success is the one outcome worse than an exception.

## The codes, and why they are there

RFC 9457's `errors` object maps a field to human-readable strings. That is the wrong thing for a
client to branch on: messages are for people, they get reworded, and they localise.

`validationCodes` carries the same grouping over the stable vocabulary instead: `required`,
`string_length`, `pattern`, and the [full list](/reference/codes). It is an extension member rather than
a replacement, so every existing client, test suite and Swagger UI still reads `errors` exactly as
before.

Turn it off if a strict schema rejects unknown members:

```csharp
builder.Services.AddValidationProblemDetails(options => options.IncludeCodes = false);
```

The same call adjusts the rest of the response:

| Option | Default |
|---|---|
| `Title` | `One or more validation errors occurred.`, the text ASP.NET Core itself uses |
| `Type` | the RFC 9110 §15.5.1 URI |
| `StatusCode` | `400` |
| `IncludeCodes` | `true` |
| `IncludeNonErrors` | `false`, since warnings did not reject the request |

## Failures thrown further in

The filter covers what a handler was handed. A service that validates deeper in, such as
`ValidateAndThrow` inside a domain method, is covered by an exception handler that produces the same
body:

```csharp
builder.Services.AddValidationProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
```

Both paths share one mapping deliberately. A service whose validation failures are shaped one way
when caught early and another way when thrown late is a service whose clients need two parsers.

Those two pipeline calls are worth a sentence each, because leaving either out is a real failure and
neither announces itself.

`UseExceptionHandler()` needs something to render the exceptions this package's handler *declines*,
which means a malformed body, a null dereference, or anything else that is not a
`ValidationException`.
`AddValidationProblemDetails()` registers ASP.NET Core's own problem-details service for exactly that,
which is what lets the no-argument spelling work. Reaching instead for `app.UseExceptionHandler(_ => { })`
compiles, starts, and is wrong. The empty lambda becomes a branch pipeline that returns 404, the
middleware reports *that* as a failure, and every non-validation fault comes back as a 500 whose
message is about a 404, with the real exception buried underneath. A request body that never parsed
lands there too.

`UseStatusCodePages()` covers the responses ASP.NET Core produces *without* throwing.
`RouteHandlerOptions.ThrowOnBadRequest` defaults to `IsDevelopment()`, so an unparseable body raises
an exception in Development and, outside it, is simply a 400 with no content at all. The second call
gives that empty 400 a body, so a client sees the same shape in both environments.

## Native AOT

Verified by publishing and serving rather than by unit tests. `scripts/verify-aot.sh` builds a real
minimal API, posts an invalid body to it, and asserts the response carries the nested paths and the
codes.

That probe exists because of a fault it found. The filter originally returned
`Results.Problem(problem)`, which serialises through the *application's* `JsonSerializerOptions`. In
a published AOT app those resolve through your `JsonSerializerContext`, which knows your DTOs and
has never heard of `ProblemDetails`. Every validation failure came back as an empty 500. Under the
JIT the reflection fallback hides it completely.

The response is now written through this package's own serialiser metadata, which also makes it
independent of however you have configured JSON. RFC 9457 fixes these member names, so there is
nothing there a naming policy should be reshaping.

That covers the problem body. **Your own request and response types are still yours to declare.** A
published AOT app has no reflection to fall back on, so without a context for them the first request
fails while deserialising, before validation is ever reached. Declare them and chain the context in
rather than replacing the resolver, so this package's metadata stays reachable:

```csharp
[JsonSerializable(typeof(CreateOrder))]
[JsonSerializable(typeof(AcceptedOrder))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
```

`integ-tests/ApiDemo` in the repository is a working example of this whole page: the wiring above,
the two failure paths, and the context. `integ-tests/ApiDemo.Tests` asserts the responses over real
HTTP in both environments.

## What this package does not do

- **MVC controllers and model binding.** The filter is a minimal API endpoint filter. A controller
  can call the validator directly, or map a result with `ValidationProblem.ToResult(result)`.
- **Automatic discovery.** There is no "validate every parameter that has a validator" mode, for the
  reason at the top of this page.
- **OpenAPI schema.** The constraints are not currently projected into a generated document.
