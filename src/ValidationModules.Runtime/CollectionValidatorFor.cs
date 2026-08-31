namespace ValidationModules;

/// <summary>
/// Validates each element of a collection with the validators registered for the element type,
/// pathing errors by index: <c>[2].quantity</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a top-level JSON array body an ordinary endpoint:
/// <c>.Validate&lt;List&lt;TicketOrder&gt;&gt;()</c> resolves a validator for the list itself, and
/// this class is that validator. The generated <c>Add…Validators()</c> registers one per validated
/// type, for <c>List&lt;T&gt;</c> and <c>T[]</c> - the two shapes a minimal API body parameter is
/// ordinarily declared as.
/// </para>
/// <para>
/// Implemented against <see cref="IReadOnlyList{T}"/> and registered under the concrete service
/// shapes through <see cref="IValidatorFor{T}"/>'s contravariance, so one class serves both
/// registrations with no reflection anywhere - the closed generic is named in the generated
/// registration, which is what keeps the path alive under Native AOT.
/// </para>
/// <para>
/// A null element is skipped, exactly as the generated walk over a <c>[ValidateNested]</c>
/// collection property skips one: null has nothing to validate, and inventing an error whose
/// meaning the element type owns is not this class's call.
/// </para>
/// </remarks>
/// <typeparam name="TElement">The element type, which owns the actual rules.</typeparam>
public sealed class CollectionValidatorFor<TElement> : IValidatorFor<IReadOnlyList<TElement>> {

    private readonly IValidatorFor<TElement>[] _element;

    /// <summary>
    /// Creates the validator over the element type's validators.
    /// </summary>
    /// <param name="element">
    /// Every validator registered for <typeparamref name="TElement"/>, run per element in
    /// registration order - the same merge <see cref="ValidationRunner{T}"/> applies at the top.
    /// </param>
    public CollectionValidatorFor(IEnumerable<IValidatorFor<TElement>> element) {
        ArgumentNullException.ThrowIfNull(element);

        _element = element as IValidatorFor<TElement>[] ?? System.Linq.Enumerable.ToArray(element);
    }

    /// <inheritdoc/>
    public ValidationFlow Validate(ref ValidationContext context, IReadOnlyList<TElement> value) {
        for (var i = 0; i < value.Count; i++) {
            if (value[i] is not { } element) {
                continue;
            }

            // An empty segment name puts the index alone at the head of the path, so a top-level
            // array's errors read [2].quantity rather than inventing a field name for the body.
            var elementContext = context.PushIndex(string.Empty, i);

            for (var v = 0; v < _element.Length; v++) {
                if (_element[v].Validate(ref elementContext, element).ShouldStop) {
                    return ValidationFlow.Stop;
                }
            }
        }

        return ValidationFlow.Continue;
    }
}
