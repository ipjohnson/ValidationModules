using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;

namespace ValidationModules.Benchmarks;

/// <summary>
/// The configuration every suite in this repository runs under, so that numbers taken on different
/// days and from different projects are comparable.
/// </summary>
/// <remarks>
/// <para>
/// Native AOT is the target runtime and several of the questions here turn on it - whether ILC
/// inlines the runtime helpers back into the caller, what a devirtualized
/// <c>IValidatorFor&lt;T&gt;</c> call costs. The JIT job runs alongside it because a difference that
/// appears under one and not the other is worth knowing about, and because it is the runtime most
/// consumers will debug under.
/// </para>
/// <para>
/// Categories are always shown: nearly every reading here is a comparison within a category, and a
/// category column makes a full-suite summary readable without cross-referencing the source. The
/// job column is hidden instead, because the job is already a row group.
/// </para>
/// <para>
/// <b>Both jobs name their runtime explicitly, and must keep doing so.</b> The default suite's
/// project sets <c>PublishAot</c>, and BenchmarkDotNet resolves the toolchain for a runtime-less job
/// from the project's own properties - so a job that does not say <c>WithRuntime</c> silently
/// becomes a Native AOT job there and a CoreCLR one elsewhere. That is also why <c>--quick</c> is
/// handled here rather than by forwarding BenchmarkDotNet's <c>--job short</c>, which has no runtime
/// and picks up AOT by that route.
/// </para>
/// </remarks>
public static class BenchmarkConfig {

    /// <summary>Both runtimes. Selected by <c>--runtime both</c>, and the default suite's default.</summary>
    public static IConfig Create(bool quick = false) => Base().AddJob(Jit(quick)).AddJob(Aot(quick));

    /// <summary>
    /// The JIT job only. Selected by <c>--runtime jit</c>, and the fast path for a check that is
    /// about relative cost rather than about the published binary.
    /// </summary>
    public static IConfig CreateJitOnly(bool quick = false) => Base().AddJob(Jit(quick));

    /// <summary>The Native AOT job only. Selected by <c>--runtime aot</c>.</summary>
    public static IConfig CreateAotOnly(bool quick = false) => Base().AddJob(Aot(quick));

    private static IConfig Base() =>
        DefaultConfig.Instance
            .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared))
            .HideColumns(Column.Job)
            .AddColumn(CategoriesColumn.Default);

    private static Job Jit(bool quick) =>
        Counts(Job.Default.WithRuntime(CoreRuntime.Core10_0).WithId("jit"), quick);

    private static Job Aot(bool quick) =>
        Counts(Job.Default.WithRuntime(NativeAotRuntime.Net10_0).WithId("aot"), quick);

    /// <summary>
    /// Fifteen iterations after five warmups is enough to separate readings a few nanoseconds
    /// apart, which several of the component comparisons are. The quick counts are not - they exist
    /// to answer "does it still run and is it roughly the same", and a number taken under them
    /// should not be quoted.
    /// </summary>
    private static Job Counts(Job job, bool quick) =>
        quick
            ? job.WithWarmupCount(1).WithIterationCount(3).WithLaunchCount(1)
            : job.WithWarmupCount(5).WithIterationCount(15);
}
