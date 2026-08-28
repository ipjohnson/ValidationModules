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
/// <para>
/// <b>Every cross-engine row does the same work: a full pass that materializes a result.</b>
/// The generated boolean fast path - <c>IsValid</c>, which returns at the first failure and
/// builds nothing - is measured as its own labelled row and never against FluentValidation's
/// <c>Validate</c>, because FluentValidation has no boolean-only entry point to pair it with.
/// This class briefly drifted into exactly that pairing when the fast path was generated
/// (2026-08-26) under benchmarks written before it existed; the labelled row is the fix.
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
    // Both engines run the full pass and materialize their result object - like for like.

    [Benchmark(Baseline = true, Description = "ValidationModules - clean")]
    public ValidationResult Vm_Clean() => CustomerValidatorShared.Validate(_valid);

    [Benchmark(Description = "FluentValidation - clean")]
    public FluentValidation.Results.ValidationResult Fv_Clean() =>
        CustomerFluentValidator.Instance.Validate(_valid);

    [Benchmark(Description = "DataAnnotations - clean")]
    public bool Da_Clean() => DataAnnotationsEngine.TryValidate(CheckedAnnotations(_validAnnotated), _annotationResults);

    // ---- Failing payload: every rule violated, every error reported -----------------------------

    [Benchmark(Description = "ValidationModules - 5 failures")]
    public ValidationResult Vm_Failing() => CustomerValidatorShared.Validate(_invalid);

    [Benchmark(Description = "FluentValidation - 5 failures")]
    public FluentValidation.Results.ValidationResult Fv_Failing() =>
        CustomerFluentValidator.Instance.Validate(_invalid);

    [Benchmark(Description = "DataAnnotations - 5 failures")]
    public bool Da_Failing() => DataAnnotationsEngine.TryValidate(CheckedAnnotations(_invalidAnnotated), _annotationResults);

    // ---- Shapes with no cross-engine equivalent, measured alone ---------------------------------

    /// <summary>
    /// The pooled-collector shape, which has no equivalent in either competitor: the caller owns the
    /// buffer, so a clean pass allocates nothing at all. This is what a generated request filter
    /// emits, and the gap against the rows above is most of why the library exists.
    /// </summary>
    [Benchmark(Description = "ValidationModules - clean, pooled collector (no FV/DA equivalent)")]
    public bool Vm_Clean_Pooled() {
        _pooled.Reset();

        CustomerValidatorShared.ValidateInto(_pooled, _valid);

        return !_pooled.HasErrors;
    }

    /// <summary>
    /// The generated boolean fast path: straight-line tests that return at the first failure and
    /// build no path, message or error record. Not compared to FluentValidation because it has no
    /// boolean-only API - pairing this against its <c>Validate</c> would measure two different
    /// amounts of work.
    /// </summary>
    [Benchmark(Description = "ValidationModules - clean, boolean fast path (no FV/DA equivalent)")]
    public bool Vm_Clean_Bool() => CustomerValidatorShared.IsValid(_valid);
}
