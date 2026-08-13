using BenchmarkDotNet.Attributes;
using ValidationModules.Benchmarks.Models;

namespace ValidationModules.Benchmarks.EndToEnd;

/// <summary>
/// How a pass scales with the number of collection elements it has to descend into.
/// </summary>
/// <remarks>
/// <para>
/// The emitted loop calls <c>ctx.PushIndex</c> once per element, and each push appends a node to
/// the collector's path log. So the clean case should be linear in element count with a flat
/// allocation column - the log grows, but it grows into a buffer that doubles, and nothing
/// materializes a string until an error appears.
/// </para>
/// <para>
/// The reading to check is the jump from 100 to 1000: a superlinear step there would mean the node
/// buffer's growth or the parent-chain walk is costing more than it looks, and 1000-element
/// payloads are ordinary in bulk-import endpoints.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.EndToEnd)]
public class CollectionScalingBenchmarks {
    private readonly ValidationErrorCollector _pooled = new();

    private Basket _clean = null!;
    private Basket _withFailures = null!;

    /// <summary>Elements in the validated collection.</summary>
    [Params(1, 10, 100, 1000)]
    public int Elements { get; set; }

    [GlobalSetup]
    public void Setup() {
        _clean = SampleData.BasketOf(Elements, withFailures: false);
        _withFailures = SampleData.BasketOf(Elements, withFailures: true);
    }

    [Benchmark(Baseline = true, Description = "All elements clean, IsValid")]
    public bool Clean_IsValid() => BasketValidator.Instance.IsValid(_clean);

    [Benchmark(Description = "All elements clean, ValidateInto a pooled collector")]
    public bool Clean_ValidateInto() {
        _pooled.Reset();

        BasketValidator.Instance.ValidateInto(_pooled, _clean);

        return _pooled.HasErrors;
    }

    /// <summary>
    /// A tenth of the elements failing, which is what a bulk import of partly-bad rows looks like.
    /// Each failure materializes a <c>lines[n].quantity</c> path.
    /// </summary>
    [Benchmark(Description = "1 in 10 elements failing, Validate")]
    public ValidationResult Failing_Validate() => BasketValidator.Instance.Validate(_withFailures);
}
