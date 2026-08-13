using BenchmarkDotNet.Attributes;
using ValidationModules.Benchmarks.Models;

namespace ValidationModules.Benchmarks.EndToEnd;

/// <summary>
/// What composition costs: running validators through <see cref="ValidationRunner{T}"/> rather than
/// calling one directly, and what the async path adds when there is nothing async to do.
/// </summary>
/// <remarks>
/// <para>
/// The runner owns the policy in §8 of the plan - every registered validator runs, results merge
/// rather than replace, and business rules run only if structural validation passed. It is also
/// what a request pipeline resolves once at handler construction, so its per-call overhead is paid
/// per request.
/// </para>
/// <para>
/// <see cref="Direct_Validate"/> against <see cref="Runner_Validate"/> is that overhead with one
/// structural validator and no business rules, which is the configuration of nearly every validated
/// type. The runner holds its validators as <c>IEnumerable&lt;T&gt;</c> because that is what the
/// container hands it, so this is also the reading that shows what iterating them costs.
/// </para>
/// <para>
/// The async benchmarks deliberately use business rules that complete synchronously. A rule that
/// awaited real I/O would measure the I/O; what is interesting here is the floor - the state
/// machine, the <c>ValueTask</c>, and the gate that skips business rules when structural validation
/// already failed.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.EndToEnd)]
public class ValidationRunnerBenchmarks {
    private ValidationRunner<Customer> _structuralOnly = null!;
    private ValidationRunner<Customer> _withBusinessRule = null!;

    private Customer _valid = null!;
    private Customer _invalid = null!;

    [GlobalSetup]
    public void Setup() {
        _valid = SampleData.ValidCustomer();
        _invalid = SampleData.InvalidCustomer();

        _structuralOnly = new ValidationRunner<Customer>(
            [CustomerValidator.Instance],
            []);

        _withBusinessRule = new ValidationRunner<Customer>(
            [CustomerValidator.Instance],
            [new SynchronousBusinessRule()]);
    }

    [Benchmark(Baseline = true, Description = "Direct validator call, clean")]
    public ValidationResult Direct_Validate() => CustomerValidator.Instance.Validate(_valid);

    [Benchmark(Description = "Runner, structural only, clean")]
    public ValidationResult Runner_Validate() => _structuralOnly.Validate(_valid);

    [Benchmark(Description = "Runner, structural only, 8 failures")]
    public ValidationResult Runner_Validate_Failing() => _structuralOnly.Validate(_invalid);

    [Benchmark(Description = "Runner async, no business rules, clean")]
    public ValueTask<ValidationResult> Runner_ValidateAsync() => _structuralOnly.ValidateAsync(_valid);

    [Benchmark(Description = "Runner async, one business rule, clean - the rule runs")]
    public ValueTask<ValidationResult> Runner_ValidateAsync_WithRule() => _withBusinessRule.ValidateAsync(_valid);

    /// <summary>
    /// Structural validation fails, so the business rule is skipped entirely. This is the gate
    /// working, and against the benchmark above it is what the gate saves - which in production is
    /// a database round trip rather than the microseconds shown here.
    /// </summary>
    [Benchmark(Description = "Runner async, one business rule, failing - the rule is skipped")]
    public ValueTask<ValidationResult> Runner_ValidateAsync_Gated() => _withBusinessRule.ValidateAsync(_invalid);
}

/// <summary>
/// A business rule with no I/O in it, so the async benchmarks measure the machinery rather than a
/// simulated round trip. It does add an error, so the merge path is exercised rather than skipped.
/// </summary>
public sealed class SynchronousBusinessRule : IAsyncValidatorFor<Customer> {

    public ValueTask ValidateAsync(ValidationContext context, Customer value, CancellationToken cancellationToken) {
        if (value.Tier == "gold" && value.DiscountRate > 0.5) {
            context.AddHere("conflict", "a gold customer cannot exceed a 50% discount.");
        }

        return default;
    }
}
