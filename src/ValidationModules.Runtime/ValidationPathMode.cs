namespace ValidationModules;

/// <summary>How an error's field path is rendered.</summary>
public enum ValidationPathMode {

    /// <summary>
    /// The outermost segment, the immediate parent, and nothing between them, so an error four
    /// levels down reads <c>body...address.postalCode</c>. Allocates one string per error and no
    /// more, which is what keeps a failing pass proportional to its failures rather than its depth.
    /// The default.
    /// </summary>
    Bounded = 0,

    /// <summary>
    /// Every segment walked, so the same error reads <c>body.order.address.postalCode</c>. Costs a
    /// longer string per error; worth it where documents are deep and the reader needs to find the
    /// exact one that failed - manifests, config files, batch imports.
    /// </summary>
    Full = 1
}
