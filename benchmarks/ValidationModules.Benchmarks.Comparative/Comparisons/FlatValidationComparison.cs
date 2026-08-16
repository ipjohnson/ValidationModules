using BenchmarkDotNet.Attributes;
using FluentValidation;
using ValidationModules.Benchmarks.Comparative.Engines;
using ValidationModules.Benchmarks.Comparative.Models;
using DataAnnotations = System.ComponentModel.DataAnnotations;

namespace ValidationModules.Benchmarks.Comparative.Comparisons;

/// <summary>
/// Three engines, one flat payload, the same five rules.
/// </summary>
/// <remarks>
/// <para>
/// The central comparison, and the one to read first. No nesting, no collections, no container -
/// just the cost of getting from "validate this object" to "here are the failures" in each engine.
/// </para>
/// <para>
/// All three validators are constructed once, in setup, which is the configuration every engine
/// documents and recommends. That is deliberately generous to the two that build state up front:
/// <c>ValidatorConstructionComparison</c> shows what that state costs to build, and §10.2 of the
/// implementation plan records a real framework that was paying it per request.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(ComparativeCategories.Flat)]
public class FlatValidationComparison {

    // Hoisted: constructing per invocation would put an allocation on the measured path.
    private static readonly CustomerValidator CustomerValidatorShared = new();
    private readonly List<DataAnnotations.ValidationResult> _annotationResults = [];
    private readonly ValidationErrorCollector _pooled = new();

    private Customer _valid = null!;
    private Customer _invalid = null!;
    private Models.Annotated.Customer _validAnnotated = null!;
    private Models.Annotated.Customer _invalidAnnotated = null!;

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

        _valid = SampleData.ValidCustomer();
        _invalid = SampleData.InvalidCustomer();
        _validAnnotated = SampleData.ValidAnnotatedCustomer();
        _invalidAnnotated = SampleData.InvalidAnnotatedCustomer();

        // First call warms each engine's lazily-built state, so no benchmark pays it once and
        // reports it as though it were per-call. DataAnnotations caches property descriptors on
        // first use; FluentValidation resolves its display-name providers.
        _ = CustomerValidatorShared.IsValid(_valid);
        _ = CustomerFluentValidator.Instance.Validate(_valid);
        _ = DataAnnotationsEngine.TryValidate(_validAnnotated, _annotationResults);
    }

    // ---- Clean payload: the path production traffic actually takes ------------------------------

    [Benchmark(Baseline = true, Description = "ValidationModules - clean")]
    public bool Vm_Clean() => CustomerValidatorShared.IsValid(_valid);

    [Benchmark(Description = "FluentValidation - clean")]
    public bool Fv_Clean() => CustomerFluentValidator.Instance.Validate(_valid).IsValid;

    [Benchmark(Description = "DataAnnotations - clean")]
    public bool Da_Clean() => DataAnnotationsEngine.TryValidate(CheckedAnnotations(_validAnnotated), _annotationResults);

    // ---- Failing payload: every rule violated ---------------------------------------------------

    [Benchmark(Description = "ValidationModules - 5 failures")]
    public bool Vm_Failing() => CustomerValidatorShared.IsValid(_invalid);

    [Benchmark(Description = "FluentValidation - 5 failures")]
    public bool Fv_Failing() => CustomerFluentValidator.Instance.Validate(_invalid).IsValid;

    [Benchmark(Description = "DataAnnotations - 5 failures")]
    public bool Da_Failing() => DataAnnotationsEngine.TryValidate(CheckedAnnotations(_invalidAnnotated), _annotationResults);

    // ---- Clean payload, with each engine's errors materialized -----------------------------------

    /// <summary>
    /// The pooled-collector shape, which has no equivalent in either competitor: the caller owns the
    /// buffer, so a clean pass allocates nothing at all. This is what a generated request filter
    /// emits, and the gap against the two below is most of why the library exists.
    /// </summary>
    [Benchmark(Description = "ValidationModules - clean, pooled collector")]
    public bool Vm_Clean_Pooled() {
        _pooled.Reset();

        CustomerValidatorShared.ValidateInto(_pooled, _valid);

        return !_pooled.HasErrors;
    }

    [Benchmark(Description = "ValidationModules - clean, materialized result")]
    public ValidationResult Vm_Clean_Result() => CustomerValidatorShared.Validate(_valid);

    [Benchmark(Description = "FluentValidation - clean, materialized result")]
    public FluentValidation.Results.ValidationResult Fv_Clean_Result() =>
        CustomerFluentValidator.Instance.Validate(_valid);
}
