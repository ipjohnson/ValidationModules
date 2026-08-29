using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ValidationModules.AspNetCore;

/// <summary>
/// Validates the <typeparamref name="T"/> argument of a minimal API handler before the handler
/// runs, and short-circuits with a problem response if it fails.
/// </summary>
/// <remarks>
/// <para>
/// <b>The type is a type argument, and that is the whole design.</b> The obvious way to write this
/// filter is to reflect over the handler's parameters and validate whatever looks validatable,
/// which is exactly the reflection this library exists to remove - and it would reintroduce it at
/// the one layer that runs on every request. Naming <typeparamref name="T"/> at the call site keeps
/// every lookup a closed generic, so nothing here needs
/// <c>MakeGenericType</c> and the whole path survives trimming.
/// </para>
/// <para>
/// <b>The argument is found by pattern match, not by position.</b> Minimal API argument order is
/// the handler's business and changes when a parameter is added; scanning for the first
/// <typeparamref name="T"/> means adding a <c>CancellationToken</c> cannot silently start
/// validating the wrong thing.
/// </para>
/// <para>
/// <b>A missing or null argument is not this filter's failure to report.</b> An absent body is a
/// binding failure and ASP.NET Core has already answered it; an optional parameter that is legally
/// null has nothing to validate. Either way the filter stands aside rather than inventing an error
/// whose field name it would have to guess.
/// </para>
/// <para>
/// <b>Internal, and deliberately.</b> It is only ever constructed by <c>Validate&lt;T&gt;()</c>, and
/// a public constructor is a shape 1.0.0 would pin. The convention registration sketched in
/// <c>docs/deferred-features.md</c> may well want this to take a table entry or a pre-resolved
/// validator rather than options; keeping the type internal leaves that free. Nothing a consumer
/// can write today needs it by name.
/// </para>
/// </remarks>
/// <typeparam name="T">The type to validate.</typeparam>
internal sealed class ValidationEndpointFilter<T> : IEndpointFilter where T : class {
    private readonly ValidationProblemOptions _options;

    /// <summary>
    /// Creates the filter with options resolved from the container.
    /// </summary>
    /// <remarks>
    /// This was public on an internal type until the filter grew a factory:
    /// <c>AddEndpointFilter&lt;TBuilder, TFilter&gt;</c> constructs through
    /// <c>ActivatorUtilities.CreateFactory</c>, which only considers public constructors, and an
    /// internal one failed at startup with "a suitable constructor could not be located".
    /// <see cref="ValidationEndpointFilterFactory"/> calls this directly, so the accessibility can
    /// now say what it means.
    /// </remarks>
    internal ValidationEndpointFilter(IOptions<ValidationProblemOptions> options) {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (Argument(context) is not { } value) {
            return await next(context).ConfigureAwait(false);
        }

        var result = await ValidateAsync(context.HttpContext, value).ConfigureAwait(false);

        return result.IsValid
            ? await next(context).ConfigureAwait(false)
            : ValidationProblem.ToResult(
                result, _options.WithFormatterFrom(context.HttpContext.RequestServices));
    }

    private static T? Argument(EndpointFilterInvocationContext context) {
        for (var i = 0; i < context.Arguments.Count; i++) {
            if (context.Arguments[i] is T match) {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the validators for <typeparamref name="T"/>, preferring the runner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ValidationRunner{T}"/> first, because the generator registers one per validated
    /// type and it is what merges a hand-written <see cref="IAsyncValidatorFor{T}"/> with the
    /// generated structural pass. Taking the plain <see cref="IValidatorFor{T}"/> instead would
    /// silently skip business rules, which is the sort of gap nobody notices until a duplicate
    /// lands in the database.
    /// </para>
    /// <para>
    /// Falling back to <see cref="IValidatorFor{T}"/> covers a type whose validator was registered
    /// by hand without a runner. Neither present means nothing was registered for
    /// <typeparamref name="T"/>, which is a wiring mistake rather than a validation failure, so it
    /// throws rather than passing the request through as valid.
    /// </para>
    /// </remarks>
    private static async ValueTask<ValidationResult> ValidateAsync(HttpContext http, T value) {
        var services = http.RequestServices;

        if (services.GetService<ValidationRunner<T>>() is { } runner) {
            return await runner.ValidateAsync(value, http.RequestAborted).ConfigureAwait(false);
        }

        if (services.GetService<IValidatorFor<T>>() is { } validator) {
            return validator.Validate(value);
        }

        throw new InvalidOperationException(
            $"No validator is registered for {typeof(T)}. Call the generated " +
            "Add<Assembly>Validators() at startup, or register an IValidatorFor<T> by hand. " +
            "Validating nothing and reporting success would be worse than this exception.");
    }
}
