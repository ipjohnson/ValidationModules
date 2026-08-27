using System.Buffers;

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

    /// <summary>
    /// Materialised once, and held as arrays rather than as the injected sequences.
    /// </summary>
    /// <remarks>
    /// <c>foreach</c> over an array typed as <see cref="IEnumerable{T}"/> boxes its enumerator -
    /// measured at exactly 32 bytes per call, and the async path pays it twice. A runner is built
    /// once per scope and used per request, so copying here is paid once and the per-call
    /// allocation goes to zero. The generated validators already hold their nested sets this way,
    /// for the same reason.
    /// </remarks>
    private readonly IValidatorFor<T>[] _structural;
    private readonly IAsyncValidatorFor<T>[] _business;

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

        _structural = structural as IValidatorFor<T>[] ?? System.Linq.Enumerable.ToArray(structural);
        _business = business as IAsyncValidatorFor<T>[] ?? System.Linq.Enumerable.ToArray(business);
    }

    /// <summary>
    /// Runs the structural validators only. Allocation-free when the value is clean.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    public ValidationResult Validate(T value) {
        var collector = new ValidationErrorCollector();
        var path = ArrayPool<PathSegment>.Shared.Rent(ValidationErrorCollector.DefaultDepthLimit);

        try {
            RunStructural(collector, path, value);

            return collector.ToResult();
        }
        finally {
            ArrayPool<PathSegment>.Shared.Return(path);
        }
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
        var path = ArrayPool<PathSegment>.Shared.Rent(ValidationErrorCollector.DefaultDepthLimit);

        try {
            RunStructural(collector, path, value);

            // Blocking errors, not any error. A structural rule that reports a warning has
            // accepted the value - the error model says so, and IsValid agrees - so gating on
            // HasErrors here silently skipped every business rule for a request that was valid.
            // The gate itself is deliberate: do not spend a round trip checking whether a policy
            // number exists when the policy number is malformed.
            if (!collector.HasBlockingErrors) {
                var context = new ValidationContext(collector, path);

                for (var i = 0; i < _business.Length; i++) {
                    await _business[i].ValidateAsync(context, value, cancellationToken).ConfigureAwait(false);
                }
            }

            return collector.ToResult();
        }
        finally {
            ArrayPool<PathSegment>.Shared.Return(path);
        }
    }

    private void RunStructural(ValidationErrorCollector collector, PathSegment[] path, T value) {
        for (var i = 0; i < _structural.Length; i++) {
            var context = new ValidationContext(collector, path);

            _structural[i].Validate(ref context, value);
        }
    }
}
