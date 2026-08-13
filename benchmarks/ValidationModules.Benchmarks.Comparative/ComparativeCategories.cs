namespace ValidationModules.Benchmarks.Comparative;

/// <summary>
/// The comparison axes, as BenchmarkDotNet category names.
/// </summary>
public static class ComparativeCategories {

    /// <summary>A flat payload: five rules, no nesting. The cleanest engine-against-engine reading.</summary>
    public const string Flat = "flat";

    /// <summary>An object graph two levels deep with a collection in it.</summary>
    public const string Nested = "nested";

    /// <summary>How each engine scales with the number of collection elements.</summary>
    public const string Collection = "collection";

    /// <summary>
    /// What each engine costs before it validates anything - building the rule graph, compiling
    /// accessors, warming a reflection cache.
    /// </summary>
    public const string Startup = "startup";

    /// <summary>Registration and per-request resolution through the container.</summary>
    public const string DependencyInjection = "di";
}
