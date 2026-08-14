using System.Text.RegularExpressions;
using ValidationModules.Rules;

namespace ValidationModules;

/// <summary>
/// A property already anchored by an earlier rule, so the rules that follow it need no selector.
/// </summary>
/// <remarks>
/// This is what makes <c>rules.Required(x =&gt; x.Name).Length(1, 100)</c> the spelling rather than
/// repeating the selector or opening with a <c>For</c>. It carries the field name, the accessor and
/// the owner; the constraints themselves are extension methods on it, because which ones are legal
/// depends on <typeparamref name="TValue"/> and an instance method cannot be constrained on its own
/// declaring type's argument. That is how <c>Length</c> is offered on a string and not on an
/// <c>int</c>, at compile time rather than as a runtime check.
/// </remarks>
/// <typeparam name="T">The type being described.</typeparam>
/// <typeparam name="TValue">The anchored property's type.</typeparam>
public sealed class PropertyRules<T, TValue> {

    internal PropertyRules(ValidationRules<T> owner, string field, Func<T, TValue> read) {
        Owner = owner;
        Field = field;
        Read = read;
    }

    /// <summary>Returns to the type-level builder, for a rule that is not about this property.</summary>
    public ValidationRules<T> And => Owner;

    internal ValidationRules<T> Owner { get; }

    internal string Field { get; }

    internal Func<T, TValue> Read { get; }
}

/// <summary>
/// The constraints available on an anchored property, split by what its type admits.
/// </summary>
/// <remarks>
/// Extensions rather than instance members for the reason in <see cref="PropertyRules{T, TValue}"/>,
/// and the same split <see cref="ValidatorForExtensions"/> and
/// <see cref="ValidationContextExtensions"/> already use: the type stays small and the ergonomics
/// live outside it.
/// </remarks>
public static class PropertyRulesExtensions {

    /// <summary>Declares that the anchored string must be present. Whitespace counts as missing.</summary>
    public static PropertyRules<T, string?> Required<T>(this PropertyRules<T, string?> rules) {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new RequiredStringRule<T>(rules.Field, rules.Read, allowEmptyStrings: false));

        return rules;
    }

    /// <summary>
    /// Declares that the anchored string must be non-null, accepting empty and whitespace-only.
    /// </summary>
    /// <remarks>
    /// A distinct name rather than a flag, for the reason on
    /// <see cref="ValidationRules{T}.Required(Func{T, string}, string, string)"/>: the two overloads
    /// have to keep matching parameter lists or a string anchor cannot pick between this and the
    /// reference-typed form.
    /// </remarks>
    public static PropertyRules<T, string?> RequiredAllowingEmpty<T>(this PropertyRules<T, string?> rules) {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new RequiredStringRule<T>(rules.Field, rules.Read, allowEmptyStrings: true));

        return rules;
    }

    /// <summary>Declares that the anchored reference-typed value must be present.</summary>
    public static PropertyRules<T, TValue?> Required<T, TValue>(this PropertyRules<T, TValue?> rules)
        where TValue : class {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new RequiredReferenceRule<T, TValue>(rules.Field, rules.Read));

        return rules;
    }

    /// <summary>Declares that the anchored nullable value type must carry a value.</summary>
    public static PropertyRules<T, TValue?> Required<T, TValue>(this PropertyRules<T, TValue?> rules)
        where TValue : struct {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new RequiredNullableRule<T, TValue>(rules.Field, rules.Read));

        return rules;
    }

    /// <summary>Declares the anchored string's length bounds.</summary>
    public static PropertyRules<T, string?> Length<T>(
        this PropertyRules<T, string?> rules, int min = 0, int max = int.MaxValue) {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new StringLengthRule<T>(rules.Field, rules.Read, min, max));

        return rules;
    }

    /// <summary>Declares the anchored string's pattern, as a <c>[GeneratedRegex]</c> accessor.</summary>
    public static PropertyRules<T, string?> Pattern<T>(
        this PropertyRules<T, string?> rules, Func<Regex> pattern) {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(pattern);
        rules.Owner.Add(new PatternRule<T>(rules.Field, rules.Read, pattern()));

        return rules;
    }

    /// <summary>Declares the anchored value's inclusive range.</summary>
    public static PropertyRules<T, TValue?> Range<T, TValue>(
        this PropertyRules<T, TValue?> rules, TValue min, TValue max)
        where TValue : struct, IComparable<TValue>, IFormattable {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new RangeRule<T, TValue>(rules.Field, rules.Read, min, max));

        return rules;
    }

    /// <summary>Declares the anchored value's lower bound, with no upper one.</summary>
    public static PropertyRules<T, TValue?> RangeAtLeast<T, TValue>(
        this PropertyRules<T, TValue?> rules, TValue min)
        where TValue : struct, IComparable<TValue>, IFormattable {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new RangeRule<T, TValue>(rules.Field, rules.Read, min, null));

        return rules;
    }

    /// <summary>Declares the anchored value's upper bound, with no lower one.</summary>
    public static PropertyRules<T, TValue?> RangeAtMost<T, TValue>(
        this PropertyRules<T, TValue?> rules, TValue max)
        where TValue : struct, IComparable<TValue>, IFormattable {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new RangeRule<T, TValue>(rules.Field, rules.Read, null, max));

        return rules;
    }

    /// <summary>Declares the anchored value's permitted set.</summary>
    public static PropertyRules<T, TValue> AllowedValues<T, TValue>(
        this PropertyRules<T, TValue> rules, params TValue[] allowed) {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(allowed);
        rules.Owner.Add(new AllowedValuesRule<T, TValue>(rules.Field, rules.Read, allowed));

        return rules;
    }

    /// <summary>Declares the anchored collection's element-count bounds.</summary>
    public static PropertyRules<T, IReadOnlyList<TElement>?> Count<T, TElement>(
        this PropertyRules<T, IReadOnlyList<TElement>?> rules, int min = 0, int max = int.MaxValue) {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new ItemCountRule<T, TElement>(rules.Field, rules.Read, min, max));

        return rules;
    }

    /// <summary>Declares that the anchored collection's elements must all differ.</summary>
    public static PropertyRules<T, IEnumerable<TElement>?> Unique<T, TElement>(
        this PropertyRules<T, IEnumerable<TElement>?> rules) {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new UniqueItemsRule<T, TElement>(rules.Field, rules.Read));

        return rules;
    }

    /// <summary>Declares that the anchored integral value must be an exact multiple of a divisor.</summary>
    public static PropertyRules<T, long?> MultipleOf<T>(this PropertyRules<T, long?> rules, long divisor) {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new MultipleOfRule<T>(rules.Field, target => rules.Read(target), divisor));

        return rules;
    }

    /// <summary>Declares that the anchored decimal value must be an exact multiple of a divisor.</summary>
    public static PropertyRules<T, decimal?> MultipleOf<T>(this PropertyRules<T, decimal?> rules, decimal divisor) {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new MultipleOfRule<T>(rules.Field, rules.Read, divisor));

        return rules;
    }

    /// <summary>Declares that the anchored floating-point value must be a multiple of a divisor.</summary>
    public static PropertyRules<T, double?> MultipleOf<T>(this PropertyRules<T, double?> rules, double divisor) {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new MultipleOfApproximateRule<T>(rules.Field, rules.Read, (decimal)divisor));

        return rules;
    }

    /// <summary>Descends into each element of the anchored collection.</summary>
    public static PropertyRules<T, IReadOnlyList<TElement>?> Each<T, TElement>(
        this PropertyRules<T, IReadOnlyList<TElement>?> rules)
        where TElement : class {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new EachRule<T, TElement>(
            rules.Field, rules.Read, rules.Owner.ValidatorFor<TElement>(rules.Field)));

        return rules;
    }

    /// <summary>Descends into the anchored object.</summary>
    public static PropertyRules<T, TValue?> Nested<T, TValue>(this PropertyRules<T, TValue?> rules)
        where TValue : class {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Owner.Add(new NestedRule<T, TValue>(
            rules.Field, rules.Read, rules.Owner.ValidatorFor<TValue>(rules.Field)));

        return rules;
    }
}
