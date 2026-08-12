namespace ValidationModules.Constraints;

/// <summary>
/// The collection's element count must fall within bounds. Emits code <c>array_bounds</c>.
/// </summary>
/// <remarks>
/// Constrains the collection itself. To validate the elements, add
/// <see cref="ValidateNestedAttribute"/> as well - the two coexist on one property.
/// </remarks>
public sealed class ItemCountAttribute : ValidationConstraintAttribute {

    /// <summary>
    /// Bounds set through <see cref="Min"/> and <see cref="Max"/>, for declaring only one of them.
    /// </summary>
    public ItemCountAttribute() { }

    /// <summary>
    /// Bounds set positionally.
    /// </summary>
    /// <param name="min">Fewest permitted elements, inclusive.</param>
    /// <param name="max">Most permitted elements, inclusive.</param>
    public ItemCountAttribute(int min, int max) {
        Min = min;
        Max = max;
    }

    /// <summary>Fewest permitted elements, inclusive.</summary>
    public int Min { get; init; }

    /// <summary>Most permitted elements, inclusive. Defaults to unbounded.</summary>
    public int Max { get; init; } = int.MaxValue;
}
