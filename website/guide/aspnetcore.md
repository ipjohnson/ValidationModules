# ASP.NET Core

`ValidationModules.AspNetCore` validates minimal API arguments before the handler runs, and turns
failed validation into an RFC 9457 problem details response.

## Install

```shell
dotnet add package ValidationModules.AspNetCore
dotnet add package ValidationModules.SourceGenerator
```

`ValidationModules.AspNetCore` brings in `ValidationModules.Runtime`. The packages target .NET 8 and
.NET 10.

## Set up an endpoint

These are the models:

<!-- verify -->
```csharp
using ValidationModules.Constraints;

public sealed record CreateOrder
{
    [Required, StringLength(3, 40)]
    public string? Reference { get; init; }

    [Range(1, 500)]
    public int Quantity { get; init; }

    [ValidateNested]
    public Address? ShipTo { get; init; }

    [ValidateNested]
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];
}

public sealed record Address
{
    [Required]
    public string? Postcode { get; init; }
}

public sealed record OrderLine
{
    [Required]
    public string? Sku { get; init; }
}
```

And this is the application, in a project named `Shop`:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddShopValidators();
builder.Services.AddValidationProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapPost("/orders", (CreateOrder order) => Results.Ok(order)).Validate<CreateOrder>();

app.Run();
```

`.Validate<CreateOrder>()` adds an endpoint filter. For each request it finds the handler's
`CreateOrder` argument, validates it, and runs the handler only when the result is valid.
`AddValidationProblemDetails()` configures the response. The filter works without it, with the
default options, but the call is needed to change the options and to turn a thrown
`ValidationException` into a response. It also calls `AddProblemDetails()`, which
`UseExceptionHandler()` needs when it is given no arguments. `UseExceptionHandler()` and
`UseStatusCodePages()` turn other failed requests into problem details too, as described below.

The repository's `integ-tests/ApiDemo` project runs this setup, and its tests check the responses
shown on this page.

## The response

Posting this body:

```json
{ "reference": "ab", "quantity": 0, "shipTo": {}, "lines": [{ "sku": "A1" }, {}] }
```

returns `400 Bad Request` with the content type `application/problem+json`:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "reference": ["reference must be between 3 and 40 characters."],
    "quantity": ["quantity must be between 1 and 500."],
    "shipTo.postcode": ["postcode is required."],
    "lines[1].sku": ["sku is required."]
  },
  "validationCodes": {
    "reference": ["string_length"],
    "quantity": ["range"],
    "shipTo.postcode": ["required"],
    "lines[1].sku": ["required"]
  }
}
```

`errors` has the shape of ASP.NET Core's `ValidationProblemDetails`, keyed by field path.
`validationCodes` holds the codes under the same keys. A client that reacts to specific failures
should read the codes. An error about the object as a whole, reported with `ReportHere`, appears
under the empty key `""`.

## What the filter does

- It validates through `ValidationRunner<T>.ValidateAsync`, with the request's services and
  cancellation token. Hand-written and [async validators](./async) for the type run too.
- A result with only warnings is valid, and the handler runs.
- When the argument is `null` or missing, the filter lets the handler run. A body that is not valid
  JSON is rejected by ASP.NET Core with its own `400` before the filter runs.
- `.Validate<T>()` works on a route group as well as on a single endpoint. On a group, endpoints
  whose handlers take no `T` are left alone.
- A filter can be added more than once, as in `.Validate<Order>().Validate<Customer>()`. The first
  failure answers the request.

The filter checks its setup when the endpoint is first built, which happens on the first request.
It throws an `InvalidOperationException` when the handler has no parameter of type `T`, or when no
validator is registered for `T`. Endpoints are built together, so such a mistake fails every
endpoint. The generator reports `VM5003` at build time for a `.Validate<T>()` whose `T` has no
rules at all.

## Status codes

The status is `400` by default. Change it for one endpoint or group:

```csharp
app.MapPost("/orders/strict", (CreateOrder order) => Results.Ok(order))
    .Validate<CreateOrder>(statusCode: 422);
```

The `type` member follows the status: `422` gives
`https://tools.ietf.org/html/rfc9110#section-15.5.21`. A status with no section in RFC 9110 gives
`about:blank`. A `Type` set in the options is kept for every status.

## Options

`AddValidationProblemDetails` takes a delegate that sets `ValidationProblemOptions`:

```csharp
builder.Services.AddValidationProblemDetails(options =>
{
    options.StatusCode = StatusCodes.Status422UnprocessableEntity;
    options.PathMode = ValidationPathMode.Full;
});
```

| Property | Default | Effect |
| --- | --- | --- |
| `StatusCode` | `400` | The response status. |
| `Title` | `One or more validation errors occurred.` | The `title` member. |
| `Type` | a link to the status in RFC 9110 | The `type` member. |
| `IncludeCodes` | `true` | Adds the `validationCodes` member. |
| `IncludeNonErrors` | `false` | Adds warnings and informational entries to a response that has errors. |
| `PathMode` | `Bounded` | How deep paths are written. See [Paths](./errors#paths). |
| `MessageFormatter` | the registered `ValidationMessageFormatter`, if any | Formats the messages, for example with [language packs](./messages#language-packs). |

Warnings never cause a failed response. With `IncludeNonErrors`, they appear in `errors` next to the
errors, without a severity marker.

## Thrown validation exceptions

A `ValidationException` thrown while handling a request produces the same response, through the
exception handler that `AddValidationProblemDetails` registers. This needs
`app.UseExceptionHandler()`. It suits validation that happens deeper in the application than the
endpoint:

```csharp
app.MapPost(
    "/orders/import",
    (CreateOrder order, ValidationRunner<CreateOrder> runner) =>
    {
        var result = runner.Validate(order);
        if (!result.IsValid)
        {
            throw new ValidationException(result);
        }

        return Results.Ok(order);
    }
);
```

The exception handler uses the status from `ValidationProblemOptions`, not a status given to a
single endpoint. Other exceptions are left to the default handling and still produce a `500`.

`validator.ValidateAndThrow(value)` also throws `ValidationException`. It uses a single
`IValidatorFor<T>`, so it does not run hand-written validators registered alongside the generated
one. Validate through `ValidationRunner<T>` when there are several.

## Malformed requests

When ASP.NET Core rejects a request body itself, for example because the JSON does not parse, it
throws `BadHttpRequestException` in development and writes an empty `400` otherwise.
`AddValidationProblemDetails` registers a handler that keeps the exception's status, such as `400`
or `415`, instead of `500`. With `UseExceptionHandler()` and `UseStatusCodePages()`, both cases
become problem details responses.

## Lists as request bodies

The registration method also registers validators for `List<T>` and `T[]`. A handler that takes a
list can validate every element:

```csharp
app.MapPost("/orders/batch", (List<CreateOrder> orders) => Results.Ok(orders.Count))
    .Validate<List<CreateOrder>>();
```

The error keys start with the element's index, as in `[1].reference`. Other collection types, such
as `IReadOnlyList<T>`, need a hand-written validator. A rule about the list as a whole, such as a
maximum batch size, goes in a hand-written `IValidatorFor<List<CreateOrder>>`. Registered after the
generated validators, it runs alongside the per-element checks.

## Build a response yourself

`ValidationProblem` converts a `ValidationResult` without the filter:

| Method | Returns |
| --- | --- |
| `ValidationProblem.ToResult(result, options)` | An `IResult` for a minimal API handler. |
| `ValidationProblem.ToProblemDetails(result, options)` | A `ValidationProblemDetails`, for example for an MVC controller. |
| `ValidationProblem.ToDictionary(result, options)` | Field paths to messages. |
| `ValidationProblem.ToCodeDictionary(result, options)` | Field paths to codes. |

These methods use the `options` you pass, or the defaults when you pass none. They do not read the
options configured with `AddValidationProblemDetails`.

There is no filter for MVC controllers. In a controller action, validate with `ValidationRunner<T>`
and return the problem details from `ToProblemDetails`. The package also adds nothing to OpenAPI
documents. The rules do not appear in a generated schema.

## Native AOT

The problem details body is written with the package's own source-generated JSON metadata, so the
application's `JsonSerializerContext` lists only the application's own types:

```csharp
var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ShopJsonContext.Default)
);

[JsonSerializable(typeof(CreateOrder))]
internal sealed partial class ShopJsonContext : JsonSerializerContext;
```

The problem details body does not follow the application's JSON naming policy. Its keys are the
field paths the generator wrote. See [Native AOT](./aot) for the rest of the AOT setup.
