using BenchmarkDotNet.Attributes;

namespace ValidationModules.Benchmarks.Design;

/// <summary>
/// The same two shapes over a collection, which is where they diverge most and where the decision
/// is likely to be made.
/// </summary>
/// <remarks>
/// <para>
/// The shipped log grows one entry per element and never shrinks, so a fresh collector over 1000
/// elements doubles its buffer from 16 to 1024 and discards every intermediate - measured at
/// 49 KB in <c>EndToEnd/CollectionScalingBenchmarks</c>. Reusing the collector makes that nothing,
/// because <c>Reset</c> keeps the buffer.
/// </para>
/// <para>
/// The prototype has no buffer to grow, and an element that is a leaf is never pinned, so it
/// should hold at zero without the caller pooling anything. The pair to read is
/// <see cref="Chain_LeafElements"/> against <see cref="Log_LeafElements_Pooled"/>: if the
/// prototype matches the pooled log while asking nothing of the caller, that is the case for
/// changing shape.
/// </para>
/// <para>
/// <b><see cref="Chain_NestedElements"/> is the prototype's worst case and is here on purpose.</b>
/// When each element descends one level further - <c>lines[i].shipTo.city</c> - every element pins
/// a node, so the chain allocates per element where the pooled log allocates nothing. A shape
/// comparison that measured only the flattering case would not be worth running.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Design)]
public class ContextShapeCollectionBenchmarks {
    private readonly ValidationErrorCollector _pooledLog = new();
    private readonly ChainErrorCollector _pooledChain = new();

    /// <summary>Elements the pass descends into.</summary>
    [Params(1, 10, 100, 1000)]
    public int Elements { get; set; }

    [GlobalSetup]
    public void Setup() => ContextShapeParity.Verify();

    // ---- Elements are leaves: the common shape, and the prototype's best case -------------------

    [Benchmark(Baseline = true, Description = "log: fresh collector, leaf elements")]
    public bool Log_LeafElements() {
        var collector = new ValidationErrorCollector();

        var root = new ValidationContext(collector);
        for (var i = 0; i < Elements; i++) {
            root.PushIndex("lines", i);
        }

        return collector.HasErrors;
    }

    [Benchmark(Description = "chain: fresh collector, leaf elements")]
    public bool Chain_LeafElements() {
        var collector = new ChainErrorCollector();

        var root = new ChainContext(collector);
        for (var i = 0; i < Elements; i++) {
            root.PushIndex("lines", i);
        }

        return collector.HasErrors;
    }

    [Benchmark(Description = "log: pooled collector, leaf elements")]
    public bool Log_LeafElements_Pooled() {
        _pooledLog.Reset();

        var root = new ValidationContext(_pooledLog);
        for (var i = 0; i < Elements; i++) {
            root.PushIndex("lines", i);
        }

        return _pooledLog.HasErrors;
    }

    [Benchmark(Description = "chain: pooled collector, leaf elements")]
    public bool Chain_LeafElements_Pooled() {
        _pooledChain.Reset();

        var root = new ChainContext(_pooledChain);
        for (var i = 0; i < Elements; i++) {
            root.PushIndex("lines", i);
        }

        return _pooledChain.HasErrors;
    }

    // ---- Elements nest one level further: the prototype's worst case ----------------------------

    [Benchmark(Description = "log: pooled collector, elements nest one level")]
    public bool Log_NestedElements() {
        _pooledLog.Reset();

        var root = new ValidationContext(_pooledLog);
        for (var i = 0; i < Elements; i++) {
            root.PushIndex("lines", i).Push("shipTo");
        }

        return _pooledLog.HasErrors;
    }

    [Benchmark(Description = "chain: pooled collector, elements nest one level")]
    public bool Chain_NestedElements() {
        _pooledChain.Reset();

        var root = new ChainContext(_pooledChain);
        for (var i = 0; i < Elements; i++) {
            root.PushIndex("lines", i).Push("shipTo");
        }

        return _pooledChain.HasErrors;
    }
}
