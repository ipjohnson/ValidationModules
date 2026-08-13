using BenchmarkDotNet.Attributes;

namespace ValidationModules.Benchmarks.Design;

/// <summary>
/// Compares the three candidate shapes for what the generator emits per constraint.
/// </summary>
/// <remarks>
/// <para>
/// The question is where the predicate and the message live:
/// </para>
/// <list type="number">
/// <item><b>Inline</b> - the emitter writes the comparison and a literal message at every site.</item>
/// <item><b>Split</b> - the emitter writes the comparison; the runtime composes the message, so the
/// call happens only on failure.</item>
/// <item><b>Helper</b> - the runtime does the comparison and the message, so the call happens
/// unconditionally.</item>
/// </list>
/// <para>
/// Size is already measured: 313, 207 and 197 native bytes per constraint site respectively. This
/// measures the other axis. The success path is the one that matters - production traffic mostly
/// validates cleanly - so <see cref="FailingFields"/> = 0 is the case to read first.
/// </para>
/// <para>
/// The helpers live in this assembly rather than in ValidationModules.Runtime, because the runtime
/// does not carry them yet. In the real shape they would be cross-assembly, which both the JIT and
/// ILC still inline through; if that assumption ever looks shaky it is worth re-measuring with them
/// moved.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Design)]
public class EmissionShapeBenchmarks {
    private readonly ValidationErrorCollector _collector = new();

    private Model _model = null!;

    /// <summary>
    /// How many of the model's 12 fields fail. 0 is the hot path, 12 the worst case, 1 the
    /// realistic bad-request shape.
    /// </summary>
    [Params(0, 1, 12)]
    public int FailingFields { get; set; }

    [GlobalSetup]
    public void Setup() {
        string? Field(int index) => index < FailingFields ? null : "value";

        _model = new Model {
            F0 = Field(0), F1 = Field(1), F2 = Field(2), F3 = Field(3),
            F4 = Field(4), F5 = Field(5), F6 = Field(6), F7 = Field(7),
            F8 = Field(8), F9 = Field(9), F10 = Field(10), F11 = Field(11),
        };
    }

    [Benchmark(Baseline = true)]
    public int Inline() {
        _collector.Reset();
        var context = new ValidationContext(_collector);
        InlineValidator.Instance.Validate(ref context, _model);

        return _collector.Count;
    }

    [Benchmark]
    public int Split() {
        _collector.Reset();
        var context = new ValidationContext(_collector);
        SplitValidator.Instance.Validate(ref context, _model);

        return _collector.Count;
    }

    [Benchmark]
    public int Helper() {
        _collector.Reset();
        var context = new ValidationContext(_collector);
        HelperValidator.Instance.Validate(ref context, _model);

        return _collector.Count;
    }
}

public sealed record Model {
    public string? F0 { get; init; }
    public string? F1 { get; init; }
    public string? F2 { get; init; }
    public string? F3 { get; init; }
    public string? F4 { get; init; }
    public string? F5 { get; init; }
    public string? F6 { get; init; }
    public string? F7 { get; init; }
    public string? F8 { get; init; }
    public string? F9 { get; init; }
    public string? F10 { get; init; }
    public string? F11 { get; init; }
}

/// <summary>Shape 1: comparison and message both emitted at every site.</summary>
public sealed class InlineValidator : IValidatorFor<Model> {
    public static readonly InlineValidator Instance = new();

    private InlineValidator() { }

    public void Validate(ref ValidationContext ctx, Model value) {
        if (string.IsNullOrWhiteSpace(value.F0)) ctx.Add("f0", "required", "f0 is required.");
        else if (value.F0.Length > 100) ctx.Add("f0", "string_length", "f0 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F1)) ctx.Add("f1", "required", "f1 is required.");
        else if (value.F1.Length > 100) ctx.Add("f1", "string_length", "f1 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F2)) ctx.Add("f2", "required", "f2 is required.");
        else if (value.F2.Length > 100) ctx.Add("f2", "string_length", "f2 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F3)) ctx.Add("f3", "required", "f3 is required.");
        else if (value.F3.Length > 100) ctx.Add("f3", "string_length", "f3 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F4)) ctx.Add("f4", "required", "f4 is required.");
        else if (value.F4.Length > 100) ctx.Add("f4", "string_length", "f4 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F5)) ctx.Add("f5", "required", "f5 is required.");
        else if (value.F5.Length > 100) ctx.Add("f5", "string_length", "f5 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F6)) ctx.Add("f6", "required", "f6 is required.");
        else if (value.F6.Length > 100) ctx.Add("f6", "string_length", "f6 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F7)) ctx.Add("f7", "required", "f7 is required.");
        else if (value.F7.Length > 100) ctx.Add("f7", "string_length", "f7 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F8)) ctx.Add("f8", "required", "f8 is required.");
        else if (value.F8.Length > 100) ctx.Add("f8", "string_length", "f8 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F9)) ctx.Add("f9", "required", "f9 is required.");
        else if (value.F9.Length > 100) ctx.Add("f9", "string_length", "f9 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F10)) ctx.Add("f10", "required", "f10 is required.");
        else if (value.F10.Length > 100) ctx.Add("f10", "string_length", "f10 must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(value.F11)) ctx.Add("f11", "required", "f11 is required.");
        else if (value.F11.Length > 100) ctx.Add("f11", "string_length", "f11 must be at most 100 characters.");
    }
}

/// <summary>Shape 2: comparison emitted, message composed by the runtime on failure only.</summary>
public sealed class SplitValidator : IValidatorFor<Model> {
    public static readonly SplitValidator Instance = new();

    private SplitValidator() { }

    public void Validate(ref ValidationContext ctx, Model value) {
        if (string.IsNullOrWhiteSpace(value.F0)) Rules.AddRequired(ref ctx, "f0");
        else if (value.F0.Length > 100) Rules.AddStringLength(ref ctx, "f0", 100);
        if (string.IsNullOrWhiteSpace(value.F1)) Rules.AddRequired(ref ctx, "f1");
        else if (value.F1.Length > 100) Rules.AddStringLength(ref ctx, "f1", 100);
        if (string.IsNullOrWhiteSpace(value.F2)) Rules.AddRequired(ref ctx, "f2");
        else if (value.F2.Length > 100) Rules.AddStringLength(ref ctx, "f2", 100);
        if (string.IsNullOrWhiteSpace(value.F3)) Rules.AddRequired(ref ctx, "f3");
        else if (value.F3.Length > 100) Rules.AddStringLength(ref ctx, "f3", 100);
        if (string.IsNullOrWhiteSpace(value.F4)) Rules.AddRequired(ref ctx, "f4");
        else if (value.F4.Length > 100) Rules.AddStringLength(ref ctx, "f4", 100);
        if (string.IsNullOrWhiteSpace(value.F5)) Rules.AddRequired(ref ctx, "f5");
        else if (value.F5.Length > 100) Rules.AddStringLength(ref ctx, "f5", 100);
        if (string.IsNullOrWhiteSpace(value.F6)) Rules.AddRequired(ref ctx, "f6");
        else if (value.F6.Length > 100) Rules.AddStringLength(ref ctx, "f6", 100);
        if (string.IsNullOrWhiteSpace(value.F7)) Rules.AddRequired(ref ctx, "f7");
        else if (value.F7.Length > 100) Rules.AddStringLength(ref ctx, "f7", 100);
        if (string.IsNullOrWhiteSpace(value.F8)) Rules.AddRequired(ref ctx, "f8");
        else if (value.F8.Length > 100) Rules.AddStringLength(ref ctx, "f8", 100);
        if (string.IsNullOrWhiteSpace(value.F9)) Rules.AddRequired(ref ctx, "f9");
        else if (value.F9.Length > 100) Rules.AddStringLength(ref ctx, "f9", 100);
        if (string.IsNullOrWhiteSpace(value.F10)) Rules.AddRequired(ref ctx, "f10");
        else if (value.F10.Length > 100) Rules.AddStringLength(ref ctx, "f10", 100);
        if (string.IsNullOrWhiteSpace(value.F11)) Rules.AddRequired(ref ctx, "f11");
        else if (value.F11.Length > 100) Rules.AddStringLength(ref ctx, "f11", 100);
    }
}

/// <summary>Shape 3: the runtime does the comparison too, so the call is unconditional.</summary>
public sealed class HelperValidator : IValidatorFor<Model> {
    public static readonly HelperValidator Instance = new();

    private HelperValidator() { }

    public void Validate(ref ValidationContext ctx, Model value) {
        Rules.RequiredThenLength(ref ctx, "f0", value.F0, 100);
        Rules.RequiredThenLength(ref ctx, "f1", value.F1, 100);
        Rules.RequiredThenLength(ref ctx, "f2", value.F2, 100);
        Rules.RequiredThenLength(ref ctx, "f3", value.F3, 100);
        Rules.RequiredThenLength(ref ctx, "f4", value.F4, 100);
        Rules.RequiredThenLength(ref ctx, "f5", value.F5, 100);
        Rules.RequiredThenLength(ref ctx, "f6", value.F6, 100);
        Rules.RequiredThenLength(ref ctx, "f7", value.F7, 100);
        Rules.RequiredThenLength(ref ctx, "f8", value.F8, 100);
        Rules.RequiredThenLength(ref ctx, "f9", value.F9, 100);
        Rules.RequiredThenLength(ref ctx, "f10", value.F10, 100);
        Rules.RequiredThenLength(ref ctx, "f11", value.F11, 100);
    }
}

/// <summary>The runtime primitives the two non-inline shapes call.</summary>
public static class Rules {

    public static void AddRequired(ref ValidationContext ctx, string field) =>
        ctx.Add(field, "required", string.Concat(field, " is required."));

    public static void AddStringLength(ref ValidationContext ctx, string field, int max) =>
        ctx.Add(field, "string_length", string.Concat(field, " must be at most ", max.ToString(), " characters."));

    /// <summary>
    /// One call covering both constraints on the field, so that the suppression rule survives -
    /// which is the whole reason this shape needs a combined method rather than two independent
    /// ones.
    /// </summary>
    public static void RequiredThenLength(ref ValidationContext ctx, string field, string? value, int max) {
        if (string.IsNullOrWhiteSpace(value)) {
            AddRequired(ref ctx, field);
        } else if (value.Length > max) {
            AddStringLength(ref ctx, field, max);
        }
    }
}
