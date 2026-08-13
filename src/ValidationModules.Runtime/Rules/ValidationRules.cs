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
        [CallerArgumentExpression(nameof(value))] string? selector = null) =>
        new(this, FieldOf(field, selector), value);

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
        _rules.Add(new RequiredStringRule<T>(name, value, allowEmptyStrings: false));

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
        _rules.Add(new RequiredStringRule<T>(name, value, allowEmptyStrings: true));

        return new PropertyRules<T, string?>(this, name, value);
    }

    /// <summary>Declares that a reference-typed value must be present.</summary>
    public PropertyRules<T, TValue?> Required<TValue>(
        Func<T, TValue?> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null)
        where TValue : class {

        var name = FieldOf(field, selector);
        _rules.Add(new RequiredReferenceRule<T, TValue>(name, value));

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
        _rules.Add(new RequiredNullableRule<T, TValue>(name, value));

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
        _rules.Add(new StringLengthRule<T>(name, value, min, max));

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
        _rules.Add(new RangeRule<T, TValue>(name, value, min, max));

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
        _rules.Add(new PatternRule<T>(name, value, pattern()));

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
        _rules.Add(new AllowedValuesRule<T, TValue>(name, value, allowed));

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
        _rules.Add(new ItemCountRule<T, TElement>(name, value, min, max));

        return new PropertyRules<T, IReadOnlyList<TElement>?>(this, name, value);
    }

    /// <summary>Descends into a nested object, the equivalent of <c>[ValidateNested]</c>.</summary>
    public PropertyRules<T, TValue?> Nested<TValue>(
        Func<T, TValue?> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null)
        where TValue : class {

        var name = FieldOf(field, selector);
        _rules.Add(new NestedRule<T, TValue>(name, value, ValidatorFor<TValue>(name)));

        return new PropertyRules<T, TValue?>(this, name, value);
    }

    /// <summary>Descends into each element of a collection.</summary>
    public PropertyRules<T, IReadOnlyList<TElement>?> Each<TElement>(
        Func<T, IReadOnlyList<TElement>?> value,
        string? field = null,
        [CallerArgumentExpression(nameof(value))] string? selector = null)
        where TElement : class {

        var name = FieldOf(field, selector);
        _rules.Add(new EachRule<T, TElement>(name, value, ValidatorFor<TElement>(name)));

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

        _rules.Add(new PredicateRule<T>(
            name,
            predicate,
            code ?? ValidationCodes.Predicate,
            message ?? RuleText.RenderPredicate(expression, _namer.ToFieldName),
            severity));

        return this;
    }

    /// <summary>Applies a hand-written rule, taken as a method group.</summary>
    public ValidationRules<T> Apply(RuleAction<T> rule) {
        ArgumentNullException.ThrowIfNull(rule);
        _rules.Add(new ActionRule<T>(rule));

        return this;
    }

    internal void Add(ICompiledRule<T> rule) => _rules.Add(rule);

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
    /// one exception, and load-bearing: the collector's suppression is forward-only, so a
    /// <c>Required</c> declared after a length check would otherwise fail to suppress it.
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
            ordered.AddRange(grouped[field]);
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
