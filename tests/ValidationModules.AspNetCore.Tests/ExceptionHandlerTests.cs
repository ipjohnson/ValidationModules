using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ValidationModules.AspNetCore;
using Xunit;

namespace ValidationModules.AspNetCore.Tests;

/// <summary>
/// The two handlers <c>AddValidationProblemDetails</c> registers, driven directly rather than
/// through a pipeline.
/// </summary>
/// <remarks>
/// Both are reached in the wild only through <c>UseExceptionHandler</c>, which is why
/// <c>integ-tests/ApiDemo</c> covers them over real HTTP. These pin the decisions each one makes on
/// its own: which exceptions it claims, what status it leaves behind, and what it declines.
/// </remarks>
public class ExceptionHandlerTests {

    private static DefaultHttpContext Context() {
        var context = new DefaultHttpContext {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };

        context.Response.Body = new MemoryStream();

        return context;
    }

    private static string BodyOf(HttpContext context) {
        context.Response.Body.Position = 0;

        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static ValidationResult OneFailure() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        context.ReportRequired("name", value: null);

        return collector.ToResult();
    }

    // -- ValidationExceptionHandler -----------------------------------------------------------

    [Fact]
    public async Task ValidationException_IsAnsweredAsProblemDetails() {
        var handler = new ValidationExceptionHandler(Options.Create(new ValidationProblemOptions()));
        var context = Context();

        var handled = await handler.TryHandleAsync(
            context, new ValidationException(OneFailure()), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        using var document = JsonDocument.Parse(BodyOf(context));

        Assert.Equal(
            "name is required.",
            document.RootElement.GetProperty("errors").GetProperty("name")[0].GetString());
    }

    [Fact]
    public async Task AnythingElse_IsDeclined() {
        // Declining is what lets the application's own handler answer. Claiming every exception
        // would turn an unrelated fault into a validation response.
        var handler = new ValidationExceptionHandler(Options.Create(new ValidationProblemOptions()));
        var context = Context();

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException("unrelated"), CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(BodyOf(context));
    }

    [Fact]
    public async Task TheConfiguredStatusCode_IsUsed() {
        var options = Options.Create(new ValidationProblemOptions {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
        });

        var context = Context();

        await new ValidationExceptionHandler(options).TryHandleAsync(
            context, new ValidationException(OneFailure()), CancellationToken.None);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, context.Response.StatusCode);
    }

    // -- BadRequestStatusHandler --------------------------------------------------------------

    [Fact]
    public async Task ABadRequest_KeepsTheStatusItAlreadyCarried() {
        // The middleware sets 500 before any handler runs and never reads the exception's own
        // status, so without this a body that never parsed comes back as a server fault.
        var handler = new BadRequestStatusHandler();
        var context = Context();

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var handled = await handler.TryHandleAsync(
            context, new BadHttpRequestException("malformed", StatusCodes.Status400BadRequest),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

#if NET9_0_OR_GREATER
        // From .NET 9 the middleware renders from the status the response now holds, so setting it
        // and declining leaves the application's own problem-details customisation in charge.
        Assert.False(handled);
        Assert.Empty(BodyOf(context));
#else
        // On .NET 8 the middleware would overwrite the status again, so the body is written here.
        Assert.True(handled);
        using var document = JsonDocument.Parse(BodyOf(context));
        Assert.Equal("Bad Request", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
#endif
    }

    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status415UnsupportedMediaType)]
    [InlineData(StatusCodes.Status431RequestHeaderFieldsTooLarge)]
    public async Task EveryStatusABadRequestCarries_Survives(int statusCode) {
        var context = Context();

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        await new BadRequestStatusHandler().TryHandleAsync(
            context, new BadHttpRequestException("rejected", statusCode), CancellationToken.None);

        Assert.Equal(statusCode, context.Response.StatusCode);
    }

    [Fact]
    public async Task AnExceptionThatIsNotABadRequest_IsDeclinedAndChangesNothing() {
        var context = Context();

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var handled = await new BadRequestStatusHandler().TryHandleAsync(
            context, new InvalidOperationException("unrelated"), CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    // -- ValidationProblemOptions.WithFormatterFrom -------------------------------------------

    [Fact]
    public void AFormatterOnTheOptions_WinsOverOneInTheContainer() {
        // The explicit one was configured for this boundary; a registered formatter is the default
        // for everything that did not say.
        var mine = new ValidationMessageMap();
        var options = new ValidationProblemOptions { MessageFormatter = mine };

        var services = new ServiceCollection()
            .AddSingleton<ValidationMessageFormatter>(new ValidationMessageMap())
            .BuildServiceProvider();

        Assert.Same(options, options.WithFormatterFrom(services));
        Assert.Same(mine, options.WithFormatterFrom(services).MessageFormatter);
    }

    [Fact]
    public void AFormatterInTheContainer_IsPickedUpWithoutLosingTheOtherOptions() {
        var registered = new ValidationMessageMap();

        var services = new ServiceCollection()
            .AddSingleton<ValidationMessageFormatter>(registered)
            .BuildServiceProvider();

        var options = new ValidationProblemOptions {
            Title = "Nope.",
            Type = "https://example.test/errors",
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            IncludeCodes = false,
            IncludeNonErrors = true,
        };

        var resolved = options.WithFormatterFrom(services);

        Assert.NotSame(options, resolved);
        Assert.Same(registered, resolved.MessageFormatter);
        Assert.Equal("Nope.", resolved.Title);
        Assert.Equal("https://example.test/errors", resolved.Type);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, resolved.StatusCode);
        Assert.False(resolved.IncludeCodes);
        Assert.True(resolved.IncludeNonErrors);
    }

    [Fact]
    public void NoFormatterAnywhere_LeavesTheOptionsAlone() {
        var options = new ValidationProblemOptions();

        Assert.Same(options, options.WithFormatterFrom(new ServiceCollection().BuildServiceProvider()));
    }
}
