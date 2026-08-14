using BenchmarkDotNet.Attributes;
using FluentValidation;
using ValidationModules.Benchmarks.Comparative.Engines;
using ValidationModules.Benchmarks.Comparative.Models;

namespace ValidationModules.Benchmarks.Comparative.Comparisons;

/// <summary>
/// What each engine costs before it has validated anything, and what it costs to get that wrong.
/// </summary>
/// <remarks>
/// <para>
/// A generated validator has no construction cost to speak of: the rules are straight-line code in
/// a stateless singleton, so reaching it is a static field read. A FluentValidation validator builds
/// its rule graph in its constructor, and each <c>RuleFor</c> takes an expression tree that has to
/// be turned into a property accessor.
/// </para>
/// <para>
/// That difference is invisible when both are held for the lifetime of the process, which is what
/// <c>FlatValidationComparison</c> assumes and what every engine's documentation recommends. It
/// stops being invisible the moment a validator is constructed on the request path - which is not a
/// hypothetical: §10.2 of the implementation plan records a shipping framework rebuilding its rule
/// graph, including a <c>RegexOptions.Compiled</c> regex, on every single request.
/// </para>
/// <para>
/// So the pair to read is <see cref="Fv_Shared_Validate"/> against
/// <see cref="Fv_ConstructPerCall_Validate"/>. The first is FluentValidation used correctly; the
/// second is the same engine used the way that framework used its own, and the ratio between them
/// is what the mistake costs.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(ComparativeCategories.Startup)]
public class ValidatorConstructionComparison {

    // Hoisted: constructing per invocation would put an allocation on the measured path.
    private static readonly CustomerValidator CustomerValidatorShared = new();
    private Customer _valid = null!;

    [GlobalSetup]
    public void Setup() {
        _valid = SampleData.ValidCustomer();

        _ = CustomerValidatorShared.IsValid(_valid);
        _ = CustomerFluentValidator.Instance.Validate(_valid);
    }

    // ---- Reaching the validator ----------------------------------------------------------------

    [Benchmark(Baseline = true, Description = "ValidationModules - reach the singleton")]
    public IValidatorFor<Customer> Vm_Reach() => CustomerValidatorShared;

    [Benchmark(Description = "FluentValidation - construct a validator")]
    public IValidator<Customer> Fv_Construct() => new CustomerFluentValidator();

    /// <summary>
    /// The nested case, where constructing the parent also constructs nothing extra - the child
    /// validators are shared singletons here. A codebase that news up its children inside each
    /// parent's constructor pays this multiplied by the graph.
    /// </summary>
    [Benchmark(Description = "FluentValidation - construct the nested order validator")]
    public IValidator<Order> Fv_ConstructNested() => new OrderFluentValidator();

    // ---- Reaching it and using it, which is what a request actually does ------------------------

    [Benchmark(Description = "ValidationModules - singleton + validate")]
    public bool Vm_Shared_Validate() => CustomerValidatorShared.IsValid(_valid);

    [Benchmark(Description = "FluentValidation - shared validator + validate (correct usage)")]
    public bool Fv_Shared_Validate() => CustomerFluentValidator.Instance.Validate(_valid).IsValid;

    /// <summary>
    /// The anti-pattern, quantified: a fresh validator per call, then one validation through it.
    /// </summary>
    [Benchmark(Description = "FluentValidation - construct per call + validate (the §10.2 shape)")]
    public bool Fv_ConstructPerCall_Validate() => new CustomerFluentValidator().Validate(_valid).IsValid;
}
