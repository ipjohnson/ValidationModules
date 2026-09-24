namespace ValidationModules;

/// <summary>
/// Business rules that need I/O - uniqueness checks, cross-aggregate invariants, anything that has
/// to ask a database. Hand-written, registered as scoped, and free to take dependencies.
/// </summary>
/// <remarks>
/// <para>
/// Takes the same <see cref="ValidationContext"/> as the synchronous side, by value. That works
/// because the context is a <c>readonly struct</c> rather than a <c>ref struct</c>, so it can be a
/// parameter of an async method and survives awaits. A pass is still single-threaded:
/// <see cref="ValidationContext"/> explains how to validate branches concurrently.
/// </para>
/// <para>
/// <see cref="ValidationRunner{T}"/> runs these only when structural validation produced no error,
/// so a uniqueness check never reaches the database for a field that was null.
/// </para>
/// </remarks>
/// <typeparam name="T">The type being validated.</typeparam>
public interface IAsyncValidatorFor<in T>
{
    /// <summary>
    /// Validates <paramref name="value"/>, adding any failures to <paramref name="context"/>.
    /// </summary>
    /// <param name="context">Accumulates failures and carries the current field path.</param>
    /// <param name="value">The value to validate.</param>
    /// <param name="cancellationToken">Cancels any I/O the rule performs.</param>
    ValueTask ValidateAsync(
        ValidationContext context,
        T value,
        CancellationToken cancellationToken = default
    );
}
