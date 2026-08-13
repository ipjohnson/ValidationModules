using BenchmarkDotNet.Configs;

namespace ValidationModules.Benchmarks;

/// <summary>
/// Pulls this repository's own switches out of the command line before the rest is handed to
/// BenchmarkDotNet.
/// </summary>
/// <remarks>
/// <para>
/// Two switches, both of which exist because the BenchmarkDotNet equivalent does something subtly
/// different here:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>--runtime jit|aot|both</c>. BenchmarkDotNet's <c>--runtimes</c> <i>adds</i> a runtime to the
/// jobs the config already declares rather than replacing them, so once the config has declared
/// both there is no way to ask for one. The distinction matters because the AOT job publishes
/// through ILC, which turns a thirty-second check into a five-minute one.
/// </item>
/// <item>
/// <c>--quick</c>. BenchmarkDotNet's <c>--job short</c> declares no runtime, and the toolchain for a
/// runtime-less job is resolved from the project's own properties - so in the default suite, whose
/// project sets <c>PublishAot</c>, <c>--job short</c> quietly becomes a <i>Native AOT</i> job and
/// runs alongside the jit job rather than shortening it.
/// </item>
/// </list>
/// <para>
/// Everything else is forwarded untouched, so <c>--filter</c>, <c>--job</c>, <c>--anyCategories</c>
/// and <c>--list</c> behave exactly as documented upstream.
/// </para>
/// </remarks>
public static class BenchmarkArguments {

    private const string RuntimeSwitch = "--runtime";
    private const string QuickSwitch = "--quick";

    /// <summary>
    /// Splits the command line into the config it selects and the arguments to forward.
    /// </summary>
    /// <param name="args">The raw command line.</param>
    /// <param name="defaultToJitOnly">
    /// What <c>--runtime</c> means when it is absent. The default suite wants both, because Native
    /// AOT is the runtime the library targets; the comparative suite wants jit, because publishing
    /// a third-party engine through ILC on every run is a poor trade for a number that only moves
    /// when that engine does.
    /// </param>
    public static (IConfig Config, string[] Forwarded) Parse(string[] args, bool defaultToJitOnly = false) {
        var selection = defaultToJitOnly ? "jit" : "both";
        var quick = false;
        var forwarded = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++) {
            // Both spellings, because a reader who has seen --filter=x will write --runtime=jit.
            if (args[i].StartsWith(RuntimeSwitch + "=", StringComparison.OrdinalIgnoreCase)) {
                selection = args[i][(RuntimeSwitch.Length + 1)..];
                continue;
            }

            if (string.Equals(args[i], RuntimeSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) {
                selection = args[++i];
                continue;
            }

            if (string.Equals(args[i], QuickSwitch, StringComparison.OrdinalIgnoreCase)) {
                quick = true;
                continue;
            }

            forwarded.Add(args[i]);
        }

        return (Select(selection, quick), forwarded.ToArray());
    }

    private static IConfig Select(string selection, bool quick) => selection.ToLowerInvariant() switch {
        "jit" => BenchmarkConfig.CreateJitOnly(quick),
        "aot" => BenchmarkConfig.CreateAotOnly(quick),
        "both" => BenchmarkConfig.Create(quick),
        _ => throw new ArgumentException(
            $"--runtime must be jit, aot or both; got '{selection}'.", nameof(selection)),
    };
}
