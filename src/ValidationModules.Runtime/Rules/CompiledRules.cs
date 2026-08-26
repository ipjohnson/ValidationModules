using System.Text.RegularExpressions;

namespace ValidationModules.Rules;

/// <summary>
/// One declared rule, reduced to something that can be applied.
/// </summary>
/// <remarks>
/// <para>
/// These exist only for the engine that has no generator behind it. Where the generator ran, the
/// same declaration became a branch in emitted source and none of these types is reachable.
/// </para>
/// <para>
/// Every one of them reaches the error through the same <see cref="ValidationContextExtensions"/>
/// helpers the emitter calls, so codes, composed messages, ordering, suppression and field paths are
/// the collector's and are shared with generated validators rather than reimplemented here. That is
/// what keeps the two engines substitutable (API-SURFACE.md §19.9).
/// </para>
/// </remarks>
internal interface ICompiledRule<in T> {

    /// <summary>The field this rule reports against, used to order <c>Required</c> first (§4.2).</summary>
    string Field { get; }

    /// <summary>Whether this is the <c>Required</c> check for <see cref="Field"/>.</summary>
    bool IsRequired { get; }

    void Apply(ref ValidationContext context, T value);
}

/// <summary>
/// The base every rule shares: a field name and the default answers to the ordering questions.
/// </summary>
internal abstract class CompiledRule<T> : ICompiledRule<T> {

    protected CompiledRule(string field) => Field = field;

    public string Field { get; }

    public virtual bool IsRequired => false;

    public abstract void Apply(ref ValidationContext context, T value);
}

/// <summary>Whitespace counts as missing unless the declaration opted out - §12 Q5.</summary>
/// <summary>
/// One field's rules, run in order, stopping if a <c>Required</c> among them fails.
/// </summary>
/// <remarks>
/// <para>
/// This is the rule engine's equivalent of the <c>else if</c> the emitter writes. Generated code
/// short-circuits a failed Required lexically, at compile time; a described validator applies a
/// flat array and has no <c>else</c> to put it in, so the short-circuit lives here instead - local
/// to the field it concerns, rather than as a rule the collector applies to every engine.
/// </para>
/// <para>
/// <see cref="ValidationRules{T}.Build"/> already groups a field's rules together and hoists its
/// Required rules to the front, so the chain is contiguous and the required ones are the prefix.
/// Failure is detected by comparing the collector's change token rather than its count, which is
/// linear - a chain per field would compound that across a model.
/// </para>
/// </remarks>
internal sealed class FieldChainRule<T> : CompiledRule<T> {
    private readonly ICompiledRule<T>[] _chain;
    private readonly int _requiredCount;

    public FieldChainRule(string field, ICompiledRule<T>[] chain, int requiredCount) : base(field) {
        _chain = chain;
        _requiredCount = requiredCount;
    }

    public override bool IsRequired => _requiredCount > 0;

    public override void Apply(ref ValidationContext context, T value) {
        var token = context.ChangeToken;

        for (var i = 0; i < _requiredCount; i++) {
            _chain[i].Apply(ref context, value);

            // A Required that failed suppresses the rest of its own field, and nothing else.
            if (!ReferenceEquals(token, context.ChangeToken)) {
                return;
            }
        }

        for (var i = _requiredCount; i < _chain.Length; i++) {
            _chain[i].Apply(ref context, value);
        }
    }
}

internal sealed class RequiredStringRule<T> : CompiledRule<T> {
    private readonly Func<T, string?> _read;
    private readonly bool _allowEmptyStrings;

    public RequiredStringRule(string field, Func<T, string?> read, bool allowEmptyStrings) : base(field) {
        _read = read;
        _allowEmptyStrings = allowEmptyStrings;
    }

    public override bool IsRequired => true;

    public override void Apply(ref ValidationContext context, T value) {
        var read = _read(value);
        var missing = _allowEmptyStrings ? read is null : string.IsNullOrWhiteSpace(read);

        if (missing) {
            context.AddRequired(Field);
        }
    }
}

/// <summary>
/// Reference-typed <c>Required</c>. Separate from the nullable-value-typed rule so that neither
/// boxes: an unconstrained <c>TValue is null</c> would box every value type it tested.
/// </summary>
internal sealed class RequiredReferenceRule<T, TValue> : CompiledRule<T> where TValue : class {
    private readonly Func<T, TValue?> _read;

    public RequiredReferenceRule(string field, Func<T, TValue?> read) : base(field) => _read = read;

    public override bool IsRequired => true;

    public override void Apply(ref ValidationContext context, T value) {
        if (_read(value) is null) {
            context.AddRequired(Field);
        }
    }
}

internal sealed class RequiredNullableRule<T, TValue> : CompiledRule<T> where TValue : struct {
    private readonly Func<T, TValue?> _read;

    public RequiredNullableRule(string field, Func<T, TValue?> read) : base(field) => _read = read;

    public override bool IsRequired => true;

    public override void Apply(ref ValidationContext context, T value) {
        if (!_read(value).HasValue) {
            context.AddRequired(Field);
        }
    }
}

internal sealed class StringLengthRule<T> : CompiledRule<T> {
    private readonly Func<T, string?> _read;
    private readonly int _min;
    private readonly int _max;

    public StringLengthRule(string field, Func<T, string?> read, int min, int max) : base(field) {
        _read = read;
        _min = min;
        _max = max;
    }

    public override void Apply(ref ValidationContext context, T value) {
        // A null value is Required's business. Reporting a length failure for it as well would be
        // the duplicate the collector's suppression exists to stop, and only stops when a Required
        // was actually declared.
        if (_read(value) is { } read && (read.Length < _min || read.Length > _max)) {
            context.AddStringLength(Field, _min, _max);
        }
    }
}

internal sealed class RangeRule<T, TValue> : CompiledRule<T>
    where TValue : struct, IComparable<TValue>, IFormattable {

    private readonly Func<T, TValue?> _read;
    private readonly TValue? _min;
    private readonly TValue? _max;

    public RangeRule(string field, Func<T, TValue?> read, TValue? min, TValue? max) : base(field) {
        _read = read;
        _min = min;
        _max = max;
    }

    /// <summary>
    /// Either bound may be absent, and an absent one is not compared against and is not named in
    /// the message - matching what the emitted path does for <c>[Range(Min = 1)]</c>.
    /// </summary>
    public override void Apply(ref ValidationContext context, T value) {
        if (_read(value) is not { } read) {
            return;
        }

        var below = _min is { } lower && read.CompareTo(lower) < 0;
        var above = _max is { } upper && read.CompareTo(upper) > 0;

        if (!below && !above) {
            return;
        }

        if (_min is { } min && _max is { } max) {
            context.AddRange(Field, min, max);
        } else if (_min is { } only) {
            context.AddRangeAtLeast(Field, only);
        } else if (_max is { } cap) {
            context.AddRangeAtMost(Field, cap);
        }
    }
}

internal sealed class PatternRule<T> : CompiledRule<T> {
    private readonly Func<T, string?> _read;
    private readonly Regex _pattern;

    /// <summary>
    /// The <see cref="Regex"/> is resolved once, here, rather than per validation. The accessor is a
    /// <c>[GeneratedRegex]</c> method group, whose backing instance is created by its own type
    /// initializer - plan §10.2 is the record of what happens when this is built per call instead.
    /// </summary>
    public PatternRule(string field, Func<T, string?> read, Regex pattern) : base(field) {
        _read = read;
        _pattern = pattern;
    }

    public override void Apply(ref ValidationContext context, T value) {
        if (_read(value) is { } read && !_pattern.IsMatch(read)) {
            context.AddPattern(Field);
        }
    }
}

internal sealed class AllowedValuesRule<T, TValue> : CompiledRule<T> {
    private readonly Func<T, TValue> _read;
    private readonly TValue[] _allowed;
    private readonly string _rendered;

    public AllowedValuesRule(string field, Func<T, TValue> read, TValue[] allowed) : base(field) {
        _read = read;
        _allowed = allowed;
        _rendered = string.Join(", ", allowed);
    }

    public override void Apply(ref ValidationContext context, T value) {
        var read = _read(value);

        if (read is null) {
            return;
        }

        for (var i = 0; i < _allowed.Length; i++) {
            if (EqualityComparer<TValue>.Default.Equals(read, _allowed[i])) {
                return;
            }
        }

        context.AddAllowedValues(Field, _rendered);
    }
}

internal sealed class ItemCountRule<T, TElement> : CompiledRule<T> {
    private readonly Func<T, IReadOnlyCollection<TElement>?> _read;
    private readonly int _min;
    private readonly int _max;

    public ItemCountRule(string field, Func<T, IReadOnlyCollection<TElement>?> read, int min, int max) : base(field) {
        _read = read;
        _min = min;
        _max = max;
    }

    public override void Apply(ref ValidationContext context, T value) {
        if (_read(value) is { } read && (read.Count < _min || read.Count > _max)) {
            context.AddItemCount(Field, _min, _max);
        }
    }
}

internal sealed class UniqueItemsRule<T, TElement> : CompiledRule<T> {
    private readonly Func<T, IEnumerable<TElement>?> _read;

    public UniqueItemsRule(string field, Func<T, IEnumerable<TElement>?> read) : base(field) => _read = read;

    public override void Apply(ref ValidationContext context, T value) {
        if (_read(value) is { } read && !ConstraintChecks.AllUnique(read)) {
            context.AddUniqueItems(Field);
        }
    }
}

/// <summary>
/// <c>[MultipleOf]</c> for a value that divides exactly - anything integral, and <c>decimal</c>.
/// </summary>
internal sealed class MultipleOfRule<T> : CompiledRule<T> {
    private readonly Func<T, decimal?> _read;
    private readonly decimal _divisor;

    public MultipleOfRule(string field, Func<T, decimal?> read, decimal divisor) : base(field) {
        _read = read;
        _divisor = divisor;
    }

    public override void Apply(ref ValidationContext context, T value) {
        if (_read(value) is { } read && read % _divisor != 0m) {
            context.AddMultipleOf(Field, _divisor);
        }
    }
}

/// <summary>
/// <c>[MultipleOf]</c> for a binary floating-point value, which does not divide exactly.
/// </summary>
/// <remarks>
/// Separate from <see cref="MultipleOfRule{T}"/> so both engines answer alike: the emitted path
/// routes double and float through <see cref="ConstraintChecks.IsMultipleOf(double, decimal)"/> for
/// the reason that method documents, and this is the same call.
/// </remarks>
internal sealed class MultipleOfApproximateRule<T> : CompiledRule<T> {
    private readonly Func<T, double?> _read;
    private readonly decimal _divisor;

    public MultipleOfApproximateRule(string field, Func<T, double?> read, decimal divisor) : base(field) {
        _read = read;
        _divisor = divisor;
    }

    public override void Apply(ref ValidationContext context, T value) {
        if (_read(value) is { } read && !ConstraintChecks.IsMultipleOf(read, _divisor)) {
            context.AddMultipleOf(Field, _divisor);
        }
    }
}

internal sealed class NestedRule<T, TValue> : CompiledRule<T> where TValue : class {
    private readonly Func<T, TValue?> _read;
    private readonly IValidatorFor<TValue> _validator;

    public NestedRule(string field, Func<T, TValue?> read, IValidatorFor<TValue> validator) : base(field) {
        _read = read;
        _validator = validator;
    }

    public override void Apply(ref ValidationContext context, T value) {
        if (_read(value) is { } read) {
            var nested = context.Push(Field);
            _validator.Validate(ref nested, read);
        }
    }
}

internal sealed class EachRule<T, TElement> : CompiledRule<T> where TElement : class {
    private readonly Func<T, IReadOnlyList<TElement?>?> _read;
    private readonly IValidatorFor<TElement> _validator;

    public EachRule(string field, Func<T, IReadOnlyList<TElement?>?> read, IValidatorFor<TElement> validator)
        : base(field) {
        _read = read;
        _validator = validator;
    }

    public override void Apply(ref ValidationContext context, T value) {
        if (_read(value) is not { } items) {
            return;
        }

        // Indexed rather than foreach: enumerating an interface-typed collection boxes the struct
        // enumerator, and a clean pass over a collection property would then allocate. Same reason
        // the emitter writes a for loop (ValidatorEmitter.cs:156).
        for (var i = 0; i < items.Count; i++) {
            if (items[i] is { } element) {
                var elementContext = context.PushIndex(Field, i);
                _validator.Validate(ref elementContext, element);
            }
        }
    }
}

/// <summary>
/// An <c>Ensure</c>. Its message was rendered from the predicate's own source when the rule was
/// declared, so nothing is composed here.
/// </summary>
internal sealed class PredicateRule<T> : CompiledRule<T> {
    private readonly Func<T, bool> _predicate;
    private readonly string _code;
    private readonly string _message;
    private readonly ValidationSeverity _severity;

    public PredicateRule(string field, Func<T, bool> predicate, string code, string message, ValidationSeverity severity)
        : base(field) {
        _predicate = predicate;
        _code = code;
        _message = message;
        _severity = severity;
    }

    public override void Apply(ref ValidationContext context, T value) {
        if (!_predicate(value)) {
            context.Add(Field, _code, _message, _severity);
        }
    }
}

/// <summary>An <c>Apply</c>. The author owns everything about what it records.</summary>
internal sealed class ActionRule<T> : CompiledRule<T> {
    private readonly RuleAction<T> _action;

    public ActionRule(RuleAction<T> action) : base(string.Empty) => _action = action;

    public override void Apply(ref ValidationContext context, T value) => _action(ref context, value);
}
