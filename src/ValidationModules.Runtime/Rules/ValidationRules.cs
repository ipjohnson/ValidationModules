using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ValidationModules.Naming;
using ValidationModules.Rules;

namespace ValidationModules;

/// <summary>
/// Accumulates the rules declared by an <see cref="IValidationRulesFor{T}.Describe"/> body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is called when the generator is present.</b> The generator reads the calls as
/// syntax and emits straight-line code; this type is what makes the same body work when it is not.
/// Both engines are pinned to the same output by API-SURFACE.md §19.9 and §16's conformance suite.
/// </para>
/// <para>
/// <b>Selectors are <see cref="Func{T, TResult}"/>, never <c>Expression&lt;Func&lt;T, TResult&gt;&gt;</c>.</b>
/// Plan §2 bans <c>Expression.Compile</c>, and an expression tree would have to be compiled to be
/// executable. What replaces it is <see cref="CallerArgumentExpressionAttribute"/>: the compiler
/// hands over the selector's own source text, so the field name is read from
/// <c>"x =&gt; x.Age"</c> once, when the rule is declared, and never again.
/// </para>
/// </remarks>
/// <typeparam name="T">The type being described.</typeparam>
public sealed class ValidationRules<T> {
    private readonly List<ICompiledRule<T>> _rules = new();
    private readonly IValidationFieldNamer _namer;
    private readonly IValidatorProvider? _nested;

    /// <summary>The distinct condition predicates, each evaluated once per validation pass.</summary>
    private readonly List<Func<T, bool>> _atoms = new();

    /// <summary>Sets of signed atom entries; a rule's <c>ConditionIndex</c> indexes this.</summary>
    private readonly List<int[]> _slots = new();

    /// <summary>The conditions of the blocks currently open around whatever is being declared.</summary>
    private readonly List<int> _open = new();

    internal ValidationRules(IValidationFieldNamer namer, IValidatorProvider? nested) {
        _namer = namer;
        _nested = nested;
    }

    /// <summary>
    /// Anchors to a property without declaring anything, for when the anchor reads better stated
    /// than carried by the first rule.
    /// </summary>
    public PropertyRules<T, TValue> For<TValue>(
        Func<T, TValue> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        // Anchors without declaring anything, so it marks the statement itself - otherwise a
        // `rules.For(x => x.A).Length(2).When(...)` would stamp from whatever came before it.
        LastStatementStart = _rules.Count;

        return new PropertyRules<T, TValue>(this, FieldOf(field, selector), value);
    }

    /// <summary>
    /// Declares that a string must be present. Whitespace counts as missing - §12 Q5.
    /// </summary>
    /// <remarks>
    /// The opt-out is <see cref="RequiredAllowingEmpty"/> rather than a flag on this method. It has
    /// to be: an optional parameter here would leave this overload and the reference-typed one with
    /// parameter lists of different lengths, which is what the "non-generic wins" tie-break needs
    /// them not to have, and <c>rules.Required(x =&gt; x.Name)</c> on a string would be ambiguous
    /// rather than resolving to the overload that knows about whitespace.
    /// </remarks>
    public PropertyRules<T, string?> Required(
        Func<T, string?> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new RequiredStringRule<T>(name, value, allowEmptyStrings: false));

        return new PropertyRules<T, string?>(this, name, value);
    }

    /// <summary>
    /// Declares that a string must be non-null, accepting empty and whitespace-only values.
    /// </summary>
    public PropertyRules<T, string?> RequiredAllowingEmpty(
        Func<T, string?> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new RequiredStringRule<T>(name, value, allowEmptyStrings: true));

        return new PropertyRules<T, string?>(this, name, value);
    }

    /// <summary>Declares that a reference-typed value must be present.</summary>
    public PropertyRules<T, TValue?> Required<TValue>(
        Func<T, TValue?> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null)
        where TValue : class {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new RequiredReferenceRule<T, TValue>(name, value));

        return new PropertyRules<T, TValue?>(this, name, value);
    }

    /// <summary>
    /// Declares that a nullable value type must carry a value.
    /// </summary>
    /// <remarks>
    /// A third overload rather than one unconstrained <c>TValue</c>: testing an unconstrained
    /// generic for null boxes it, so a clean pass over an <c>int?</c> would allocate. The constraints
    /// also make <c>Required</c> on a non-nullable value type - VM0004, a rule that can never fail -
    /// harder to write by accident.
    /// </remarks>
    public PropertyRules<T, TValue?> Required<TValue>(
        Func<T, TValue?> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null)
        where TValue : struct {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new RequiredNullableRule<T, TValue>(name, value));

        return new PropertyRules<T, TValue?>(this, name, value);
    }

    /// <summary>Declares a string's length bounds. A null value is <c>Required</c>'s business.</summary>
    public PropertyRules<T, string?> Length(
        Func<T, string?> value,
        int min = 0,
        int max = int.MaxValue,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new StringLengthRule<T>(name, value, min, max));

        return new PropertyRules<T, string?>(this, name, value);
    }

    /// <summary>Declares an inclusive range.</summary>
    public PropertyRules<T, TValue?> Range<TValue>(
        Func<T, TValue?> value,
        TValue min,
        TValue max,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null)
        where TValue : struct, IComparable<TValue>, IFormattable {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new RangeRule<T, TValue>(name, value, min, max));

        return new PropertyRules<T, TValue?>(this, name, value);
    }

    /// <summary>
    /// Declares an inclusive lower bound and no upper one - <c>[Range(Min = 1)]</c>.
    /// </summary>
    /// <remarks>
    /// A separate method rather than an optional <c>max</c>, because a nullable bound parameter
    /// costs the type inference that lets <c>Range(x =&gt; x.Age, 0, 120)</c> be written without
    /// naming <c>TValue</c>. Reporting is <see cref="ValidationCodes.Range"/> either way.
    /// </remarks>
    public PropertyRules<T, TValue?> RangeAtLeast<TValue>(
        Func<T, TValue?> value,
        TValue min,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null)
        where TValue : struct, IComparable<TValue>, IFormattable {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new RangeRule<T, TValue>(name, value, min, null));

        return new PropertyRules<T, TValue?>(this, name, value);
    }

    /// <summary>Declares an inclusive upper bound and no lower one - <c>[Range(Max = 99)]</c>.</summary>
    public PropertyRules<T, TValue?> RangeAtMost<TValue>(
        Func<T, TValue?> value,
        TValue max,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null)
        where TValue : struct, IComparable<TValue>, IFormattable {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new RangeRule<T, TValue>(name, value, null, max));

        return new PropertyRules<T, TValue?>(this, name, value);
    }

    /// <summary>
    /// Declares a pattern, taken as the accessor for a <c>[GeneratedRegex]</c> partial method.
    /// </summary>
    /// <remarks>
    /// Taking the accessor rather than a pattern string is what keeps this AOT-clean without a
    /// policy: there is no inline form to diagnose, so VM0017's +1.16 MB cannot arise here, and the
    /// short spelling is the good one. The accessor is invoked once, now.
    /// </remarks>
    public PropertyRules<T, string?> Pattern(
        Func<T, string?> value,
        Func<Regex> pattern,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        ArgumentNullException.ThrowIfNull(pattern);

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new PatternRule<T>(name, value, pattern()));

        return new PropertyRules<T, string?>(this, name, value);
    }

    /// <summary>
    /// Declares the permitted set. Written as a collection expression -
    /// <c>rules.AllowedValues(x =&gt; x.Status, ["open", "closed"])</c>.
    /// </summary>
    /// <remarks>
    /// Not <c>params</c>, which it cannot be: <c>params</c> has to come last, leaving nowhere for the
    /// <see cref="CallerArgumentExpressionAttribute"/> parameter, and a <c>params</c> overload
    /// forwarding to this one captures its own parameter name rather than the caller's selector
    /// text. Field inference would silently stop working - which is how this was found.
    /// </remarks>
    public PropertyRules<T, TValue> AllowedValues<TValue>(
        Func<T, TValue> value,
        TValue[] allowed,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        ArgumentNullException.ThrowIfNull(allowed);

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new AllowedValuesRule<T, TValue>(name, value, allowed));

        return new PropertyRules<T, TValue>(this, name, value);
    }

    /// <summary>
    /// Declares element-count bounds.
    /// </summary>
    /// <remarks>
    /// <see cref="IReadOnlyList{T}"/> rather than <see cref="IReadOnlyCollection{T}"/> so that this
    /// and <see cref="Each{TElement}"/> take the same shape and chain. Arrays and
    /// <see cref="List{T}"/> both qualify; a set does not, and wants an explicit
    /// <see cref="Ensure"/>.
    /// </remarks>
    public PropertyRules<T, IReadOnlyList<TElement>?> Count<TElement>(
        Func<T, IReadOnlyList<TElement>?> value,
        int min = 0,
        int max = int.MaxValue,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new ItemCountRule<T, TElement>(name, value, min, max));

        return new PropertyRules<T, IReadOnlyList<TElement>?>(this, name, value);
    }

    /// <summary>Declares that the collection's elements must all differ.</summary>
    /// <remarks>
    /// <see cref="IEnumerable{T}"/> rather than the <see cref="IReadOnlyList{T}"/> that
    /// <see cref="Count{TElement}"/> takes, because uniqueness enumerates rather than reading a
    /// count - so a set-typed or enumerable-only property is declarable here where a count is not.
    /// </remarks>
    public PropertyRules<T, IEnumerable<TElement>?> Unique<TElement>(
        Func<T, IEnumerable<TElement>?> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new UniqueItemsRule<T, TElement>(name, value));

        return new PropertyRules<T, IEnumerable<TElement>?>(this, name, value);
    }

    /// <summary>Declares that an integral value must be an exact multiple of a divisor.</summary>
    /// <remarks>
    /// Three overloads rather than one generic method, because the divisor's own type is what
    /// resolves them: <c>MultipleOf(x =&gt; x.Quantity, 5)</c> picks this one, <c>0.05m</c> picks
    /// the decimal one and <c>0.01</c> the double one. A single generic would need the caller to
    /// name <c>TValue</c>, which is the trap <see cref="RangeAtLeast{TValue}"/> exists to avoid.
    /// </remarks>
    public PropertyRules<T, long?> MultipleOf(
        Func<T, long?> value,
        long divisor,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new MultipleOfRule<T>(name, target => value(target), divisor));

        return new PropertyRules<T, long?>(this, name, value);
    }

    /// <summary>Declares that a decimal value must be an exact multiple of a divisor.</summary>
    public PropertyRules<T, decimal?> MultipleOf(
        Func<T, decimal?> value,
        decimal divisor,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new MultipleOfRule<T>(name, value, divisor));

        return new PropertyRules<T, decimal?>(this, name, value);
    }

    /// <summary>
    /// Declares that a floating-point value must be a multiple of a divisor, decided in the decimal
    /// domain - see <see cref="ConstraintChecks.IsMultipleOf(double, decimal)"/>.
    /// </summary>
    public PropertyRules<T, double?> MultipleOf(
        Func<T, double?> value,
        double divisor,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null) {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new MultipleOfApproximateRule<T>(name, value, (decimal)divisor));

        return new PropertyRules<T, double?>(this, name, value);
    }

    /// <summary>Descends into a nested object, the equivalent of <c>[ValidateNested]</c>.</summary>
    public PropertyRules<T, TValue?> Nested<TValue>(
        Func<T, TValue?> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null)
        where TValue : class {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new NestedRule<T, TValue>(name, value, ValidatorFor<TValue>(name)));

        return new PropertyRules<T, TValue?>(this, name, value);
    }

    /// <summary>Descends into each element of a collection.</summary>
    public PropertyRules<T, IReadOnlyList<TElement>?> Each<TElement>(
        Func<T, IReadOnlyList<TElement>?> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null)
        where TElement : class {

        var name = FieldOf(field, selector);
        LastStatementStart = _rules.Count;
        Add(new EachRule<T, TElement>(name, value, ValidatorFor<TElement>(name)));

        return new PropertyRules<T, IReadOnlyList<TElement>?>(this, name, value);
    }

    /// <summary>
    /// Declares a rule the six constraints cannot say: a cross-field comparison, arithmetic, or
    /// anything else with no schema meaning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The message is the predicate, rendered</b> - <c>x =&gt; x.Start &lt; x.End</c> reports
    /// <c>"start &lt; end."</c>. It therefore cannot drift from what is actually checked the way a
    /// composed message repeating a bound can, and both engines produce it identically because both
    /// start from the same source text.
    /// </para>
    /// <para>
    /// <b>The code does not derive from the predicate.</b> It defaults to
    /// <see cref="ValidationCodes.Predicate"/>. Message and code have opposite churn requirements:
    /// the message is human-facing and should track the rule, while the code is a wire contract, and
    /// a code slugged from the expression would make widening a bound a breaking change for every
    /// client switching on it. Name one when clients need to tell two rules apart.
    /// </para>
    /// </remarks>
    /// <param name="predicate">Returns true when the value is acceptable.</param>
    /// <param name="field">Overrides the field inferred from the predicate's first member access.</param>
    /// <param name="code">Overrides <see cref="ValidationCodes.Predicate"/>.</param>
    /// <param name="message">Overrides the rendered predicate.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="expression">Supplied by the compiler. Do not pass.</param>
    public ValidationRules<T> Ensure(
        Func<T, bool> predicate,
        string? field = null,
        string? code = null,
        string? message = null,
        ValidationSeverity severity = ValidationSeverity.Error,
        [CallerArgumentExpression(nameof(predicate))] string? expression = null) {

        ArgumentNullException.ThrowIfNull(predicate);

        var name = field ?? Named(RuleText.AnchorOfPredicate(expression), expression, "predicate");

        LastStatementStart = _rules.Count;

        Add(new PredicateRule<T>(
            name,
            predicate,
            code ?? ValidationCodes.Predicate,
            message ?? RuleText.RenderPredicate(expression, _namer.ToFieldName),
            severity));

        return this;
    }

    /// <summary>
    /// Conditions every constraint declared by the statement this terminates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scope is the statement</b>, which is already the unit a reader sees and the unit the
    /// generator's body reader walks. <c>rules.Ensure(…).When(…)</c> guards the <c>Ensure</c>;
    /// nothing reaches back past the semicolon.
    /// </para>
    /// <para>
    /// FluentValidation's <c>ApplyConditionTo</c> has no counterpart here, deliberately. That
    /// parameter exists to undo a surprising default - a chained <c>.When()</c> there applies to
    /// every validator in the chain including ones written before it, and the parameter opts out.
    /// Scoping to the statement removes the default, and with it the need to opt out of one: to
    /// guard less, write two statements.
    /// </para>
    /// </remarks>
    public ValidationRules<T> When(Func<T, bool> condition) =>
        StampFrom(LastStatementStart, condition, negated: false);

    /// <summary>The negation of <see cref="When(Func{T, bool})"/>.</summary>
    public ValidationRules<T> Unless(Func<T, bool> condition) =>
        StampFrom(LastStatementStart, condition, negated: true);

    /// <summary>
    /// Declares a group of rules that apply only when <paramref name="condition"/> holds.
    /// </summary>
    /// <param name="condition">Evaluated once per validation pass, not once per rule inside.</param>
    /// <param name="rules">
    /// Runs immediately. Everything it declares carries the condition, however deeply nested; a
    /// block inside a block conjoins rather than replaces.
    /// </param>
    /// <returns>A handle offering <c>Otherwise</c> and nothing else.</returns>
    /// <remarks>
    /// Two arguments rather than one is what separates this from
    /// <see cref="When(Func{T, bool})"/>, which terminates a statement instead of opening a block.
    /// </remarks>
    public ConditionalRules<T> When(Func<T, bool> condition, Action rules) =>
        Block(condition, rules, negated: false);

    /// <summary>The negation of <see cref="When(Func{T, bool}, Action)"/>.</summary>
    public ConditionalRules<T> Unless(Func<T, bool> condition, Action rules) =>
        Block(condition, rules, negated: true);

    /// <summary>Applies a hand-written rule, taken as a method group.</summary>
    public ValidationRules<T> Apply(RuleAction<T> rule) {
        ArgumentNullException.ThrowIfNull(rule);
        LastStatementStart = _rules.Count;
        Add(new ActionRule<T>(rule));

        return this;
    }

    /// <summary>
    /// Adds a rule, stamped with whatever conditions are open around it.
    /// </summary>
    /// <remarks>
    /// The single funnel, so that a rule declared inside a <c>When</c> block carries the block's
    /// condition however it got there - through a type-level entry point or a chained constraint on
    /// an anchored property.
    /// </remarks>
    internal void Add(ICompiledRule<T> rule) {
        rule.ConditionIndex = _open.Count == 0 ? -1 : SlotFor(_open.ToArray());
        _rules.Add(rule);
    }

    /// <summary>
    /// Where the statement currently being declared started, so <c>.When()</c> knows how far back
    /// to reach.
    /// </summary>
    internal int LastStatementStart { get; private set; }

    internal Func<T, bool>[] Atoms => _atoms.ToArray();

    internal int[][] Slots => _slots.ToArray();

    /// <summary>
    /// Conditions every rule from <paramref name="start"/> to the end of the current declaration.
    /// </summary>
    /// <remarks>
    /// Combined with whatever each rule already carries rather than replacing it, so a chained
    /// <c>.When()</c> written inside a <c>When</c> block means both.
    /// </remarks>
    internal ValidationRules<T> StampFrom(int start, Func<T, bool> condition, bool negated) {
        ArgumentNullException.ThrowIfNull(condition);

        var entry = Entry(Atom(condition), negated);

        for (var i = start; i < _rules.Count; i++) {
            _rules[i].ConditionIndex = Combine(_rules[i].ConditionIndex, entry);
        }

        return this;
    }

    /// <summary>Runs <paramref name="rules"/> with <paramref name="condition"/> open around it.</summary>
    internal ConditionalRules<T> Block(Func<T, bool> condition, Action rules, bool negated) {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(rules);

        _open.Add(Entry(Atom(condition), negated));

        try {
            // Run immediately rather than deferred: Describe is a declaration, so everything the
            // body adds belongs in this rule set now, in the position it was written.
            rules();
        } finally {
            _open.RemoveAt(_open.Count - 1);
        }

        return new ConditionalRules<T>(this, condition, negated);
    }

    /// <summary>
    /// The index of <paramref name="predicate"/> among the distinct conditions, adding it if new.
    /// </summary>
    /// <remarks>
    /// Delegate equality, so the same instance handed in twice is one atom evaluated once - which
    /// is how <c>Otherwise</c> reuses its block's condition rather than adding a second one that
    /// would evaluate the same lambda again.
    /// </remarks>
    private int Atom(Func<T, bool> predicate) {
        var existing = _atoms.IndexOf(predicate);

        if (existing >= 0) {
            return existing;
        }

        _atoms.Add(predicate);

        return _atoms.Count - 1;
    }

    private static int Entry(int atom, bool negated) => negated ? -(atom + 1) : atom + 1;

    private int Combine(int slot, int entry) {
        if (slot < 0) {
            return SlotFor(new[] { entry });
        }

        var existing = _slots[slot];

        if (Array.IndexOf(existing, entry) >= 0) {
            return slot;
        }

        var combined = new int[existing.Length + 1];
        Array.Copy(existing, combined, existing.Length);
        combined[existing.Length] = entry;

        return SlotFor(combined);
    }

    private int SlotFor(int[] entries) {
        for (var i = 0; i < _slots.Count; i++) {
            if (SameEntries(_slots[i], entries)) {
                return i;
            }
        }

        _slots.Add(entries);

        return _slots.Count - 1;
    }

    private static bool SameEntries(int[] left, int[] right) {
        if (left.Length != right.Length) {
            return false;
        }

        for (var i = 0; i < left.Length; i++) {
            if (left[i] != right[i]) {
                return false;
            }
        }

        return true;
    }

    internal IValidationFieldNamer Namer => _namer;

    internal IValidatorFor<TValue> ValidatorFor<TValue>(string field) =>
        _nested?.GetValidator<TValue>()
        ?? throw new InvalidOperationException(
            $"No validator is registered for {typeof(TValue)}, needed by the rule on '{field}' of " +
            $"{typeof(T)}. Nested rules resolve through IValidatorProvider; register one, or let the " +
            "source generator compile this rules class, where the nested validator is a static reference.");

    internal string FieldOf(string? field, string? selector) =>
        field ?? Named(RuleText.PropertyOfSelector(selector), selector, "selector");

    private string Named(string? property, string? text, string kind) =>
        property is null
            ? throw new InvalidOperationException(
                $"Could not infer a field name from the {kind} '{text}' on {typeof(T)}. It reads no " +
                "property of its parameter; pass field: explicitly.")
            : _namer.ToFieldName(property);

    /// <summary>
    /// Freezes the declarations into the order they will run in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Grouped by field, fields in first-mention order, rules within a field in declaration
    /// order.</b> Not raw body order, because §4.2 pins errors to <i>property</i> order and the
    /// generated engine walks properties - so a body that declares a rule on <c>notes</c>, then one
    /// on <c>start</c>, then a second on <c>notes</c> has to report both <c>notes</c> errors together
    /// or the two engines are not substitutable. Grouping here is what makes the generator's property
    /// walk and this loop produce the same sequence.
    /// </para>
    /// <para>
    /// Within a field, <c>Required</c> moves to the front whatever order it was written in - §4.2's
    /// one exception, and load-bearing: <see cref="Rules.FieldChainRule{T}"/> stops at the first
    /// failed <c>Required</c> in the chain, so a <c>Required</c> declared after a length check would
    /// otherwise fail to suppress it.
    /// </para>
    /// <para>
    /// <c>Apply</c> rules own no field and stay last, in declaration order (§19.7).
    /// </para>
    /// </remarks>
    internal ICompiledRule<T>[] Build() {
        var fields = new List<string>();
        var grouped = new Dictionary<string, List<ICompiledRule<T>>>(StringComparer.Ordinal);
        var applied = new List<ICompiledRule<T>>();

        foreach (var rule in _rules) {
            if (rule is ActionRule<T>) {
                applied.Add(rule);
                continue;
            }

            if (!grouped.TryGetValue(rule.Field, out var group)) {
                grouped[rule.Field] = group = new List<ICompiledRule<T>>();
                fields.Add(rule.Field);
            }

            if (rule.IsRequired) {
                group.Insert(CountOfRequired(group), rule);
            } else {
                group.Add(rule);
            }
        }

        var ordered = new List<ICompiledRule<T>>(_rules.Count);

        foreach (var field in fields) {
            var group = grouped[field];
            var required = CountOfRequired(group);

            // Only a field that can short-circuit needs the wrapper; everything else stays a plain
            // rule, so a model with no Required pays nothing for this.
            if (required > 0 && group.Count > required) {
                ordered.Add(new FieldChainRule<T>(field, group.ToArray(), required));
            } else {
                ordered.AddRange(group);
            }
        }

        ordered.AddRange(applied);

        return ordered.ToArray();
    }

    private static int CountOfRequired(List<ICompiledRule<T>> group) {
        var count = 0;

        while (count < group.Count && group[count].IsRequired) {
            count++;
        }

        return count;
    }
}
