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

    // Hoisted: constructing per invocation would put an allocation on the measured path.
    private static readonly OrderValidator OrderValidatorShared = new();
    private readonly List<DataAnnotations.ValidationResult> _annotationResults = [];
    private readonly ValidationErrorCollector _pooled = new();

    private Order _valid = null!;
    private Order _invalid = null!;
    private Models.Annotated.Order _validAnnotated = null!;
    private Models.Annotated.Order _invalidAnnotated = null!;

    /// <summary>Set when DataAnnotations is not validating on this runtime. See EngineParity.</summary>
    private string? _dataAnnotationsDivergence;

    /// <summary>
    /// The payload, or a throw naming why DataAnnotations cannot be measured here.
    /// </summary>
    /// <remarks>
    /// Throwing from the benchmark rather than the setup costs only the DataAnnotations rows: they
    /// report NA with the reason in the log, and the two engines that still work keep their numbers.
    /// </remarks>
    private T CheckedAnnotations<T>(T payload) =>
        _dataAnnotationsDivergence is null
            ? payload
            : throw new InvalidOperationException(_dataAnnotationsDivergence);

    [GlobalSetup]
    public void Setup() {
        // In the measured process, not just the host: under Native AOT this class runs in its
        // own AOT-compiled binary, and an engine that quietly stopped validating there would
        // otherwise be reported as fast. See EngineParity's remarks.
        EngineParity.Verify();
        _dataAnnotationsDivergence = EngineParity.DataAnnotationsDivergence();

        _valid = SampleData.ValidOrder();
        _invalid = SampleData.InvalidOrder();
        _validAnnotated = SampleData.ValidAnnotatedOrder();
        _invalidAnnotated = SampleData.InvalidAnnotatedOrder();

        _ = OrderValidatorShared.IsValid(_valid);
        _ = OrderFluentValidator.Instance.Validate(_valid);
        _ = DataAnnotationsEngine.TryValidate(_validAnnotated, _annotationResults);
    }

    // Both engines run the full pass and materialize their result object - like for like. The
    // boolean fast path is measured alone below, because FluentValidation has no boolean-only API.

    [Benchmark(Baseline = true, Description = "ValidationModules - clean")]
    public ValidationResult Vm_Clean() => OrderValidatorShared.Validate(_valid);

    [Benchmark(Description = "FluentValidation - clean")]
    public FluentValidation.Results.ValidationResult Fv_Clean() =>
        OrderFluentValidator.Instance.Validate(_valid);

    [Benchmark(Description = "DataAnnotations - clean, TOP LEVEL ONLY (does not descend)")]
    public bool Da_Clean() => DataAnnotationsEngine.TryValidate(CheckedAnnotations(_validAnnotated), _annotationResults);

    [Benchmark(Description = "ValidationModules - clean, pooled collector (no FV/DA equivalent)")]
    public bool Vm_Clean_Pooled() {
        _pooled.Reset();

        OrderValidatorShared.ValidateInto(_pooled, _valid);

        return !_pooled.HasErrors;
    }

    [Benchmark(Description = "ValidationModules - clean, boolean fast path (no FV/DA equivalent)")]
    public bool Vm_Clean_Bool() => OrderValidatorShared.IsValid(_valid);

    /// <summary>
    /// One failure at each level, so all three engines - the two that descend, anyway - have to
    /// build a qualified error path.
    /// </summary>
    [Benchmark(Description = "ValidationModules - 1 failure per level")]
    public ValidationResult Vm_Failing() => OrderValidatorShared.Validate(_invalid);

    [Benchmark(Description = "FluentValidation - 1 failure per level")]
    public FluentValidation.Results.ValidationResult Fv_Failing() =>
        OrderFluentValidator.Instance.Validate(_invalid);

    [Benchmark(Description = "DataAnnotations - failing, TOP LEVEL ONLY (finds 1 of 4)")]
    public bool Da_Failing() => DataAnnotationsEngine.TryValidate(CheckedAnnotations(_invalidAnnotated), _annotationResults);
}
