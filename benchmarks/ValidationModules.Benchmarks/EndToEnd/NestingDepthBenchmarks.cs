using BenchmarkDotNet.Attributes;
using ValidationModules.Benchmarks.Models;

namespace ValidationModules.Benchmarks.EndToEnd;

/// <summary>
/// How a pass scales with nesting depth, over a self-referential model.
/// </summary>
/// <remarks>
/// <para>
/// Two costs grow with depth and they grow differently. Descending is one appended node per level,
/// so a clean pass should be linear and allocate nothing. Materializing a path walks the parent
/// chain twice - once to measure it, once to fill the buffer - and then concatenates, so a failure
/// at the leaf is the expensive case and the one <see cref="Failing_Validate"/> measures.
/// </para>
/// <para>
/// Depth 16 is well inside <see cref="ValidationErrorCollector.MaxDepth"/> of 64, which exists to
/// turn a genuine object cycle into a diagnosable exception rather than a stack overflow. The guard
/// itself walks the parent chain on every push, so its cost is included in every reading here -
/// which is the point of sweeping depth rather than assuming it is free.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.EndToEnd)]
public class NestingDepthBenchmarks {
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
    public bool Clean_IsValid() => NodeValidator.Instance.IsValid(_clean);

    [Benchmark(Description = "Clean chain, ValidateInto a pooled collector")]
    public bool Clean_ValidateInto() {
        _pooled.Reset();

        NodeValidator.Instance.ValidateInto(_pooled, _clean);

        return _pooled.HasErrors;
    }

    /// <summary>
    /// The innermost node fails, so exactly one path is built and it is the longest one available -
    /// <c>child.child.child…label</c>. The difference against the clean pass is the full cost of
    /// materializing a deep path.
    /// </summary>
    [Benchmark(Description = "Failure at the deepest level, Validate")]
    public ValidationResult Failing_Validate() => NodeValidator.Instance.Validate(_failingAtLeaf);
}
