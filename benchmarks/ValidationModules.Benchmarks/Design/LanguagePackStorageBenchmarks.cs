using System.Collections.Frozen;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace ValidationModules.Benchmarks.Design;

/// <summary>
/// What a language pack's template lookup costs under each storage the emitter could produce,
/// and what each one pays at startup.
/// </summary>
/// <remarks>
/// <para>
/// The candidates: the literal if-chain the first cut emitted; the switch expression the design
/// record promised (the compiler buckets by length and character, so it is the optimized code
/// shape); a <c>Dictionary</c> and a <c>FrozenDictionary</c> built from a static entry array; and
/// the serialized-blob alternative - length-prefixed UTF-8 pairs parsed into a Dictionary on
/// first use, measured here as its parse-and-build startup plus the same Dictionary lookups.
/// </para>
/// <para>
/// Lookups run a fixed ten-probe set per invocation - eight hits spread from the first key to the
/// last, plus two misses - because the formatter's layering makes misses ordinary: an override
/// pack misses almost every probe by design. <c>OperationsPerInvoke</c> is 10, so the report
/// reads per probe.
/// </para>
/// <para>
/// The layered pair is the composite that decides the formatter's shape: walking packs per render
/// (a one-entry override that misses, then the full pack) against one merged FrozenDictionary
/// built once per culture.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Design)]
public class LanguagePackStorageBenchmarks {

    private const int Probes = 10;

    private static readonly string[] Misses = ["missing_key_a", "string_length.nope"];

    private Dictionary<string, string> _dictionary35 = null!;
    private Dictionary<string, string> _dictionary150 = null!;
    private Dictionary<string, string> _dictionary400 = null!;
    private FrozenDictionary<string, string> _frozen35 = null!;
    private FrozenDictionary<string, string> _frozen150 = null!;
    private FrozenDictionary<string, string> _frozen400 = null!;
    private byte[] _blob35 = null!;
    private byte[] _blob150 = null!;
    private byte[] _blob400 = null!;
    private string[] _probes35 = null!;
    private string[] _probes150 = null!;
    private string[] _probes400 = null!;
    private FrozenDictionary<string, string> _merged = null!;

    [GlobalSetup]
    public void Setup() {
        _dictionary35 = ToDictionary(LanguagePackStorageData.Entries35);
        _dictionary150 = ToDictionary(LanguagePackStorageData.Entries150);
        _dictionary400 = ToDictionary(LanguagePackStorageData.Entries400);
        _frozen35 = _dictionary35.ToFrozenDictionary(StringComparer.Ordinal);
        _frozen150 = _dictionary150.ToFrozenDictionary(StringComparer.Ordinal);
        _frozen400 = _dictionary400.ToFrozenDictionary(StringComparer.Ordinal);
        _blob35 = Serialize(LanguagePackStorageData.Entries35);
        _blob150 = Serialize(LanguagePackStorageData.Entries150);
        _blob400 = Serialize(LanguagePackStorageData.Entries400);
        _probes35 = ProbeSet(LanguagePackStorageData.Entries35);
        _probes150 = ProbeSet(LanguagePackStorageData.Entries150);
        _probes400 = ProbeSet(LanguagePackStorageData.Entries400);

        // The merged shape: the one-entry override layered over the full pack, resolved once.
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in LanguagePackStorageData.Entries35) {
            merged[pair.Key] = pair.Value;
        }

        merged["required"] = "Merci de renseigner {field}.";
        _merged = merged.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // ---- Lookup: ten probes, eight hits spread first-to-last plus two misses -------------------

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_IfChain_35() => Drain(LanguagePackStorageData.IfChain35, _probes35);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_IfChain_150() => Drain(LanguagePackStorageData.IfChain150, _probes150);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_IfChain_400() => Drain(LanguagePackStorageData.IfChain400, _probes400);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_Switch_35() => Drain(LanguagePackStorageData.Switch35, _probes35);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_Switch_150() => Drain(LanguagePackStorageData.Switch150, _probes150);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_Switch_400() => Drain(LanguagePackStorageData.Switch400, _probes400);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_Dictionary_35() => Drain(_dictionary35, _probes35);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_Dictionary_150() => Drain(_dictionary150, _probes150);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_Dictionary_400() => Drain(_dictionary400, _probes400);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_Frozen_35() => Drain(_frozen35, _probes35);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_Frozen_150() => Drain(_frozen150, _probes150);

    [Benchmark(OperationsPerInvoke = Probes)]
    public int Lookup_Frozen_400() => Drain(_frozen400, _probes400);

    // ---- The composite the formatter actually runs ---------------------------------------------

    /// <summary>Per render today: shape then code against the override, then the full pack.</summary>
    [Benchmark]
    public string? Layered_WalkPacksPerRender() {
        // The override pack (one entry) misses both probes; the full pack answers the second.
        return OverridePack("range.greater_than") ?? OverridePack("range")
            ?? LanguagePackStorageData.IfChain35("range.greater_than") ?? LanguagePackStorageData.IfChain35("range");

        static string? OverridePack(string key) => key == "required" ? "Merci de renseigner {field}." : null;
    }

    /// <summary>Per render merged: the layering resolved once, two frozen probes at most.</summary>
    [Benchmark]
    public string? Layered_MergedFrozen() =>
        _merged.TryGetValue("range.greater_than", out var template) ? template :
        _merged.TryGetValue("range", out template) ? template : null;

    // ---- Startup: what each structure costs to exist -------------------------------------------

    [Benchmark]
    public Dictionary<string, string> Startup_Dictionary_35() => ToDictionary(LanguagePackStorageData.Entries35);

    [Benchmark]
    public Dictionary<string, string> Startup_Dictionary_150() => ToDictionary(LanguagePackStorageData.Entries150);

    [Benchmark]
    public Dictionary<string, string> Startup_Dictionary_400() => ToDictionary(LanguagePackStorageData.Entries400);

    [Benchmark]
    public FrozenDictionary<string, string> Startup_Frozen_35() =>
        LanguagePackStorageData.Entries35.ToFrozenDictionary(StringComparer.Ordinal);

    [Benchmark]
    public FrozenDictionary<string, string> Startup_Frozen_150() =>
        LanguagePackStorageData.Entries150.ToFrozenDictionary(StringComparer.Ordinal);

    [Benchmark]
    public FrozenDictionary<string, string> Startup_Frozen_400() =>
        LanguagePackStorageData.Entries400.ToFrozenDictionary(StringComparer.Ordinal);

    [Benchmark]
    public Dictionary<string, string> Startup_BlobParse_35() => Parse(_blob35);

    [Benchmark]
    public Dictionary<string, string> Startup_BlobParse_150() => Parse(_blob150);

    [Benchmark]
    public Dictionary<string, string> Startup_BlobParse_400() => Parse(_blob400);

    // ---- Machinery ------------------------------------------------------------------------------

    private static int Drain(Func<string, string?> lookup, string[] probes) {
        var found = 0;

        foreach (var probe in probes) {
            if (lookup(probe) is not null) {
                found++;
            }
        }

        return found;
    }

    private static int Drain(Dictionary<string, string> table, string[] probes) {
        var found = 0;

        foreach (var probe in probes) {
            if (table.TryGetValue(probe, out _)) {
                found++;
            }
        }

        return found;
    }

    private static int Drain(FrozenDictionary<string, string> table, string[] probes) {
        var found = 0;

        foreach (var probe in probes) {
            if (table.TryGetValue(probe, out _)) {
                found++;
            }
        }

        return found;
    }

    private static string[] ProbeSet(KeyValuePair<string, string>[] entries) {
        var n = entries.Length;
        int[] positions = [0, 2, n / 8, n / 4, n / 2, (3 * n) / 4, n - 2, n - 1];

        return [.. positions.Select(position => entries[position].Key), .. Misses];
    }

    private static Dictionary<string, string> ToDictionary(KeyValuePair<string, string>[] entries) {
        var table = new Dictionary<string, string>(entries.Length, StringComparer.Ordinal);

        foreach (var pair in entries) {
            table[pair.Key] = pair.Value;
        }

        return table;
    }

    /// <summary>The serialized form: count, then length-prefixed UTF-8 key/value pairs.</summary>
    private static byte[] Serialize(KeyValuePair<string, string>[] entries) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(entries.Length);

        foreach (var pair in entries) {
            writer.Write(pair.Key);
            writer.Write(pair.Value);
        }

        writer.Flush();

        return stream.ToArray();
    }

    private static Dictionary<string, string> Parse(byte[] blob) {
        using var reader = new BinaryReader(new MemoryStream(blob), Encoding.UTF8);

        var count = reader.ReadInt32();
        var table = new Dictionary<string, string>(count, StringComparer.Ordinal);

        for (var i = 0; i < count; i++) {
            table[reader.ReadString()] = reader.ReadString();
        }

        return table;
    }
}
