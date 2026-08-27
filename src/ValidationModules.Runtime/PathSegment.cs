namespace ValidationModules;

/// <summary>
/// One step of the path a pass has walked: the member, and the index or key it was reached through.
/// </summary>
/// <remarks>
/// Deliberately a struct in a flat array rather than <c>object[]</c>: the index stays an
/// <see cref="int"/> and is only ever formatted when an error is actually recorded, where
/// <c>object[]</c> would box it on every indexed descent - an allocation per element, which is the
/// one thing a descent must not do.
/// </remarks>
internal struct PathSegment {

    public string Name;

    public string? Key;

    public int Index;

    /// <summary>
    /// When this slot was written, from a counter that only ever goes up. A context remembers the
    /// stamp it was given; if the slot it wrote no longer carries it, or the stamps along its
    /// lineage are no longer increasing, some other descent has overwritten the path underneath it.
    /// </summary>
    public long Stamp;
}
