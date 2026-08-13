using BenchmarkDotNet.Attributes;
using ValidationModules.Benchmarks.Models;

namespace ValidationModules.Benchmarks.Components;

/// <summary>
/// What each constraint costs on its own, through the code the generator actually emits.
/// </summary>
/// <remarks>
/// <para>
/// One model per constraint, each carrying exactly one rule, so a reading here is the constraint
/// plus a validator call and nothing else. That is what makes them subtractable from the
/// end-to-end numbers: if a pass over <see cref="Customer"/> costs more than the sum of its
/// constraints, the excess belongs to the pass rather than to any rule in it.
/// </para>
/// <para>
/// <see cref="Failing"/> = false is the reading that matters. Constraints are evaluated on every
/// request and fail on very few of them, and the failing column mostly prices the message
/// composition that <c>Design/MessageMaterializationBenchmarks</c> covers directly.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Component)]
public class ConstraintBenchmarks {
    private readonly ValidationErrorCollector _collector = new();

    private RequiredOnly _required = null!;
    private StringLengthOnly _stringLength = null!;
    private RangeOnly _range = null!;
    private PatternOnly _pattern = null!;
    private AllowedValuesOnly _allowedValues = null!;
    private ItemCountOnly _itemCount = null!;

    /// <summary>Whether the single constraint on each model is violated.</summary>
    [Params(false, true)]
    public bool Failing { get; set; }

    [GlobalSetup]
    public void Setup() {
        _required = new RequiredOnly { Value = Failing ? null : "present" };
        _stringLength = new StringLengthOnly { Value = Failing ? new string('x', 101) : "within bounds" };
        _range = new RangeOnly { Value = Failing ? 500 : 42 };
        _pattern = new PatternOnly { Value = Failing ? "nope" : "ABC-1234" };
        _allowedValues = new AllowedValuesOnly { Value = Failing ? "platinum" : "gold" };
        _itemCount = new ItemCountOnly { Value = Failing ? [] : ["one", "two"] };
    }

    [Benchmark(Baseline = true, Description = "[Required] - string.IsNullOrWhiteSpace")]
    public int Required() {
        _collector.Reset();

        RequiredOnlyValidator.Instance.ValidateInto(_collector, _required);

        return _collector.Count;
    }

    [Benchmark(Description = "[StringLength] - two integer comparisons")]
    public int StringLength() {
        _collector.Reset();

        StringLengthOnlyValidator.Instance.ValidateInto(_collector, _stringLength);

        return _collector.Count;
    }

    [Benchmark(Description = "[Range] - two comparisons, no boxing")]
    public int Range() {
        _collector.Reset();

        RangeOnlyValidator.Instance.ValidateInto(_collector, _range);

        return _collector.Count;
    }

    /// <summary>
    /// The reference form, so this is a call into a <c>[GeneratedRegex]</c> the consumer declared.
    /// <c>Design/RegexStrategyBenchmarks</c> prices the alternatives the emitter could have used.
    /// </summary>
    [Benchmark(Description = "[Pattern] - a [GeneratedRegex] match")]
    public int Pattern() {
        _collector.Reset();

        PatternOnlyValidator.Instance.ValidateInto(_collector, _pattern);

        return _collector.Count;
    }

    /// <summary>
    /// Emitted as a chain of ordinal string comparisons rather than a set lookup, which is the right
    /// shape for the two-to-five values these sets almost always hold. This is where that would show
    /// up if it were not.
    /// </summary>
    [Benchmark(Description = "[AllowedValues] - a chain of string comparisons")]
    public int AllowedValues() {
        _collector.Reset();

        AllowedValuesOnlyValidator.Instance.ValidateInto(_collector, _allowedValues);

        return _collector.Count;
    }

    [Benchmark(Description = "[ItemCount] - a Count read and two comparisons")]
    public int ItemCount() {
        _collector.Reset();

        ItemCountOnlyValidator.Instance.ValidateInto(_collector, _itemCount);

        return _collector.Count;
    }
}
