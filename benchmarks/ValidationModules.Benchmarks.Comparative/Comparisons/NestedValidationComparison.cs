using BenchmarkDotNet.Attributes;
using FluentValidation;
using ValidationModules.Benchmarks.Comparative.Engines;
using ValidationModules.Benchmarks.Comparative.Models;
using DataAnnotations = System.ComponentModel.DataAnnotations;

namespace ValidationModules.Benchmarks.Comparative.Comparisons;

/// <summary>
/// An order with a nested buyer, a nested address and three collection elements.
/// </summary>
/// <remarks>
/// <para>
/// Nesting is where the engines stop being interchangeable. ValidationModules emits a static call
/// into the child's validator and pushes a path node; FluentValidation dispatches through a child
/// validator adaptor and builds a property chain; DataAnnotations does not descend at all.
/// </para>
/// <para>
/// The DataAnnotations rows are labelled "top level only" because that is what they measure. They
/// are not a like-for-like number and must not be read as one - the engine evaluates
/// <c>reference</c> and stops, never reaching <c>buyer.age</c>, <c>shipTo.postalCode</c> or
/// <c>lines[1].quantity</c>. They are here to show what "free, in-box validation" does and does not
/// cover, which is the same gap §10.4 of the plan found in a shipping framework.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(ComparativeCategories.Nested)]
public class NestedValidationComparison {
    private readonly List<DataAnnotations.ValidationResult> _annotationResults = [];
    private readonly ValidationErrorCollector _pooled = new();

    private Order _valid = null!;
    private Order _invalid = null!;
    private Models.Annotated.Order _validAnnotated = null!;
    private Models.Annotated.Order _invalidAnnotated = null!;

    [GlobalSetup]
    public void Setup() {
        _valid = SampleData.ValidOrder();
        _invalid = SampleData.InvalidOrder();
        _validAnnotated = SampleData.ValidAnnotatedOrder();
        _invalidAnnotated = SampleData.InvalidAnnotatedOrder();

        _ = OrderValidator.Instance.IsValid(_valid);
        _ = OrderFluentValidator.Instance.Validate(_valid);
        _ = DataAnnotationsEngine.TryValidate(_validAnnotated, _annotationResults);
    }

    [Benchmark(Baseline = true, Description = "ValidationModules - clean")]
    public bool Vm_Clean() => OrderValidator.Instance.IsValid(_valid);

    [Benchmark(Description = "FluentValidation - clean")]
    public bool Fv_Clean() => OrderFluentValidator.Instance.Validate(_valid).IsValid;

    [Benchmark(Description = "DataAnnotations - clean, TOP LEVEL ONLY (does not descend)")]
    public bool Da_Clean() => DataAnnotationsEngine.TryValidate(_validAnnotated, _annotationResults);

    [Benchmark(Description = "ValidationModules - clean, pooled collector")]
    public bool Vm_Clean_Pooled() {
        _pooled.Reset();

        OrderValidator.Instance.ValidateInto(_pooled, _valid);

        return !_pooled.HasErrors;
    }

    /// <summary>
    /// One failure at each level, so all three engines - the two that descend, anyway - have to
    /// build a qualified error path.
    /// </summary>
    [Benchmark(Description = "ValidationModules - 1 failure per level")]
    public ValidationResult Vm_Failing() => OrderValidator.Instance.Validate(_invalid);

    [Benchmark(Description = "FluentValidation - 1 failure per level")]
    public FluentValidation.Results.ValidationResult Fv_Failing() =>
        OrderFluentValidator.Instance.Validate(_invalid);

    [Benchmark(Description = "DataAnnotations - failing, TOP LEVEL ONLY (finds 1 of 4)")]
    public bool Da_Failing() => DataAnnotationsEngine.TryValidate(_invalidAnnotated, _annotationResults);
}
