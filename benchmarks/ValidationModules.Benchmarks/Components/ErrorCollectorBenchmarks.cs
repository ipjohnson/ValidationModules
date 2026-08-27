using BenchmarkDotNet.Attributes;
using ValidationModules.Benchmarks.Models;

namespace ValidationModules.Benchmarks.Components;

/// <summary>
/// The collector's own costs: allocating one per pass against pooling one, and the lock a
/// synchronized collector adds.
/// </summary>
/// <remarks>
/// <c>Fresh</c> against <c>Pooled</c> is what pooling is worth, and as of 2026-08-13 the answer is
/// 48 bytes on a clean pass against a node per error on every failing one - which is why the library
/// no longer recommends it. Both run the same generated validator over the same instance, so the
/// difference is the collector and nothing else. Kept as the measurement that decision rests on.
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Component)]
public class ErrorCollectorBenchmarks {

    // Hoisted: constructing per invocation would put an allocation on the measured path.
    private static readonly CustomerValidator CustomerValidatorShared = new();
    private readonly ValidationErrorCollector _pooled = new();

    private Customer _valid = null!;
    private Customer _invalid = null!;

    [GlobalSetup]
    public void Setup() {
        _valid = SampleData.ValidCustomer();
        _invalid = SampleData.InvalidCustomer();
    }

    [Benchmark(Baseline = true, Description = "Fresh collector, clean pass")]
    public bool Fresh_CleanPass() {
        var collector = new ValidationErrorCollector();

        CustomerValidatorShared.ValidateInto(collector, _valid);

        return collector.HasErrors;
    }

    [Benchmark(Description = "Pooled collector + Reset, clean pass")]
    public bool Pooled_CleanPass() {
        _pooled.Reset();

        CustomerValidatorShared.ValidateInto(_pooled, _valid);

        return _pooled.HasErrors;
    }

    [Benchmark(Description = "Fresh collector, 8 failures")]
    public bool Fresh_FailingPass() {
        var collector = new ValidationErrorCollector();

        CustomerValidatorShared.ValidateInto(collector, _invalid);

        return collector.HasErrors;
    }

    [Benchmark(Description = "Pooled collector + Reset, 8 failures")]
    public bool Pooled_FailingPass() {
        _pooled.Reset();

        CustomerValidatorShared.ValidateInto(_pooled, _invalid);

        return _pooled.HasErrors;
    }

    [Benchmark(Description = "ToResult on a clean pass - returns the shared instance")]
    public ValidationResult ToResult_Clean() {
        _pooled.Reset();

        CustomerValidatorShared.ValidateInto(_pooled, _valid);

        return _pooled.ToResult();
    }

    [Benchmark(Description = "ToResult on 8 failures - copies into an immutable result")]
    public ValidationResult ToResult_Failing() {
        _pooled.Reset();

        CustomerValidatorShared.ValidateInto(_pooled, _invalid);

        return _pooled.ToResult();
    }

    /// <summary>
    /// Merging two results, which is what the runner does when a business rule adds to what the
    /// structural pass found.
    /// </summary>
    [Benchmark(Description = "Merge two failing results")]
    public ValidationResult Merge_TwoFailing() {
        _pooled.Reset();
        CustomerValidatorShared.ValidateInto(_pooled, _invalid);
        var first = _pooled.ToResult();

        _pooled.Reset();
        CustomerValidatorShared.ValidateInto(_pooled, _invalid);
        var second = _pooled.ToResult();

        return first.Merge(second);
    }
}
