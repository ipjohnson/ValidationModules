using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ValidationModules.Constraints;
using Xunit;

namespace ValidationModules.AspNetCore.Tests;

/// <summary>
/// The filter driven over real HTTP, because the thing being promised is a response.
/// </summary>
/// <remarks>
/// A unit test over <c>InvokeAsync</c> would exercise the branch and prove nothing about status
/// codes, content types or the shape a client parses - which is the entire value of the package.
/// </remarks>
public class EndpointFilterTests {

    private static HttpClient Server(Action<IServiceCollection>? configure = null) {
        var builder = new HostBuilder().ConfigureWebHost(web => {
            web.UseTestServer();
            web.ConfigureServices(services => {
                services.AddRouting();
                services.AddValidationModulesAspNetCoreTestsValidators();
                configure?.Invoke(services);
            });
            web.Configure(app => {
                app.UseRouting();
                app.UseEndpoints(endpoints => {
                    endpoints.MapPost("/orders", (CreateOrder order) => Results.Ok(new { accepted = true }))
                        .Validate<CreateOrder>();

                    endpoints.MapPost("/orders/unvalidated", (CreateOrder order) => Results.Ok(new { accepted = true }));

                    endpoints.MapPost("/coupons", (Coupon coupon) => Results.Ok(new { accepted = true }))
                        .Validate<Coupon>();
                });
            });
        });

        return builder.Start().GetTestClient();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateOrder Valid() => new() { Reference = "ORD-100", Quantity = 3 };

    [Fact]
    public async Task ValidRequest_ReachesTheHandler() {
        using var client = Server();

        var response = await client.PostAsJsonAsync("/orders", Valid(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InvalidRequest_NeverReachesTheHandler() {
        using var client = Server();

        var response = await client.PostAsJsonAsync("/orders", new CreateOrder { Reference = null, Quantity = 9999 }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task InvalidRequest_ReportsFieldsAndMessagesInTheStandardShape() {
        using var client = Server();

        var response = await client.PostAsJsonAsync("/orders", new CreateOrder { Reference = null, Quantity = 9999 }, Ct);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, Ct);

        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.Equal("One or more validation errors occurred.", problem.Title);
        Assert.Equal(["reference is required."], problem.Errors["reference"]);
        Assert.Equal(["quantity must be between 1 and 500."], problem.Errors["quantity"]);
    }

    [Fact]
    public async Task InvalidRequest_CarriesTheMachineReadableCodes() {
        // The reason this package does not just call Results.ValidationProblem: the codes are the
        // stable vocabulary, and dropping them at the HTTP boundary leaves a client parsing English.
        using var client = Server();

        var response = await client.PostAsJsonAsync("/orders", new CreateOrder { Reference = null, Quantity = 9999 }, Ct);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, Ct);

        Assert.NotNull(problem);
        Assert.NotNull(problem.ValidationCodes);
        Assert.Equal([ValidationCodes.Required], problem.ValidationCodes["reference"]);
        Assert.Equal([ValidationCodes.Range], problem.ValidationCodes["quantity"]);
    }

    [Fact]
    public async Task CodesCanBeTurnedOff() {
        using var client = Server(services =>
            services.AddValidationProblemDetails(options => options.IncludeCodes = false));

        var response = await client.PostAsJsonAsync("/orders", new CreateOrder { Reference = null, Quantity = 1 }, Ct);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, Ct);

        Assert.NotNull(problem);
        Assert.Null(problem.ValidationCodes);
        Assert.NotEmpty(problem.Errors);
    }

    [Fact]
    public async Task TitleAndStatusAreConfigurable() {
        using var client = Server(services =>
            services.AddValidationProblemDetails(options => {
                options.Title = "Nope";
                options.StatusCode = 422;
            }));

        var response = await client.PostAsJsonAsync("/orders", new CreateOrder { Reference = null, Quantity = 1 }, Ct);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, Ct);

        Assert.NotNull(problem);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Nope", problem.Title);
    }

    [Fact]
    public async Task NestedAndCollectionPathsSurviveIntoTheResponse() {
        // The paths are the library's output; this asserts nothing mangles them on the way out.
        using var client = Server();

        var response = await client.PostAsJsonAsync("/orders", new CreateOrder {
            Reference = "ORD-1",
            Quantity = 1,
            ShipTo = new Address { Postcode = null },
            Lines = [new OrderLine { Sku = "OK" }, new OrderLine { Sku = null }],
        }, Ct);

        var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, Ct);

        Assert.NotNull(problem);
        Assert.Contains("shipTo.postcode", problem.Errors.Keys);
        Assert.Contains("lines[1].sku", problem.Errors.Keys);
    }

    [Fact]
    public async Task AnEndpointWithoutTheFilter_IsUntouched() {
        // The filter is opt-in per endpoint, so an unvalidated route must still accept junk.
        using var client = Server();

        var response = await client.PostAsJsonAsync("/orders/unvalidated", new CreateOrder { Reference = null }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WarningsDoNotFailARequest() {
        // IsValid is false only for Error severity, and a request that was not rejected must not
        // be told it was.
        using var client = Server(services =>
            services.AddSingleton<IValidatorFor<Coupon>, WarnOnlyCouponValidator>());

        var response = await client.PostAsJsonAsync("/coupons", new Coupon { Code = "SAVE" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AsyncBusinessRules_RunThroughTheFilter() {
        // The filter prefers ValidationRunner<T> precisely so this happens. Resolving the plain
        // IValidatorFor<T> instead would skip business rules and nobody would notice until a
        // duplicate landed in the database.
        using var client = Server(services =>
            services.AddScoped<IAsyncValidatorFor<CreateOrder>, RejectingBusinessRule>());

        var response = await client.PostAsJsonAsync("/orders", Valid(), Ct);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, Ct);

        Assert.NotNull(problem);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["reference is already taken."], problem.Errors["reference"]);
    }

    [Fact]
    public async Task NamingATypeTheHandlerDoesNotTake_Fails() {
        // The whole point of the check: this endpoint would otherwise answer every body as valid,
        // forever, with nothing in a build or a test run to say so.
        //
        // It fails when the endpoint is built, which minimal APIs do lazily - so the failure lands
        // on the first request rather than at boot. See EndpointBuildIsLazy_SoTheCheckIsNotAtBoot.
        using var client = Endpoints(endpoints =>
            endpoints.MapPost("/orders", (CreateOrder order) => Results.Ok())
                .Validate<Coupon>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostAsJsonAsync("/orders", Valid(), Ct));

        Assert.Contains("takes no", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Coupon), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CreateOrder), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHandlerWithNoParametersAtAll_Fails() {
        using var client = Endpoints(endpoints =>
            endpoints.MapPost("/ping", () => Results.Ok()).Validate<CreateOrder>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostAsJsonAsync("/ping", Valid(), Ct));

        Assert.Contains("it takes no parameters", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMismatchAnywhere_FailsTheFirstRequestToEveryEndpoint() {
        // Endpoint build is all-or-nothing: RouteEndpointDataSource.get_Endpoints() constructs
        // every endpoint in the application, so one bad Validate<T>() cannot hide behind a route
        // nobody calls. The blast radius is the point - it is what makes a smoke test or a health
        // probe enough to catch this, rather than needing traffic on the affected route.
        using var client = Endpoints(endpoints =>
            endpoints.MapPost("/orders", (CreateOrder order) => Results.Ok())
                .Validate<Coupon>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostAsJsonAsync("/healthy", Valid(), Ct));

        Assert.Contains(nameof(Coupon), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AParameterDeclaredAsABaseType_IsAccepted() {
        // The filter matches on `is T`, so a parameter that could hold a T at run time must not be
        // rejected - turning a working endpoint into a failure is the worse trade.
        using var client = Endpoints(endpoints =>
            endpoints.MapPost("/loose", (object body) => Results.Ok(new { accepted = true }))
                .Validate<CreateOrder>());

        var response = await client.PostAsJsonAsync("/loose", Valid(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AGroupSkipsHandlersThatTakeNoT_RatherThanThrowing() {
        // A group is a mixed bag by construction: the POST validates, the GET has nothing to
        // validate and must still boot and still work.
        using var client = Endpoints(endpoints => {
            var group = endpoints.MapGroup("/catalogue").Validate<CreateOrder>();

            group.MapPost("/orders", (CreateOrder order) => Results.Ok(new { accepted = true }));
            group.MapGet("/orders/{id}", (string id) => Results.Ok(new { id }));
        });

        var rejected = await client.PostAsJsonAsync(
            "/catalogue/orders", new CreateOrder { Reference = null }, Ct);
        var untouched = await client.GetAsync("/catalogue/orders/abc", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, untouched.StatusCode);
    }

    /// <summary>
    /// Builds a server over a caller-supplied route table, for the cases that are about wiring
    /// rather than about a response.
    /// </summary>
    private static HttpClient Endpoints(Action<IEndpointRouteBuilder> routes) {
        var builder = new HostBuilder().ConfigureWebHost(web => {
            web.UseTestServer();
            web.ConfigureServices(services => {
                services.AddRouting();
                services.AddValidationModulesAspNetCoreTestsValidators();
            });
            web.Configure(app => {
                app.UseRouting();
                app.UseEndpoints(endpoints => {
                    // An endpoint with nothing wrong with it, so a test can tell "the host is up"
                    // from "the host refused to start".
                    endpoints.MapPost("/healthy", (CreateOrder order) => Results.Ok(new { ok = true }));

                    routes(endpoints);
                });
            });
        });

        return builder.Start().GetTestClient();
    }

    private sealed class RejectingBusinessRule : IAsyncValidatorFor<CreateOrder> {
        public async ValueTask ValidateAsync(
            ValidationContext context, CreateOrder value, CancellationToken cancellationToken = default) {
            await Task.Yield();

            context.Add("reference", "conflict", "reference is already taken.");
        }
    }

    private sealed class WarnOnlyCouponValidator : IValidatorFor<Coupon> {
        public void Validate(ref ValidationContext context, Coupon value) =>
            context.Add("code", "deprecated", "this coupon format is being retired.", ValidationSeverity.Warning);
    }

    private sealed record Problem {
        public string? Title { get; init; }
        public int Status { get; init; }
        public Dictionary<string, string[]> Errors { get; init; } = new();

        [JsonPropertyName("validationCodes")]
        public Dictionary<string, string[]>? ValidationCodes { get; init; }
    }
}

public sealed record CreateOrder {
    [Required, StringLength(min: 3, max: 40)]
    public string? Reference { get; init; }

    [Range(1, 500)]
    public int Quantity { get; init; }

    [ValidateNested]
    public Address? ShipTo { get; init; }

    [ValidateNested]
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];
}

public sealed record Address {
    [Required]
    public string? Postcode { get; init; }
}

public sealed record OrderLine {
    [Required]
    public string? Sku { get; init; }
}

/// <summary>A type with no constraints, so only the hand-written validator applies to it.</summary>
public sealed record Coupon {
    public string? Code { get; init; }
}
