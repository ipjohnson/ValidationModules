using Microsoft.Extensions.Options;
using ValidationModules;
using ValidationModules.Options;

// ReSharper disable once CheckNamespace - MS convention: DI extensions live in the DI namespace
// so that a composition root that has already imported it finds them without a second using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Wires the generated validators into the options pipeline.
/// </summary>
public static class ValidationModulesOptionsExtensions {

    /// <summary>
    /// Registers <typeparamref name="TOptions"/> with validation at host startup: every
    /// <see cref="IValidatorFor{T}"/> registered for it runs through
    /// <see cref="IValidateOptions{TOptions}"/>, and <c>ValidateOnStart()</c> makes a failure
    /// refuse the host rather than surface on first use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The complement to <c>[OptionsValidator]</c> for a model whose rules are already declared
    /// for ValidationModules - one set of constraints validates the request body and the
    /// configuration section alike. Failures render as <c>field [code] message</c>, one line per
    /// error, in the <see cref="OptionsValidationException"/> the host throws.
    /// </para>
    /// <para>
    /// Returns the <see cref="OptionsBuilder{TOptions}"/>, so binding chains as usual:
    /// <c>services.AddValidatedOptions&lt;HubOptions&gt;().BindConfiguration("Hub")</c>.
    /// Structural validators only - an <see cref="IAsyncValidatorFor{T}"/> needs I/O and a scope,
    /// and <see cref="IValidateOptions{TOptions}"/> offers neither.
    /// </para>
    /// <para>
    /// Registering no validator for <typeparamref name="TOptions"/> fails validation outright:
    /// asking for validated options and validating nothing would be the silent failure this
    /// library exists to remove. Call the generated <c>Add…Validators()</c> before the host
    /// builds.
    /// </para>
    /// </remarks>
    /// <typeparam name="TOptions">The options type, carrying its constraints.</typeparam>
    /// <param name="services">The collection to add to.</param>
    /// <param name="name">The options instance to validate; null for the default instance.</param>
    public static OptionsBuilder<TOptions> AddValidatedOptions<TOptions>(
        this IServiceCollection services, string? name = null)
        where TOptions : class {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<TOptions>(name);

        services.AddSingleton<IValidateOptions<TOptions>>(provider =>
            new ValidatorForValidateOptions<TOptions>(
                builder.Name, provider.GetServices<IValidatorFor<TOptions>>()));

        return builder.ValidateOnStart();
    }
}
