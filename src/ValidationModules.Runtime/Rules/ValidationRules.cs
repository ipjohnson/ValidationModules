using System.Text.RegularExpressions;

namespace ValidationModules;

/// <summary>
/// The vocabulary an <see cref="IValidationRulesFor{T}.Describe"/> body declares rules with. Read
/// by the source generator, never run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inert by construction.</b> The constructor is internal and nothing calls it, so no instance
/// can exist and no member can execute; every body throws so a defect in that guarantee is loud
/// rather than a validator that silently checks nothing. The generator recognizes these calls as
/// syntax and expands them into check-and-report code inside the generated validator.
/// </para>
/// <para>
/// <b>Arguments are values, not selectors.</b> <c>rules.Require(x.Street)</c> - the generator
/// resolves <c>x.Street</c> as a symbol, so the field name comes from the member path
/// (<c>[JsonPropertyName]</c> first, then the field namer), nested paths and <c>?.</c> included.
/// An argument that is not a member path on the subject parameter needs <c>field:</c>, which is a
/// raw wire name on the author's head.
/// </para>
/// <para>
/// <b>Control flow is C#.</b> There is no <c>When</c>/<c>Unless</c>; write <c>if</c>/<c>else</c>.
/// Conditions evaluate where written, at validation time, inside the generated validator.
/// </para>
/// </remarks>
/// <typeparam name="T">The type being described.</typeparam>
public sealed class ValidationRules<T> {

    internal ValidationRules() {
    }

    /// <summary>
    /// The reporting surface for free-form logic - <c>rules.Context.Report(nameof(x.Sku), …)</c>.
    /// The generator rewrites this to the live validation context; the narrow interface is what
    /// keeps IntelliSense to exactly the members that work here.
    /// </summary>
    public IValidationContextReporter Context => throw Inert();

    /// <summary>
    /// Anchors to a value without declaring anything, for when the anchor reads better stated than
    /// carried by the first rule.
    /// </summary>
    public PropertyRules<T, TValue> For<TValue>(TValue value, string? field = null) => throw Inert();

    /// <summary>
    /// Declares that a string must be present. Whitespace counts as missing - plan §12 Q5;
    /// <see cref="RequireAllowingEmpty"/> is the opt-out.
    /// </summary>
    public PropertyRules<T, string?> Require(string? value, string? field = null) => throw Inert();

    /// <summary>Declares that a string must be non-null, accepting empty and whitespace-only values.</summary>
    public PropertyRules<T, string?> RequireAllowingEmpty(string? value, string? field = null) => throw Inert();

    /// <summary>Declares that a reference-typed value must be present.</summary>
    public PropertyRules<T, TValue?> Require<TValue>(TValue? value, string? field = null)
        where TValue : class => throw Inert();

    /// <summary>Declares that a nullable value type must carry a value.</summary>
    public PropertyRules<T, TValue?> Require<TValue>(TValue? value, string? field = null)
        where TValue : struct => throw Inert();

    /// <summary>
    /// The catch-all that makes <c>Require</c> on a non-nullable value type bind, so VM0090 can
    /// be the only error on the line.
    /// </summary>
    /// <remarks>
    /// A non-nullable value type fits none of the overloads above - and cannot be given one of
    /// its own, because the reference-type overload's <c>TValue?</c> is annotation-only, so a
    /// <c>TValue value</c> twin collides with it as CS0111. Without this, the call failed as a
    /// CS0452 blaming that reference overload. Typed arguments never land here: identity beats
    /// the boxing conversion everywhere an overload above applies, which is also why this is
    /// hidden from completion - it exists to be diagnosed, not called.
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public PropertyRules<T, object?> Require(object? value, string? field = null) => throw Inert();

    /// <summary>
    /// Declares a string's length bounds. A null value is <see cref="Require(string?, string?)"/>'s
    /// business.
    /// </summary>
    public PropertyRules<T, string?> Length(
        string? value, int min = 0, int max = int.MaxValue, string? field = null) => throw Inert();

    /// <summary>Declares an inclusive range.</summary>
    /// <remarks>
    /// <para>
    /// Each range method is a pair: a <c>TValue value</c> overload and a <c>TValue? value</c>
    /// overload. The pair is what makes inference read the member rather than the bound
    /// literals alone. C# infers nothing from a non-nullable argument to a <c>TValue?</c>
    /// parameter, so with only the nullable form <c>rules.Range(x.Latitude, -90, 90)</c> fixed
    /// <c>TValue</c> to <c>int</c> from the literals and failed as CS1503 blaming the value.
    /// With the pair, a non-nullable member contributes its type through this overload and the
    /// literals convert to it; a nullable member takes the nullable overload, whose
    /// nullable-to-nullable inference is exact. Resolution never ambiguates: when both bind,
    /// the identity conversion on the value beats the lifted one, and for a nullable argument
    /// this overload's inferred <c>TValue</c> is <c>Nullable&lt;T&gt;</c>, which the
    /// <c>struct</c> constraint rejects.
    /// </para>
    /// <para>
    /// Both overloads return the nullable anchor, so a chain reads the same whichever bound.
    /// </para>
    /// </remarks>
    public PropertyRules<T, TValue?> Range<TValue>(TValue value, TValue min, TValue max, string? field = null)
        where TValue : struct, IComparable<TValue>, IFormattable => throw Inert();

    /// <summary>Declares an inclusive range over a nullable member. Null passes; presence is
    /// <c>Require</c>'s question.</summary>
    public PropertyRules<T, TValue?> Range<TValue>(TValue? value, TValue min, TValue max, string? field = null)
        where TValue : struct, IComparable<TValue>, IFormattable => throw Inert();

    /// <summary>Declares an inclusive lower bound and no upper one - <c>[Range(Min = 1)]</c>.</summary>
    public PropertyRules<T, TValue?> RangeAtLeast<TValue>(TValue value, TValue min, string? field = null)
        where TValue : struct, IComparable<TValue>, IFormattable => throw Inert();

    /// <summary>The nullable-member form. See <c>Range</c> on why each range method is a pair.</summary>
    public PropertyRules<T, TValue?> RangeAtLeast<TValue>(TValue? value, TValue min, string? field = null)
        where TValue : struct, IComparable<TValue>, IFormattable => throw Inert();

    /// <summary>Declares an inclusive upper bound and no lower one - <c>[Range(Max = 99)]</c>.</summary>
    public PropertyRules<T, TValue?> RangeAtMost<TValue>(TValue value, TValue max, string? field = null)
        where TValue : struct, IComparable<TValue>, IFormattable => throw Inert();

    /// <summary>The nullable-member form. See <c>Range</c> on why each range method is a pair.</summary>
    public PropertyRules<T, TValue?> RangeAtMost<TValue>(TValue? value, TValue max, string? field = null)
        where TValue : struct, IComparable<TValue>, IFormattable => throw Inert();

    /// <summary>
    /// Declares a pattern, taken as the accessor for a <c>[GeneratedRegex]</c> partial method.
    /// </summary>
    /// <remarks>
    /// Taking the accessor rather than a pattern string is what keeps this AOT-clean without a
    /// policy: there is no inline form to diagnose, and the short spelling is the good one.
    /// </remarks>
    public PropertyRules<T, string?> Pattern(string? value, Func<Regex> pattern, string? field = null) =>
        throw Inert();

    /// <summary>
    /// Declares the permitted set - <c>rules.AllowedValues(x.Status, ["open", "closed"])</c>.
    /// </summary>
    public PropertyRules<T, TValue> AllowedValues<TValue>(TValue value, TValue[] allowed, string? field = null) =>
        throw Inert();

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
        IReadOnlyList<TElement>? value, int min = 0, int max = int.MaxValue, string? field = null) => throw Inert();

    /// <summary>Declares that the collection's elements must all differ.</summary>
    /// <remarks>
    /// <see cref="IEnumerable{T}"/> rather than the <see cref="IReadOnlyList{T}"/> that
    /// <see cref="Count{TElement}"/> takes, because uniqueness enumerates rather than reading a
    /// count - so a set-typed or enumerable-only property is declarable here where a count is not.
    /// </remarks>
    public PropertyRules<T, IEnumerable<TElement>?> Unique<TElement>(
        IEnumerable<TElement>? value, string? field = null) => throw Inert();

    /// <summary>Declares that an integral value must be an exact multiple of a divisor.</summary>
    /// <remarks>
    /// Three overloads rather than one generic method, because the divisor's own type is what
    /// resolves them: <c>MultipleOf(x.Quantity, 5)</c> picks this one, <c>0.05m</c> picks the
    /// decimal one and <c>0.01</c> the double one.
    /// </remarks>
    public PropertyRules<T, long?> MultipleOf(long? value, long divisor, string? field = null) => throw Inert();

    /// <summary>Declares that a decimal value must be an exact multiple of a divisor.</summary>
    public PropertyRules<T, decimal?> MultipleOf(decimal? value, decimal divisor, string? field = null) =>
        throw Inert();

    /// <summary>
    /// Declares that a floating-point value must be a multiple of a divisor, decided in the decimal
    /// domain - see <see cref="ConstraintChecks.IsMultipleOf(double, decimal)"/>.
    /// </summary>
    public PropertyRules<T, double?> MultipleOf(double? value, double divisor, string? field = null) =>
        throw Inert();

    /// <summary>Descends into a nested object, the equivalent of <c>[ValidateNested]</c>.</summary>
    public PropertyRules<T, TValue?> Nested<TValue>(TValue? value, string? field = null)
        where TValue : class => throw Inert();

    /// <summary>Descends into each element of a collection.</summary>
    public PropertyRules<T, IReadOnlyList<TElement>?> Each<TElement>(
        IReadOnlyList<TElement>? value, string? field = null)
        where TElement : class => throw Inert();

    /// <summary>
    /// Anchors each string element of a collection, so the rules that follow apply per element
    /// with indexed paths - <c>rules.Each(x.Steps).Length(5, 500)</c> reports at <c>steps[0]</c>.
    /// </summary>
    /// <remarks>
    /// A collection of objects descends into the element type's generated validator; a string has
    /// none, so its elements take their rules inline. Null elements are skipped, exactly as a
    /// null element of a nested collection is.
    /// </remarks>
    public PropertyRules<T, string?> Each(IReadOnlyList<string>? value, string? field = null) =>
        throw Inert();

    /// <summary>
    /// Declares a rule the vocabulary cannot say: a cross-field comparison, arithmetic over locals,
    /// anything with no schema meaning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The message is the condition, rendered</b> - parameter stripped, member accesses
    /// wire-named, whitespace normalized, a period appended. A local appears under its own name
    /// (<c>total &lt;= creditLimit.</c>): local naming is user-facing text here, which is a feature.
    /// The text is compile-time source, so no runtime value can reach it.
    /// </para>
    /// <para>
    /// <b>The code does not derive from the condition.</b> It defaults to
    /// <see cref="ValidationCodes.Predicate"/>: the message is human-facing and should track the
    /// rule, while the code is a wire contract, and a code slugged from the expression would make
    /// widening a bound a breaking change for every client switching on it.
    /// </para>
    /// <para>
    /// The anchor is the first member access off the subject parameter; no anchor and no
    /// <c>field:</c> is a build error.
    /// </para>
    /// </remarks>
    public ValidationRules<T> Ensure(
        bool condition,
        string? field = null,
        string? code = null,
        string? message = null,
        ValidationSeverity severity = ValidationSeverity.Error) => throw Inert();

    /// <summary>
    /// Validates the subject as one of its facets - an interface or base type whose rules are
    /// declared where the facet is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The argument must be the subject parameter: a facet of a <i>child</i> is
    /// <see cref="Nested{TValue}"/>'s territory, where the path pushes. Here the path does not
    /// push - facet fields report at the current level - and suppression shares the collector as
    /// everywhere.
    /// </para>
    /// <para>
    /// One spelling, two bindings. A facet whose validator is generated in this compilation binds
    /// statically; a facet from a referenced assembly resolves
    /// <c>IValidatorFor&lt;TFacet&gt;</c> through the pass's services - closed at build time, no
    /// scanning - and a missing registration throws naming the module to compose. Never a silent
    /// skip.
    /// </para>
    /// </remarks>
    public ValidationRules<T> As<TFacet>(TFacet value) => throw Inert();

    /// <summary>
    /// Applies a hand-written rule, taken as a method group. Emitted as a direct call, ordered
    /// after everything else; the rule owns what it records.
    /// </summary>
    public ValidationRules<T> Apply(RuleAction<T> rule) => throw Inert();

    /// <summary>
    /// The surface is read, never run; reaching any member means that guarantee broke somewhere.
    /// </summary>
    internal static NotSupportedException Inert() => new(
        $"ValidationRules<{typeof(T).Name}> is read by the ValidationModules source generator and " +
        "never executed - nothing constructs the builder and nothing calls Describe. The generated " +
        "validator contains the transcribed checks. If this threw, a Describe body was invoked at " +
        "runtime, which nothing should do.");
}
