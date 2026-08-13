using BenchmarkDotNet.Attributes;

namespace ValidationModules.Benchmarks.Components;

/// <summary>
/// What Required-suppression costs as the number of already-failed fields grows.
/// </summary>
/// <remarks>
/// <para>
/// Suppression lives in <c>ValidationErrorCollector.AddCore</c> rather than in the emitted
/// <c>else if</c> chain, because the FluentValidation adapter maps failures another engine already
/// produced and has no control flow to put an <c>else</c> in. The cost of putting it there is that
/// every add scans the list of fields that have failed Required.
/// </para>
/// <para>
/// That list is a <c>List&lt;string&gt;</c> walked linearly rather than a <c>HashSet</c>, on the
/// grounds that it is short in any realistic pass. This sweep is what keeps "short" a measured
/// claim: <see cref="FailedFields"/> = 64 is a payload where every field of a large model came back
/// null, and it is the reading that would justify changing the structure if it were expensive.
/// </para>
/// <para>
/// Each benchmark runs a whole pass rather than a single add, so the quadratic term - every add
/// scanning what the previous adds appended - is included rather than measured away.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Component)]
public class SuppressionBenchmarks {
    private readonly ValidationErrorCollector _collector = new();

    private string[] _fields = [];

    /// <summary>How many distinct fields fail Required during the pass.</summary>
    [Params(1, 8, 64)]
    public int FailedFields { get; set; }

    [GlobalSetup]
    public void Setup() {
        // Composed once. Building a field name inside the benchmark would price string formatting
        // rather than the scan.
        _fields = new string[FailedFields];
        for (var i = 0; i < FailedFields; i++) {
            _fields[i] = $"field{i}";
        }
    }

    [Benchmark(Baseline = true, Description = "N fields fail Required")]
    public int RequiredFailures() {
        _collector.Reset();

        var context = new ValidationContext(_collector);
        for (var i = 0; i < _fields.Length; i++) {
            context.AddRequired(_fields[i]);
        }

        return _collector.Count;
    }

    /// <summary>
    /// The same pass, then a second rule firing on every one of those fields. Each of those adds is
    /// suppressed, so the difference against the baseline is the scan and the early return - the
    /// price of the rule, with none of the storage.
    /// </summary>
    [Benchmark(Description = "N fields fail Required, then N suppressed adds")]
    public int RequiredFailures_ThenSuppressed() {
        _collector.Reset();

        var context = new ValidationContext(_collector);
        for (var i = 0; i < _fields.Length; i++) {
            context.AddRequired(_fields[i]);
        }

        for (var i = 0; i < _fields.Length; i++) {
            context.AddPattern(_fields[i]);
        }

        return _collector.Count;
    }

    /// <summary>
    /// N failures that are not Required, so nothing is ever added to the suppression list and no add
    /// scans anything. The floor: what the same number of errors costs with the rule switched off.
    /// </summary>
    [Benchmark(Description = "N non-Required failures - no list, no scan")]
    public int NonRequiredFailures() {
        _collector.Reset();

        var context = new ValidationContext(_collector);
        for (var i = 0; i < _fields.Length; i++) {
            context.AddPattern(_fields[i]);
        }

        return _collector.Count;
    }
}
