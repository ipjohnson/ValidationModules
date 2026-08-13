using BenchmarkDotNet.Attributes;
using FluentValidation;
using ValidationModules.Benchmarks.Comparative.Engines;
using ValidationModules.Benchmarks.Comparative.Models;

namespace ValidationModules.Benchmarks.Comparative.Comparisons;

/// <summary>
/// How the two engines that descend into collections scale with element count.
/// </summary>
/// <remarks>
/// <para>
/// The per-element work is what separates them. ValidationModules emits a <c>for</c> loop calling a
/// static validator and appending one path node per element; FluentValidation runs its
/// <c>RuleForEach</c> collection rule, which allocates a child context and builds an indexed
/// property chain per element.
/// </para>
/// <para>
/// So the interesting reading is not the ratio at any single element count but how the ratio moves
/// across the sweep. A constant ratio means both engines are linear and the gap is per-element
/// overhead; a widening one means the per-element cost differs in kind.
/// </para>
/// <para>
/// DataAnnotations is absent from this comparison rather than labelled: it does not descend into
/// elements at all, so its line would be flat across the whole sweep and would say nothing about
/// scaling.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(ComparativeCategories.Collection)]
public class CollectionScalingComparison {
    private readonly ValidationErrorCollector _pooled = new();

    private Basket _basket = null!;

    /// <summary>Elements in the validated collection.</summary>
    [Params(1, 10, 100, 1000)]
    public int Elements { get; set; }

    [GlobalSetup]
    public void Setup() {
        _basket = SampleData.BasketOf(Elements);

        _ = BasketValidator.Instance.IsValid(_basket);
        _ = BasketFluentValidator.Instance.Validate(_basket);
    }

    [Benchmark(Baseline = true, Description = "ValidationModules")]
    public bool Vm() => BasketValidator.Instance.IsValid(_basket);

    [Benchmark(Description = "ValidationModules - pooled collector")]
    public bool Vm_Pooled() {
        _pooled.Reset();

        BasketValidator.Instance.ValidateInto(_pooled, _basket);

        return !_pooled.HasErrors;
    }

    [Benchmark(Description = "FluentValidation")]
    public bool Fv() => BasketFluentValidator.Instance.Validate(_basket).IsValid;
}
