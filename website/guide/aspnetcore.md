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

If nothing at all is registered for `T`, the endpoint **fails when it is built** - which minimal
APIs do on the first request to any route - rather than letting requests through. Validating
nothing and reporting success is the one outcome worse than an exception, and discovering the
mistake on the endpoint's own traffic is worse than any smoke test catching it.

## Array bodies

A batch endpoint takes a JSON array, and a JSON array binds to a `List<T>` or a `T[]`. Both
validate element-wise, with the element's position in the path:

```csharp
app.MapPost("/orders/batch", (List<CreateOrder> orders) => Results.Ok(new { accepted = orders.Count }))
   .Validate<List<CreateOrder>>();
```

```json
{
  "status": 400,
  "errors": {
    "[1].reference": ["reference is required."],
    "[1].quantity": ["quantity must be between 1 and 500."]
  }
}
```

No validator is generated for `List<CreateOrder>` itself - closed generic types never get one.
What `Add<Assembly>Validators()` registers instead is a `CollectionValidatorFor<CreateOrder>`,
which walks the list and runs the element type's validators per element, and a
`CollectionAsyncValidatorFor<CreateOrder>` doing the same for the element's
[business rules](/guide/async). Both come with a runner, so structural constraints still gate the
async pass, batch-wide. A null element is skipped, exactly as a null element of a
`[ValidateNested]` collection property is.

The two registered shapes are `List<T>` and `T[]`, because those are what a body parameter is
ordinarily declared as. A parameter declared as another collection shape needs a hand-registered
`IValidatorFor<>` - or, usually better, a wrapper type with a `[ValidateNested]` list property,
which also gives the batch somewhere to carry its own rules. A hand-written
`IValidatorFor<List<CreateOrder>>` - a batch size cap, say - composes with the element walk rather
than replacing it, like every other validator registration.

## Enums in the body

The examples above bind strings and numbers, so this is worth one section: System.Text.Json's
default for an enum body field is **numbers only**. A client sending the name meets the
serializer, not the validator:

```csharp
public sealed record CreateTicket {
    [EnumDefined]
    public TicketPriority Priority { get; init; }   // {"priority": "urgent"} is a 400 before validation runs
}
```

Opt into names with the converter, application-wide:

```csharp
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
```

The division of labour is exact: `JsonStringEnumConverter` decides what parses, and
`[EnumDefined]` judges what arrived - a raw number outside the declared members deserializes fine
under either configuration, and rejecting it is the validator's job.

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

The same call adjusts the rest of the response. The options type is `ValidationProblemOptions`,
and what you configure here is **application-wide** - every endpoint filter and the exception
handler read the same instance:

| Option | Default |
|---|---|
| `Title` | `One or more validation errors occurred.`, the text ASP.NET Core itself uses |
| `Type` | follows `StatusCode` to the matching RFC 9110 section; set it to pin your own URI |
| `StatusCode` | `400` |
| `IncludeCodes` | `true` |
| `IncludeNonErrors` | `false`, since warnings did not reject the request |
| `MessageFormatter` | `null`; a registered [language pack](/guide/messages) fills it in per request |
| `PathMode` | `Bounded`; set `ValidationPathMode.Full` to render every path segment instead of eliding the middle of a deep path - see [asking for the whole path](/guide/nesting#asking-for-the-whole-path) |

## Per-endpoint status

One endpoint can answer failures with a different status while the rest of the application keeps
the default. `Validate<T>()` takes the override directly:

```csharp
app.MapPost("/orders", (CreateOrder order) => Results.Ok())
   .Validate<CreateOrder>();                       // 400, the application-wide default

app.MapPost("/orders/strict", (CreateOrder order) => Results.Ok())
   .Validate<CreateOrder>(statusCode: 422);        // 422 on this endpoint alone
```

The body's `type` member follows the status - the 422 response links RFC 9110's definition of
422, not 400's - so the document stays consistent with itself. An explicitly configured `Type`
wins over the derived link, on every endpoint, because an explicit URI is an API contract rather
than a default. The group overload takes the same parameter.

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
  can call the validator directly and map a result with
  `ValidationProblem.ToResult(result, options)` - the second parameter is a
  `ValidationProblemOptions`, which is what makes the manual path answer with the same shape,
  status and codes the filter produces. `ToDictionary`, `ToCodeDictionary` and
  `ToProblemDetails` take the same pair for anything that wants the pieces rather than the
  `IResult`.
- **Automatic discovery.** There is no "validate every parameter that has a validator" mode, for the
  reason at the top of this page.
- **OpenAPI schema.** The constraints are not currently projected into a generated document.
