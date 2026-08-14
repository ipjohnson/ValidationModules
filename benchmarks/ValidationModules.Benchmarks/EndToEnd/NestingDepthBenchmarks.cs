using BenchmarkDotNet.Attributes;
using ValidationModules.Benchmarks.Models;

namespace ValidationModules.Benchmarks.EndToEnd;

/// <summary>
/// How a pass scales with nesting depth, over a self-referential model.
/// </summary>
/// <remarks>
/// <para>
/// Descending is a struct copy per level, so a clean pass should be linear and allocate nothing.
/// Materializing a path is now independent of depth - a context carries two segments however far
/// down it is - so <see cref="Failing_Validate"/> should track the clean row at a constant offset
/// rather than pulling away from it. Under the path log it did pull away, because rendering walked
/// the parent chain twice before concatenating.
/// </para>
/// <para>
/// Depth 16 is well inside <see cref="ValidationErrorCollector.MaxDepth"/> of 64, which exists to
/// turn a genuine object cycle into a diagnosable exception rather than a stack overflow. The guard
/// is a comparison against the depth the context already carries, so unlike the chain walk it
/// replaced it contributes nothing that grows with depth.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.EndToEnd)]
public class NestingDepthBenchmarks {

    // Hoisted: constructing per invocation would put an allocation on the measured path.
    private static readonly NodeValidator NodeValidatorShared = new();
    private readonly ValidationErrorCollector _pooled = new();

    private Node _clean = null!;
    private Node _failingAtLeaf = null!;

    /// <summary>Levels of nesting. 16 is deep for a hand-written model and shallow for a spec-generated one.</summary>
    [Params(1, 4, 16)]
    public int Depth { get; set; }

    [GlobalSetup]
    public void Setup() {
        _clean = SampleData.ChainOf(Depth, failAtLeaf: false);
        _failingAtLeaf = SampleData.ChainOf(Depth, failAtLeaf: true);
    }

    [Benchmark(Baseline = true, Description = "Clean chain, IsValid - descent only")]
    public bool Clean_IsValid() => NodeValidatorShared.IsValid(_clean);

    [Benchmark(Description = "Clean chain, ValidateInto a pooled collector")]
    public bool Clean_ValidateInto() {
        _pooled.Reset();

        NodeValidatorShared.ValidateInto(_pooled, _clean);

        return _pooled.HasErrors;
    }

    /// <summary>
    /// The innermost node fails, so exactly one path is built and it is the longest one available -
    /// <c>child.child.child…label</c>. The difference against the clean pass is the full cost of
    /// materializing a deep path.
    /// </summary>
    [Benchmark(Description = "Failure at the deepest level, Validate")]
    public ValidationResult Failing_Validate() => NodeValidatorShared.Validate(_failingAtLeaf);
}
