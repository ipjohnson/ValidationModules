namespace ValidationModules;

/// <summary>
/// Runs every validator registered for <typeparamref name="T"/> and merges what they find.
/// </summary>
/// <remarks>
/// <para>
/// This is where the composition policy lives, so that consumers do not each reimplement it
/// slightly differently: all registered validators run, their results merge rather than one
/// replacing another, and the async ones run only if structural validation produced no error.
/// </para>
/// <para>
/// Registered closed, per validated type, by the generator - never as an open generic, which
/// would route construction through MS.DI's reflection-based activation. It is also directly
/// constructible, which is how a request filter resolves its validators once at handler
/// construction instead of per request.
/// </para>
/// </remarks>
/// <typeparam name="T">The type being validated.</typeparam>
public sealed class ValidationRunner<T> {
    private readonly IEnumerable<IValidatorFor<T>> _structural;
    private readonly IEnumerable<IAsyncValidatorFor<T>> _business;

    /// <summary>
    /// The scope this runner was resolved from, handed to every context it starts so that
    /// descending into a nested object can pick up validators registered for that type.
    /// </summary>

    /// <summary>
    /// Creates a runner over the validators registered for <typeparamref name="T"/>.
    /// </summary>
    /// <param name="structural">Generated constraint validators. Usually one.</param>
    /// <param name="business">Hand-written rules that need I/O. Often none.</param>
    public ValidationRunner(
        IEnumerable<IValidatorFor<T>> structural,
        IEnumerable<IAsyncValidatorFor<T>> business) {
        ArgumentNullException.ThrowIfNull(structural);
        ArgumentNullException.ThrowIfNull(business);

        _structural = structural;
        _business = business;
    }

    /// <summary>
    /// Runs the structural validators only. Allocation-free when the value is clean.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    public ValidationResult Validate(T value) {
        var collector = new ValidationErrorCollector();

        RunStructural(collector, value);

        return collector.ToResult();
    }

    /// <summary>
    /// Runs the structural validators, then - only if they found no error - the business rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate is deliberate: a uniqueness check should not reach the database for a field that
    /// was null.
    /// </para>
    /// <para>
    /// Business rules are awaited sequentially, so error ordering across validators stays
    /// deterministic. A rule that fans out internally is the author's own choice, and its own
    /// errors land in completion order.
    /// </para>
    /// </remarks>
    /// <param name="value">The value to validate.</param>
    /// <param name="cancellationToken">Cancels any I/O the business rules perform.</param>
    public async ValueTask<ValidationResult> ValidateAsync(
        T value,
        CancellationToken cancellationToken = default) {

        var collector = new ValidationErrorCollector();

        RunStructural(collector, value);

        if (!collector.HasErrors) {
            var context = new ValidationContext(collector);

            foreach (var validator in _business) {
                await validator.ValidateAsync(context, value, cancellationToken).ConfigureAwait(false);
            }
        }

        return collector.ToResult();
    }

    private void RunStructural(ValidationErrorCollector collector, T value) {
        foreach (var validator in _structural) {
            var context = new ValidationContext(collector);

            validator.Validate(ref context, value);
        }
    }
}
