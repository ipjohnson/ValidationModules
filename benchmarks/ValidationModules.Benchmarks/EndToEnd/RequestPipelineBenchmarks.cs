using BenchmarkDotNet.Attributes;
using ValidationModules.Benchmarks.Models;

namespace ValidationModules.Benchmarks.EndToEnd;

/// <summary>
/// The four shapes a request filter could be written in, priced against each other.
/// </summary>
/// <remarks>
/// <para>
/// This is the benchmark the library exists for. §10.2 and §10.3 of the plan record what the
/// incumbent does per request - rebuild the rule graph, construct a <c>RegexOptions.Compiled</c>
/// regex, then <c>MakeGenericType</c> and <c>Invoke</c> to reach the validator. The shapes here are
/// the alternatives, from the one a filter written by habit lands on to the one a generated filter
/// should emit.
/// </para>
/// <para>
/// Only <see cref="ResolvePerRequest"/> and the pooled variants differ in where the validator comes
/// from; all four run identical constraint code. So the spread between them is entirely the cost of
/// the plumbing around the validator, which is the thing worth deciding once and emitting.
/// </para>
/// <para>
/// No benchmark here rebuilds a rule graph, because generated validators make that unrepresentable
/// - the rules are straight-line code in a stateless singleton. That is the point rather than an
/// omission.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.EndToEnd)]
public class RequestPipelineBenchmarks {

    /// <summary>Resolved once, as a generic filter constructed by generated code would.</summary>
    private readonly IValidatorFor<Order> _resolvedOnce = OrderValidator.Instance;

    /// <summary>Pooled across requests, as a filter holding one per handler instance would.</summary>
    private readonly ValidationErrorCollector _pooledCollector = new();

    private IValidatorFor<Order>[] _registered = [];
    private Order _order = null!;

    [GlobalSetup]
    public void Setup() {
        _order = SampleData.ValidOrder();

        // Stands in for what a container hands back: the validator arrives as a sequence that has
        // to be walked, not as a field that was already resolved.
        _registered = [OrderValidator.Instance];
    }

    /// <summary>
    /// The shape a filter written by habit lands on: find the validator per request, then allocate
    /// a collector and a result for it.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Look up the validator per request, fresh collector")]
    public bool ResolvePerRequest() {
        IValidatorFor<Order>? validator = null;
        for (var i = 0; i < _registered.Length; i++) {
            validator = _registered[i];
        }

        return validator!.Validate(_order).IsValid;
    }

    /// <summary>
    /// The validator resolved once at handler construction - the single change §9 of the plan makes
    /// by turning the filter generic - with the collector still allocated per request.
    /// </summary>
    [Benchmark(Description = "Validator resolved once, fresh collector per request")]
    public bool ResolvedOnce_FreshCollector() => _resolvedOnce.Validate(_order).IsValid;

    /// <summary>
    /// Both resolved once. The collector is reset rather than allocated, and no
    /// <see cref="ValidationResult"/> is materialized on the clean path.
    /// </summary>
    [Benchmark(Description = "Validator resolved once, pooled collector - the target shape")]
    public bool ResolvedOnce_PooledCollector() {
        _pooledCollector.Reset();

        _resolvedOnce.ValidateInto(_pooledCollector, _order);

        return !_pooledCollector.HasErrors;
    }

    /// <summary>
    /// The pooled shape carried through to a result, which is what a filter that has to hand errors
    /// to a response writer actually needs. On a clean pass <c>ToResult</c> returns the shared
    /// instance, so this should land on top of the benchmark above rather than above it.
    /// </summary>
    [Benchmark(Description = "Pooled collector, then ToResult - what a filter returns")]
    public ValidationResult PooledCollector_ToResult() {
        _pooledCollector.Reset();

        _resolvedOnce.ValidateInto(_pooledCollector, _order);

        return _pooledCollector.ToResult();
    }
}
