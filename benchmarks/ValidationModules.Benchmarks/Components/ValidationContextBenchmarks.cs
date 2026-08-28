using BenchmarkDotNet.Attributes;

namespace ValidationModules.Benchmarks.Components;

/// <summary>
/// What the path machinery costs: a push, an add, and the render that turns the two segments a
/// context carries into <c>lines[3]...shipTo.postalCode</c>.
/// </summary>
/// <remarks>
/// <para>
/// The claim under test is §4 of the plan: "a validation pass that finds nothing must allocate
/// nothing". <see cref="Push_NoAdd"/> is that claim stated as a benchmark - it descends
/// <see cref="Depth"/> levels and records no error, and its allocation column must read zero at
/// every depth. Everything else here prices what happens once something does fail.
/// </para>
/// <para>
/// Every benchmark resets a pooled collector first, so the reset is in all of them equally and
/// cancels out of any comparison between them. <see cref="ErrorCollectorBenchmarks"/> prices the
/// reset itself.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Component)]
public class ValidationContextBenchmarks {
    private readonly ValidationErrorCollector _collector = new();

    /// <summary>
    /// How many levels to descend before adding. 1 is a top-level field, 4 a realistic nested
    /// payload, 16 deep enough that anything scaling with depth would show.
    /// </summary>
    /// <remarks>
    /// Under the path log, 16 was the depth at which the parent-chain walk dominated. A context now
    /// keeps two segments whatever the depth, so the add columns should be flat across all three
    /// and only the pushes themselves should scale - which is what makes these rows a regression
    /// test rather than a measurement of the render.
    /// </remarks>
    [Params(1, 4, 16)]
    public int Depth { get; set; }

    [Benchmark(Baseline = true, Description = "Add at the root - no path to walk")]
    public int Add_AtRoot() {
        _collector.Reset();

        var context = new ValidationContext(_collector);
        context.ReportRequired("postalCode");

        return _collector.Count;
    }

    [Benchmark(Description = "Push to depth, add nothing - the clean pass, must be 0 B")]
    public int Push_NoAdd() {
        _collector.Reset();

        var context = new ValidationContext(_collector);
        for (var i = 0; i < Depth; i++) {
            context = context.Push("home");
        }

        return _collector.Count;
    }

    [Benchmark(Description = "Push to depth, then add - object nesting")]
    public int Push_ThenAdd() {
        _collector.Reset();

        var context = new ValidationContext(_collector);
        for (var i = 0; i < Depth; i++) {
            context = context.Push("home");
        }

        context.ReportRequired("postalCode");

        return _collector.Count;
    }

    [Benchmark(Description = "PushIndex to depth, then add - collection nesting")]
    public int PushIndex_ThenAdd() {
        _collector.Reset();

        var context = new ValidationContext(_collector);
        for (var i = 0; i < Depth; i++) {
            context = context.PushIndex("lines", i);
        }

        context.ReportRequired("sku");

        return _collector.Count;
    }

    [Benchmark(Description = "PushKey to depth, then add - dictionary nesting")]
    public int PushKey_ThenAdd() {
        _collector.Reset();

        var context = new ValidationContext(_collector);
        for (var i = 0; i < Depth; i++) {
            context = context.PushKey("items", "sku-1");
        }

        context.ReportRequired("name");

        return _collector.Count;
    }

    /// <summary>
    /// A type-level failure rather than a field one, which is the shape a cross-field rule uses.
    /// It skips the leaf append, so the difference against <see cref="Push_ThenAdd"/> is what
    /// appending the field name costs.
    /// </summary>
    [Benchmark(Description = "AddHere at depth - a type-level rule")]
    public int AddHere_AtDepth() {
        _collector.Reset();

        var context = new ValidationContext(_collector);
        for (var i = 0; i < Depth; i++) {
            context = context.Push("home");
        }

        context.ReportHere("conflict", "the address is inconsistent.");

        return _collector.Count;
    }

    /// <summary>
    /// Siblings rather than a chain: <see cref="Depth"/> pushes off one parent, each adding an
    /// error. This is what validating a collection looks like from the collector's side, and it is
    /// the case a depth-indexed path stack would have got wrong - see the remarks on
    /// <see cref="ValidationContext"/>.
    /// </summary>
    [Benchmark(Description = "Sibling pushes, one error each - a collection pass")]
    public int Siblings_EachAdd() {
        _collector.Reset();

        var root = new ValidationContext(_collector);
        for (var i = 0; i < Depth; i++) {
            root.PushIndex("lines", i).ReportRequired("sku");
        }

        return _collector.Count;
    }
}
