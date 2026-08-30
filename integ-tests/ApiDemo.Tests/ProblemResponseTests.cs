using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ApiDemo.Tests;

/// <summary>
/// What a client actually receives from the documented wiring.
/// </summary>
/// <remarks>
/// Every assertion here is about a response over real HTTP - status, content type, and body -
/// because that is the whole of what this package promises. The cases that matter most are the ones
/// no unit test can reach: a body that never parsed, and a fault that has nothing to do with
/// validation. Both travel through middleware the test hosts elsewhere in this repository do not
/// build.
/// </remarks>
public class ProblemResponseTests {

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CreateOrder Valid() => new() {
        Reference = "ORD-100",
        Quantity = 3,
        ShipTo = new Address { Postcode = "SW1A 1AA" },
        Lines = [new OrderLine { Sku = "SKU-1" }],
    };

    private static StringContent Malformed() =>
        new("{\"reference\": \"ORD-1\", \"quantity\": ", Encoding.UTF8, "application/json");

    private static string[] CodesFor(Problem problem, string field) {
        Assert.NotNull(problem.ValidationCodes);
        return Assert.Contains(field, problem.ValidationCodes);
    }

    private static async Task<Problem> ProblemFrom(HttpResponseMessage response) {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.False(string.IsNullOrWhiteSpace(body), "the response carried no body at all.");
        return JsonSerializer.Deserialize<Problem>(body, Json)
            ?? throw new InvalidOperationException($"not a problem document: {body}");
    }

    // ---- the filter path ------------------------------------------------------------------

    [Fact]
    public async Task ValidBody_ReachesTheHandler() {
        using var api = DemoApi.Development();
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/orders", Valid(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InvalidBody_IsAProblemDocumentCarryingPathsAndCodes() {
        using var api = DemoApi.Development();
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/orders", new CreateOrder {
            Reference = "x",
            Quantity = 0,
            ShipTo = new Address(),
            Lines = [new OrderLine()],
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ProblemFrom(response);
        Assert.Equal(400, problem.Status);
        Assert.Contains("reference", problem.Errors.Keys);
        Assert.Contains("shipTo.postcode", problem.Errors.Keys);
        Assert.Contains("lines[0].sku", problem.Errors.Keys);
        Assert.Equal(["required"], CodesFor(problem, "shipTo.postcode"));
    }

    /// <summary>
    /// Two endpoints in one app answering different statuses: /orders keeps the application-wide
    /// 400 while /orders/strict overrides to 422 - and each body's type member matches its own
    /// status rather than pointing every failure at the definition of 400.
    /// </summary>
    [Fact]
    public async Task PerEndpointStatus_OverridesOneEndpointAndItsTypeMember() {
        using var api = DemoApi.Development();
        using var client = api.CreateClient();

        var invalid = new CreateOrder { Reference = "x", Quantity = 0 };

        var wide = await client.PostAsJsonAsync("/orders", invalid, Ct);
        var strict = await client.PostAsJsonAsync("/orders/strict", invalid, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, strict.StatusCode);

        var wideProblem = await ProblemFrom(wide);
        Assert.Equal(400, wideProblem.Status);
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.1", wideProblem.Type);

        var strictProblem = await ProblemFrom(strict);
        Assert.Equal(422, strictProblem.Status);
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.21", strictProblem.Type);
        Assert.Contains("reference", strictProblem.Errors.Keys);
        Assert.Equal(["string_length"], CodesFor(strictProblem, "reference"));
    }

    /// <summary>
    /// A type-level failure lands under the empty key, in both dictionaries. The guide's response
    /// sample never showed one, so nothing pinned it.
    /// </summary>
    [Fact]
    public async Task ObjectLevelFailure_UsesTheEmptyFieldKey() {
        using var api = DemoApi.Development();
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/orders", Valid() with { Quantity = 200, Lines = [] }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ProblemFrom(response);
        Assert.Contains("", problem.Errors.Keys);
        Assert.Equal(["bulk_needs_lines"], CodesFor(problem, ""));
    }

    // ---- the thrown path ------------------------------------------------------------------

    [Fact]
    public async Task ValidateAndThrow_DeeperIn_ProducesTheSameShape() {
        using var api = DemoApi.Development();
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/orders/deep", new CreateOrder { Reference = "x", Quantity = 0 }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ProblemFrom(response);
        Assert.Contains("reference", problem.Errors.Keys);
    }

    // ---- the paths the recipe used to break ------------------------------------------------

    /// <summary>
    /// The body never parsed, so validation never ran. This has to be a 400 a client can read -
    /// the documented recipe used to turn it into a 500 carrying a message about a 404.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task MalformedJson_IsABadRequest(string environment) {
        using var api = new DemoApi(environment);
        using var client = api.CreateClient();

        var response = await client.PostAsync("/orders", Malformed(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ProblemFrom(response);
        Assert.Equal(400, problem.Status);
    }

    /// <summary>
    /// An ordinary fault must stay a 500 and must not be dressed up as a validation problem - the
    /// handler declines it, and something downstream still has to render it.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task NonValidationException_StaysAServerError(string environment) {
        using var api = new DemoApi(environment);
        using var client = api.CreateClient();

        var response = await client.GetAsync("/boom", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var problem = await ProblemFrom(response);
        Assert.Equal(500, problem.Status);
        Assert.Empty(problem.Errors);
    }

    private sealed record Problem {
        public string? Type { get; init; }
        public string? Title { get; init; }
        public int Status { get; init; }
        public Dictionary<string, string[]> Errors { get; init; } = new();

        [JsonPropertyName("validationCodes")]
        public Dictionary<string, string[]>? ValidationCodes { get; init; }
    }
}
