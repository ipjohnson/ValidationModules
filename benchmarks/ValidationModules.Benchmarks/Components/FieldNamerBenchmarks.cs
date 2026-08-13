using BenchmarkDotNet.Attributes;
using ValidationModules.Naming;

namespace ValidationModules.Benchmarks.Components;

/// <summary>
/// The naming policies, which the generator calls at build time and the FluentValidation adapter
/// calls at run time.
/// </summary>
/// <remarks>
/// <para>
/// Generated validators pay none of this: field names are baked into the emitted source as string
/// literals, so <c>CamelCaseFieldNamer</c> runs inside the generator and never in the application.
/// The adapter is the one that pays, because it receives a CLR property path from FluentValidation
/// per failure and has to convert it to stay in step with those literals.
/// </para>
/// <para>
/// So these numbers price the adapter's per-failure overhead, and the gap between
/// <see cref="Pascal_ToFieldName"/> - which returns its argument - and the other two is the whole
/// of it.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Component)]
public class FieldNamerBenchmarks {
    private const string PropertyName = "PostalCode";
    private const string AcronymPropertyName = "HTTPStatusCode";
    private const string ParentPath = "order.shipTo";

    [Benchmark(Baseline = true, Description = "PascalCase - returns the argument unchanged")]
    public string Pascal_ToFieldName() => PascalCaseFieldNamer.Instance.ToFieldName(PropertyName);

    [Benchmark(Description = "CamelCase - lowers the first character")]
    public string Camel_ToFieldName() => CamelCaseFieldNamer.Instance.ToFieldName(PropertyName);

    [Benchmark(Description = "SnakeCase - a StringBuilder pass")]
    public string Snake_ToFieldName() => SnakeCaseFieldNamer.Instance.ToFieldName(PropertyName);

    /// <summary>
    /// The acronym case, where snake-casing has to look ahead to decide that <c>HTTPStatus</c> is
    /// two words. Compared against the plain name, this is what that lookahead costs.
    /// </summary>
    [Benchmark(Description = "SnakeCase over an acronym - the lookahead path")]
    public string Snake_ToFieldName_Acronym() => SnakeCaseFieldNamer.Instance.ToFieldName(AcronymPropertyName);

    [Benchmark(Description = "Combine - one concat")]
    public string Combine() => CamelCaseFieldNamer.Instance.Combine(ParentPath, "postalCode");

    [Benchmark(Description = "CombineIndex - concat plus an int format")]
    public string CombineIndex() => CamelCaseFieldNamer.Instance.CombineIndex(ParentPath, "lines", 3);
}
