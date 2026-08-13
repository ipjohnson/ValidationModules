using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace ValidationModules.Benchmarks.Design;

/// <summary>How deep the pass descends before it does anything.</summary>
public enum PathShape {
    /// <summary>No nesting. A flat request body, and the commonest shape there is.</summary>
    Flat,

    /// <summary>One nested object. The chain prototype's best case — a leaf, never pinned.</summary>
    Depth1,

    /// <summary>Four levels. Where the prototype starts paying a node per intermediate level.</summary>
    Depth4,
}

/// <summary>
/// Where path segments should live: an append-only log on the collector, as shipped, or the
/// contexts themselves.
/// </summary>
/// <remarks>
/// <para>
/// The shipped shape allocates a 16-entry <c>PathNode</c> buffer in the collector's field
/// initializer whether or not anything nests, which is 408 of the 472 bytes a fresh collector
/// costs. The prototype has no buffer at all; it pays a heap node per level instead, and only for
/// levels that something descends past. See <see cref="ChainContext"/> for why that is not simply a
/// <c>ref struct</c>.
/// </para>
/// <para>
/// <b>Read the clean rows for the shape question.</b> They never build a path string, so they
/// isolate storage exactly. The failing rows also change how the string is built - the prototype
/// recurses where the shipped one fills a temporary array - and that part is separable and could be
/// back-ported without changing shape, so do not credit the chain with all of it.
/// </para>
/// <para>
/// Both arms add the same literal message, so message composition is a constant and cancels.
/// </para>
/// <para>
/// <b>Why <c>[IterationTime]</c> is here.</b> Several cells are a few nanoseconds - a pooled
/// collector reset and one push is close to nothing - and at the default 500 ms target
/// BenchmarkDotNet climbs to 134 million operations per iteration to fill the time, which turned a
/// single cell into a fifteen-minute run. 100 ms keeps the op counts sane. Precision suffers on the
/// fastest rows, which costs nothing here: the decision rests on the allocation column, which is a
/// GC counter and exact at any iteration count, and on time ratios far larger than the noise.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[IterationTime(100)]
[BenchmarkCategory(BenchmarkCategories.Design)]
public class ContextShapeBenchmarks {
    private const string Message = "the field is required.";

    private readonly ValidationErrorCollector _pooledLog = new();
    private readonly ChainErrorCollector _pooledChain = new();

    private int _depth;

    [Params(PathShape.Flat, PathShape.Depth1, PathShape.Depth4)]
    public PathShape Shape { get; set; }

    [GlobalSetup]
    public void Setup() {
        ContextShapeParity.Verify();

        _depth = Shape switch {
            PathShape.Flat => 0,
            PathShape.Depth1 => 1,
            _ => 4,
        };
    }

    // ---- Fresh collector: the default path, what Validate/IsValid do today ----------------------

    [Benchmark(Baseline = true, Description = "log: descend, add nothing")]
    public int Log_Clean() {
        var collector = new ValidationErrorCollector();

        var context = new ValidationContext(collector);
        for (var i = 0; i < _depth; i++) {
            context = context.Push("home");
        }

        return Consume(ref context);
    }

    [Benchmark(Description = "chain: descend, add nothing")]
    public int Chain_Clean() {
        var collector = new ChainErrorCollector();

        var context = new ChainContext(collector);
        for (var i = 0; i < _depth; i++) {
            context = context.Push("home");
        }

        return Consume(ref context);
    }

    [Benchmark(Description = "log: descend, one error at the leaf")]
    public bool Log_Failing() {
        var collector = new ValidationErrorCollector();

        var context = new ValidationContext(collector);
        for (var i = 0; i < _depth; i++) {
            context = context.Push("home");
        }

        context.Add("postalCode", ValidationCodes.Required, Message);

        return collector.HasErrors;
    }

    [Benchmark(Description = "chain: descend, one error at the leaf")]
    public bool Chain_Failing() {
        var collector = new ChainErrorCollector();

        var context = new ChainContext(collector);
        for (var i = 0; i < _depth; i++) {
            context = context.Push("home");
        }

        context.Add("postalCode", ValidationCodes.Required, Message);

        return collector.HasErrors;
    }

    // ---- Pooled collector: today's escape hatch, and what the chain has to beat -----------------

    /// <summary>
    /// The shipped shape with the collector reused, which is the 0 B row the library already
    /// achieves and the bar the prototype has to clear without asking the caller for anything.
    /// </summary>
    [Benchmark(Description = "log: pooled collector, descend, add nothing")]
    public int Log_Clean_Pooled() {
        _pooledLog.Reset();

        var context = new ValidationContext(_pooledLog);
        for (var i = 0; i < _depth; i++) {
            context = context.Push("home");
        }

        return Consume(ref context);
    }

    /// <summary>
    /// The prototype pooled. There is no path buffer to keep, so pooling saves only the collector
    /// object itself - the gap against <see cref="Chain_Clean"/> is the whole of what pooling is
    /// worth under this shape.
    /// </summary>
    [Benchmark(Description = "chain: pooled collector, descend, add nothing")]
    public int Chain_Clean_Pooled() {
        _pooledChain.Reset();

        var context = new ChainContext(_pooledChain);
        for (var i = 0; i < _depth; i++) {
            context = context.Push("home");
        }

        return Consume(ref context);
    }

    /// <summary>
    /// Stands in for the nested validator call the emitter writes. Non-inlined and taking the
    /// context by <c>ref</c>, so the descended context has to be materialized rather than optimized
    /// away in whichever arm leaves no side effect behind. See the remarks on
    /// <see cref="ContextShapeCollectionBenchmarks"/> for what happened without it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Consume(ref ValidationContext context) => context.ErrorCount;

    /// <inheritdoc cref="Consume(ref ValidationContext)"/>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Consume(ref ChainContext context) => context.ErrorCount;
}
