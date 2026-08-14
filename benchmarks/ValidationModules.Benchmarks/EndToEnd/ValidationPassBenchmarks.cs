using BenchmarkDotNet.Attributes;
using ValidationModules.Benchmarks.Models;

namespace ValidationModules.Benchmarks.EndToEnd;

/// <summary>
/// A whole validation pass through generated code, over the two model shapes a request body
/// normally has: flat scalars, and an object with a nested object and a list of children.
/// </summary>
/// <remarks>
/// <para>
/// This is the headline number - what a consumer pays to validate one payload. Everything in
/// <c>Components/</c> exists to explain a reading here.
/// </para>
/// <para>
/// Three entry points are measured against each other because they differ in what they allocate,
/// not in what they check: <c>IsValid</c> answers a bool, <c>Validate</c> materializes a
/// <see cref="ValidationResult"/>, and <c>ValidateInto</c> writes into a collector the caller owns.
/// All three run every constraint - there is no first-failure exit - so on a clean payload the gap
/// between them is allocation and nothing else.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.EndToEnd)]
public class ValidationPassBenchmarks {

    // Hoisted: constructing per invocation would put an allocation on the measured path.
    private static readonly CustomerValidator CustomerValidatorShared = new();
    private static readonly OrderValidator OrderValidatorShared = new();
    private readonly ValidationErrorCollector _pooled = new();

    private Customer _validCustomer = null!;
    private Customer _oneFailure = null!;
    private Customer _invalidCustomer = null!;
    private Order _validOrder = null!;
    private Order _invalidOrder = null!;

    [GlobalSetup]
    public void Setup() {
        _validCustomer = SampleData.ValidCustomer();
        _oneFailure = SampleData.CustomerWithOneFailure();
        _invalidCustomer = SampleData.InvalidCustomer();
        _validOrder = SampleData.ValidOrder();
        _invalidOrder = SampleData.InvalidOrder();
    }

    // ---- Flat model: 8 constraints, no nesting -------------------------------------------------

    [Benchmark(Baseline = true, Description = "Flat, clean, IsValid - the hot path")]
    public bool Flat_Clean_IsValid() => CustomerValidatorShared.IsValid(_validCustomer);

    [Benchmark(Description = "Flat, clean, Validate - materializes a result")]
    public ValidationResult Flat_Clean_Validate() => CustomerValidatorShared.Validate(_validCustomer);

    [Benchmark(Description = "Flat, clean, ValidateInto a pooled collector")]
    public bool Flat_Clean_ValidateInto() {
        _pooled.Reset();

        CustomerValidatorShared.ValidateInto(_pooled, _validCustomer);

        return _pooled.HasErrors;
    }

    /// <summary>
    /// One field wrong out of eight, which is what a real bad request looks like. The realistic
    /// failure cost, as against the worst case below.
    /// </summary>
    [Benchmark(Description = "Flat, 1 failure, Validate")]
    public ValidationResult Flat_OneFailure_Validate() => CustomerValidatorShared.Validate(_oneFailure);

    [Benchmark(Description = "Flat, 8 failures, Validate - the worst case")]
    public ValidationResult Flat_AllFailing_Validate() => CustomerValidatorShared.Validate(_invalidCustomer);

    // ---- Nested model: an object, an address, and three collection elements ---------------------

    [Benchmark(Description = "Nested, clean, IsValid")]
    public bool Nested_Clean_IsValid() => OrderValidatorShared.IsValid(_validOrder);

    [Benchmark(Description = "Nested, clean, Validate")]
    public ValidationResult Nested_Clean_Validate() => OrderValidatorShared.Validate(_validOrder);

    [Benchmark(Description = "Nested, clean, ValidateInto a pooled collector")]
    public bool Nested_Clean_ValidateInto() {
        _pooled.Reset();

        OrderValidatorShared.ValidateInto(_pooled, _validOrder);

        return _pooled.HasErrors;
    }

    /// <summary>
    /// One failure at each level, so every one of <c>reference</c>, <c>buyer.age</c>,
    /// <c>shipTo.postalCode</c> and <c>lines[1].quantity</c> has to walk its parent chain and build
    /// a string. The path machinery's real cost, rather than its cost at depth 1.
    /// </summary>
    [Benchmark(Description = "Nested, 1 failure per level, Validate")]
    public ValidationResult Nested_Failing_Validate() => OrderValidatorShared.Validate(_invalidOrder);

    /// <summary>
    /// The same failing payload read all the way out to its error list, which is what a handler
    /// producing a 400 body does. Against the benchmark above, this is what enumerating the result
    /// and touching every message costs.
    /// </summary>
    [Benchmark(Description = "Nested, failing, then read every error - the 400 path")]
    public int Nested_Failing_ReadErrors() {
        var result = OrderValidatorShared.Validate(_invalidOrder);

        var total = 0;
        for (var i = 0; i < result.Errors.Count; i++) {
            total += result.Errors[i].Field.Length + result.Errors[i].Message.Length;
        }

        return total;
    }
}
