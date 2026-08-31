namespace ValidationModules;

/// <summary>
/// Runs the element type's business rules per element of a collection, pathing errors by index -
/// the async half of <see cref="CollectionValidatorFor{TElement}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered scoped, because the element rules it wraps are: an
/// <see cref="IAsyncValidatorFor{T}"/> is hand-written and free to take a DbContext. Elements are
/// awaited sequentially for the reason <see cref="ValidationRunner{T}"/> awaits its rules
/// sequentially - error ordering stays deterministic.
/// </para>
/// <para>
/// <see cref="ValidationRunner{T}"/>'s gate applies to the whole list: business rules run only
/// when no element failed structurally, so a uniqueness check never reaches the database for a
/// batch carrying a malformed entry.
/// </para>
/// </remarks>
/// <typeparam name="TElement">The element type, which owns the actual rules.</typeparam>
public sealed class CollectionAsyncValidatorFor<TElement> : IAsyncValidatorFor<IReadOnlyList<TElement>> {

    private readonly IAsyncValidatorFor<TElement>[] _element;

    /// <summary>
    /// Creates the adapter over the element type's business rules.
    /// </summary>
    /// <param name="element">
    /// Every async validator registered for <typeparamref name="TElement"/>, run per element in
    /// registration order.
    /// </param>
    public CollectionAsyncValidatorFor(IEnumerable<IAsyncValidatorFor<TElement>> element) {
        ArgumentNullException.ThrowIfNull(element);

        _element = element as IAsyncValidatorFor<TElement>[] ?? System.Linq.Enumerable.ToArray(element);
    }

    /// <inheritdoc/>
    public async ValueTask ValidateAsync(
        ValidationContext context,
        IReadOnlyList<TElement> value,
        CancellationToken cancellationToken = default) {

        for (var i = 0; i < value.Count; i++) {
            if (value[i] is not { } element) {
                continue;
            }

            var elementContext = context.PushIndex(string.Empty, i);

            for (var v = 0; v < _element.Length; v++) {
                await _element[v].ValidateAsync(elementContext, element, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
