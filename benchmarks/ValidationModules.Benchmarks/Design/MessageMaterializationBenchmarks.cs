using BenchmarkDotNet.Attributes;

namespace ValidationModules.Benchmarks.Design;

/// <summary>
/// Answers whether message helpers on the context can avoid the 56 bytes per error that composing
/// a message costs, and what avoiding it is worth.
/// </summary>
/// <remarks>
/// <para>
/// The 56 bytes is the <c>string.Concat</c>, not the call. A helper - free function, extension, or
/// instance method on the context - still has to produce a string if <c>ValidationError.Message</c>
/// is a string field. The only way to zero it is to store the pieces and materialize on read.
/// </para>
/// <para>
/// Three shapes, all producing byte-identical message text:
/// </para>
/// <list type="number">
/// <item><b>Literal</b> - the emitter puts the whole message in metadata. Zero allocation, biggest binary.</item>
/// <item><b>Eager</b> - a helper composes the message when the error is added.</item>
/// <item><b>Deferred</b> - the error stores field, code and a small argument payload; the message is
/// built when something reads it.</item>
/// </list>
/// <para>
/// The AddOnly benchmarks are the honest comparison for "does adding an error allocate". The
/// AddAndRead ones are what a caller serializing a 400 response actually does.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Design)]
public class MessageMaterializationBenchmarks {
    private const int Errors = 12;

    private static readonly string[] Fields =
        ["f0", "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11"];

    private static readonly string[] Literals = [
        "f0 must be at most 100 characters.", "f1 must be at most 100 characters.",
        "f2 must be at most 100 characters.", "f3 must be at most 100 characters.",
        "f4 must be at most 100 characters.", "f5 must be at most 100 characters.",
        "f6 must be at most 100 characters.", "f7 must be at most 100 characters.",
        "f8 must be at most 100 characters.", "f9 must be at most 100 characters.",
        "f10 must be at most 100 characters.", "f11 must be at most 100 characters.",
    ];

    private readonly List<EagerError> _eager = new(Errors);
    private readonly List<DeferredError> _deferred = new(Errors);

    [Benchmark(Baseline = true, Description = "Literal: emitter puts the message in metadata")]
    public int Literal_Add() {
        _eager.Clear();

        for (var i = 0; i < Errors; i++) {
            _eager.Add(new EagerError(Fields[i], "string_length", Literals[i]));
        }

        return _eager.Count;
    }

    [Benchmark(Description = "Eager: helper composes the message on Add")]
    public int Eager_Add() {
        _eager.Clear();

        for (var i = 0; i < Errors; i++) {
            _eager.Add(new EagerError(
                Fields[i],
                "string_length",
                string.Concat(Fields[i], " must be at most ", 100.ToString(), " characters.")));
        }

        return _eager.Count;
    }

    [Benchmark(Description = "Deferred: store the pieces, never read the message")]
    public int Deferred_Add() {
        _deferred.Clear();

        for (var i = 0; i < Errors; i++) {
            _deferred.Add(new DeferredError(Fields[i], ValidationCode.StringLength, 100));
        }

        return _deferred.Count;
    }

    [Benchmark(Description = "Literal + read every message (the 400-response path)")]
    public int Literal_AddAndRead() {
        Literal_Add();

        var total = 0;
        for (var i = 0; i < _eager.Count; i++) {
            total += _eager[i].Message.Length;
        }

        return total;
    }

    [Benchmark(Description = "Eager + read every message")]
    public int Eager_AddAndRead() {
        Eager_Add();

        var total = 0;
        for (var i = 0; i < _eager.Count; i++) {
            total += _eager[i].Message.Length;
        }

        return total;
    }

    [Benchmark(Description = "Deferred + read every message")]
    public int Deferred_AddAndRead() {
        Deferred_Add();

        var total = 0;
        for (var i = 0; i < _deferred.Count; i++) {
            total += _deferred[i].Message.Length;
        }

        return total;
    }
}

/// <summary>Today's shape: the message is a string field, so it must exist by the time Add returns.</summary>
public readonly record struct EagerError(string Field, string Code, string Message);

/// <summary>
/// The deferred shape. Field and code are the machine-readable truth; the message is a projection
/// of them plus the constraint's arguments.
/// </summary>
/// <remarks>
/// The argument payload is the part that decides whether this is worth it. Two longs and one
/// reference cover every built-in constraint without boxing - a length or count bound, a numeric
/// range (doubles bit-cast into the longs), and a reference for the emitted values array that
/// AllowedValues needs. Anything richer than that starts boxing, at which point the allocation is
/// back and the complexity has bought nothing.
/// </remarks>
public readonly struct DeferredError {
    private readonly long _arg0;
    private readonly long _arg1;
    private readonly object? _reference;

    public DeferredError(string field, ValidationCode code, long arg0 = 0, long arg1 = 0, object? reference = null) {
        Field = field;
        Code = code;
        _arg0 = arg0;
        _arg1 = arg1;
        _reference = reference;
    }

    public string Field { get; }

    public ValidationCode Code { get; }

    public string Message => Code switch {
        ValidationCode.Required => string.Concat(Field, " is required."),
        ValidationCode.StringLength => string.Concat(Field, " must be at most ", _arg0.ToString(), " characters."),
        ValidationCode.Range => string.Concat(Field, " must be between ", _arg0.ToString(), " and ", _arg1.ToString(), "."),
        ValidationCode.Pattern => string.Concat(Field, " is not in the required format."),
        ValidationCode.ArrayBounds => string.Concat(Field, " must contain at least ", _arg0.ToString(), " items."),
        _ => _reference as string ?? Field,
    };
}

public enum ValidationCode {
    Required = 0,
    StringLength = 1,
    Range = 2,
    Pattern = 3,
    ArrayBounds = 4,
    Enum = 5,
}
