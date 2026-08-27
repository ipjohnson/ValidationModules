namespace ValidationModules;

/// <summary>
/// What a conditional block returns: the one thing that can legally follow it.
/// </summary>
/// <remarks>
/// <para>
/// A distinct type rather than <see cref="ValidationRules{T}"/> so that <c>Otherwise</c> can only
/// be written where it means something. Returning the builder would make
/// <c>rules.Required(…).Otherwise(…)</c> compile, and it has no reading.
/// </para>
/// <para>
/// <c>Otherwise</c> reuses the block's own condition negated rather than taking a second predicate,
/// so the two halves cannot drift apart and the condition is still evaluated once per pass.
/// </para>
/// </remarks>
/// <typeparam name="T">The type being described.</typeparam>
public sealed class ConditionalRules<T> {
    private readonly ValidationRules<T> _owner;
    private readonly Func<T, bool> _condition;
    private readonly bool _negated;

    internal ConditionalRules(ValidationRules<T> owner, Func<T, bool> condition, bool negated) {
        _owner = owner;
        _condition = condition;
        _negated = negated;
    }

    /// <summary>
    /// Declares the rules that apply when the block's condition does not hold.
    /// </summary>
    /// <param name="rules">Runs immediately, exactly as the block's own body does.</param>
    /// <returns>The builder, so declaration continues normally.</returns>
    public ValidationRules<T> Otherwise(Action rules) {
        _owner.Block(_condition, rules, negated: !_negated);

        return _owner;
    }
}
