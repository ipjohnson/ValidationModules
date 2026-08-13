using System.Runtime.CompilerServices;
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
/// <para>
/// <b><c>[IterationTime]</c>:</b> the one-element cells are a few nanoseconds, and the default
/// 500 ms target has BenchmarkDotNet climb to over a hundred million operations per iteration to
/// fill it. See <see cref="ContextShapeBenchmarks"/> for the reasoning.
/// </para>
/// <para>
/// <b>Every element context is handed to <c>Consume</c>, and that is load-bearing.</b> A first
/// version of this class pushed and dropped the result, which flattered the prototype badly: the
/// log's push mutates the collector and cannot be elided, while the chain's leaf push builds a
/// struct with no side effect, so the JIT deleted the loop body outright and the chain "measured"
/// 0.28 ns per element. <c>Consume</c> takes the context by <c>ref</c> from a non-inlined method,
/// which is exactly the shape generated code uses -
/// <c>ToyValidator.Instance.Validate(ref elementCtx, element)</c> - so both arms now pay for a
/// context that genuinely has to exist.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[IterationTime(100)]
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
    public int Log_LeafElements() {
        var collector = new ValidationErrorCollector();

        var root = new ValidationContext(collector);
        var total = 0;
        for (var i = 0; i < Elements; i++) {
            var element = root.PushIndex("lines", i);
            total += Consume(ref element);
        }

        return total;
    }

    [Benchmark(Description = "chain: fresh collector, leaf elements")]
    public int Chain_LeafElements() {
        var collector = new ChainErrorCollector();

        var root = new ChainContext(collector);
        var total = 0;
        for (var i = 0; i < Elements; i++) {
            var element = root.PushIndex("lines", i);
            total += Consume(ref element);
        }

        return total;
    }

    [Benchmark(Description = "log: pooled collector, leaf elements")]
    public int Log_LeafElements_Pooled() {
        _pooledLog.Reset();

        var root = new ValidationContext(_pooledLog);
        var total = 0;
        for (var i = 0; i < Elements; i++) {
            var element = root.PushIndex("lines", i);
            total += Consume(ref element);
        }

        return total;
    }

    [Benchmark(Description = "chain: pooled collector, leaf elements")]
    public int Chain_LeafElements_Pooled() {
        _pooledChain.Reset();

        var root = new ChainContext(_pooledChain);
        var total = 0;
        for (var i = 0; i < Elements; i++) {
            var element = root.PushIndex("lines", i);
            total += Consume(ref element);
        }

        return total;
    }

    // ---- Elements nest one level further: the prototype's worst case ----------------------------

    [Benchmark(Description = "log: pooled collector, elements nest one level")]
    public int Log_NestedElements() {
        _pooledLog.Reset();

        var root = new ValidationContext(_pooledLog);
        var total = 0;
        for (var i = 0; i < Elements; i++) {
            var nested = root.PushIndex("lines", i).Push("shipTo");
            total += Consume(ref nested);
        }

        return total;
    }

    [Benchmark(Description = "chain: pooled collector, elements nest one level")]
    public int Chain_NestedElements() {
        _pooledChain.Reset();

        var root = new ChainContext(_pooledChain);
        var total = 0;
        for (var i = 0; i < Elements; i++) {
            var nested = root.PushIndex("lines", i).Push("shipTo");
            total += Consume(ref nested);
        }

        return total;
    }

    /// <summary>
    /// Stands in for the nested validator call the emitter writes. Non-inlined and taking the
    /// context by <c>ref</c>, so the context has to be materialized in both arms rather than
    /// optimized out of existence in whichever arm has no side effect.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Consume(ref ValidationContext context) => context.ErrorCount;

    /// <inheritdoc cref="Consume(ref ValidationContext)"/>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Consume(ref ChainContext context) => context.ErrorCount;
}
