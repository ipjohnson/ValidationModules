using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;

namespace ValidationModules.Benchmarks;

/// <summary>
/// What a pattern constraint costs under each strategy available to a source generator.
/// </summary>
/// <remarks>
/// <para>
/// <c>[GeneratedRegex]</c> is the one the plan mandates and the one we cannot emit: source
/// generators do not see each other's output, so the partial method is never implemented. It is
/// included here hand-written, as the ceiling to measure the alternatives against.
/// </para>
/// <para>
/// <c>RegexOptions.Compiled</c> is included as a reference point only. It emits IL through
/// Reflection.Emit, so it is unavailable under Native AOT - where it degrades silently to the
/// interpreter rather than throwing, which is exactly the failure mode this library exists to
/// remove.
/// </para>
/// <para>
/// The specialized matcher is what a generator that compiled a conservative subset of patterns
/// itself would emit. Most OpenAPI and JSON Schema patterns are this simple, so it is the
/// interesting option rather than a straw man.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public partial class RegexStrategyBenchmarks {
    private const string Pattern = "^[A-Z]{3}$";

    private static readonly Regex Interpreted = new(Pattern, RegexOptions.None);
    private static readonly Regex Compiled = new(Pattern, RegexOptions.Compiled);

    [GeneratedRegex(Pattern)]
    private static partial Regex SourceGenerated();

    /// <summary>Hit and miss, so neither branch is predicted away.</summary>
    [Params("ABC", "abcd")]
    public string Value { get; set; } = "ABC";

    [Benchmark(Baseline = true, Description = "[GeneratedRegex] - the ceiling, unavailable to us")]
    public bool GeneratedRegex() => SourceGenerated().IsMatch(Value);

    [Benchmark(Description = "static readonly Regex, interpreted - what we emit")]
    public bool StaticInterpreted() => Interpreted.IsMatch(Value);

    [Benchmark(Description = "static readonly Regex, Compiled - not AOT-viable")]
    public bool StaticCompiled() => Compiled.IsMatch(Value);

    [Benchmark(Description = "specialized matcher - what a subset compiler would emit")]
    public bool Specialized() {
        var value = Value;

        if (value.Length != 3) {
            return false;
        }

        for (var i = 0; i < 3; i++) {
            if (value[i] is < 'A' or > 'Z') {
                return false;
            }
        }

        return true;
    }
}
