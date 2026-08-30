using ApiDemo;
using ValidationModules;

// The app the guide describes, running for real. Everything here is the documented wiring and
// nothing more - the point of this project is that following the guide produces correct responses,
// so any deviation between this file and website/guide/aspnetcore.md is a bug in one of them.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiDemoValidators();
builder.Services.AddValidationProblemDetails();

// Type-level errors need a hand-written validator; the generator only compiles per-member constraints.
builder.Services.AddSingleton<IValidatorFor<CreateOrder>, OrderTotalsValidator>();

// AOT: the app's DTOs resolve through the app's own context. The problem body carries its own
// metadata inside the package, so the two chain rather than one replacing the other.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiDemoJsonContext.Default));

var app = builder.Build();

// UseExceptionHandler() with no argument renders through IProblemDetailsService, which
// AddValidationProblemDetails() registers. UseStatusCodePages() covers the responses ASP.NET Core
// produces without throwing - a malformed body outside Development is a 400 with no content at all.
app.UseExceptionHandler();
app.UseStatusCodePages();

// The filter path: the handler runs only if the body validated.
app.MapPost("/orders", (CreateOrder order) => Results.Ok(new AcceptedOrder(order.Reference!, order.Quantity)))
    .Validate<CreateOrder>();

// The same body under a per-endpoint status: this endpoint answers 422 while /orders stays on the
// application-wide 400, and each body's type member follows its own status.
app.MapPost("/orders/strict", (CreateOrder order) => Results.Ok(new AcceptedOrder(order.Reference!, order.Quantity)))
    .Validate<CreateOrder>(statusCode: 422);

// The thrown path: a service validating deeper in, which the exception handler maps to the same
// body. It runs through ValidationRunner<T> rather than a single IValidatorFor<T>, because the
// runner is what composes every registered validator - which is also what the filter resolves, so
// the two paths agree. ValidateAndThrow lives on IValidatorFor<T> and would see only one of them.
app.MapPost("/orders/deep", (CreateOrder order, ValidationRunner<CreateOrder> runner) => {
    var result = runner.Validate(order);
    if (!result.IsValid) {
        throw new ValidationException(result);
    }

    return Results.Ok(new AcceptedOrder(order.Reference!, order.Quantity));
});

// Neither path: an ordinary fault, which must stay a 500 and must not be reshaped into a validation
// problem. The recipe used to convert this into a misleading 404-about-a-500.
app.MapGet("/boom", () => {
    throw new InvalidOperationException("nothing to do with validation.");
});

app.Run();

/// <summary>Exposed so ApiDemo.Tests can drive the real pipeline through WebApplicationFactory.</summary>
public partial class Program;
