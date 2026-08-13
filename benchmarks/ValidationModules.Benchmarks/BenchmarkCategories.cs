namespace ValidationModules.Benchmarks;

/// <summary>
/// The three questions this suite answers, as BenchmarkDotNet category names.
/// </summary>
/// <remarks>
/// Constants rather than literals because they are typed on the command line
/// (<c>--anyCategories=component</c>) and a typo in an attribute silently produces an empty run
/// rather than an error.
/// </remarks>
public static class BenchmarkCategories {

    /// <summary>
    /// What a runtime primitive costs on its own: a push, an add, a collector reset. Read these
    /// when a number in <see cref="EndToEnd"/> looks wrong and you need to know which part owns it.
    /// </summary>
    public const string Component = "component";

    /// <summary>
    /// A whole validation pass through generated code, plus the machinery around it - the runner,
    /// the container, the per-request pooling shape. This is the number a consumer feels.
    /// </summary>
    public const string EndToEnd = "endtoend";

    /// <summary>
    /// Comparisons between shapes the generator could emit, only one of which it does. These exist
    /// to settle emitter design questions and are the reason several of them measure hand-written
    /// stand-ins rather than generated output.
    /// </summary>
    public const string Design = "design";
}
