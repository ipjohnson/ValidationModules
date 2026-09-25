using System.Numerics;
using System.Text.RegularExpressions;
using ValidationModules.Constraints;

namespace ValidationModules;

/// <summary>
/// A value already anchored by an earlier rule in a chain, so the rules that follow it need not
/// repeat it - <c>rules.Require(x.Name).Length(1, 100)</c>. Read by the source generator, never run.
/// </summary>
/// <remarks>
/// <para>
/// Inert like <see cref="ValidationRules{T}"/>: no instance can exist, so a chain is syntax for the
/// generator to read. A chain is one statement and emits as a single <c>if</c>/<c>else if</c>
/// ladder, which is what makes a failed <c>Require</c> suppress the checks chained after it without
/// any runtime bookkeeping.
/// </para>
/// <para>
/// The constraints themselves are extension methods, because which ones are legal depends on
/// <typeparamref name="TValue"/> and an instance method cannot be constrained on its own declaring
/// type's argument. That is how <c>Length</c> is offered on a string and not on an <c>int</c>, at
/// compile time rather than as a runtime check.
/// </para>
/// </remarks>
/// <typeparam name="T">The type being described.</typeparam>
/// <typeparam name="TValue">The anchored value's type.</typeparam>
public sealed class PropertyRules<T, TValue>
{
    internal PropertyRules() { }
}

/// <summary>
/// The constraints available on an anchored value, split by what its type admits.
/// </summary>
public static class PropertyRulesExtensions
{
    /// <summary>Declares that the anchored string must be present. Whitespace counts as missing.</summary>
    public static PropertyRules<T, string?> Require<T>(this PropertyRules<T, string?> rules) =>
        throw ValidationRules<T>.Inert();

    /// <summary>
    /// Declares that the anchored string must be non-null, accepting empty and whitespace-only.
    /// </summary>
    public static PropertyRules<T, string?> RequireAllowingEmpty<T>(
        this PropertyRules<T, string?> rules
    ) => throw ValidationRules<T>.Inert();

    /// <summary>Declares that the anchored reference-typed value must be present.</summary>
    public static PropertyRules<T, TValue?> Require<T, TValue>(this PropertyRules<T, TValue?> rules)
        where TValue : class => throw ValidationRules<T>.Inert();

    /// <summary>Declares that the anchored nullable value type must carry a value.</summary>
    public static PropertyRules<T, TValue?> Require<T, TValue>(this PropertyRules<T, TValue?> rules)
        where TValue : struct => throw ValidationRules<T>.Inert();

    /// <summary>Declares the anchored string's length bounds.</summary>
    public static PropertyRules<T, string?> Length<T>(
        this PropertyRules<T, string?> rules,
        int min = 0,
        int max = int.MaxValue
    ) => throw ValidationRules<T>.Inert();

    /// <summary>Declares the anchored string's pattern, as a <c>[GeneratedRegex]</c> accessor.</summary>
    public static PropertyRules<T, string?> Pattern<T>(
        this PropertyRules<T, string?> rules,
        Func<Regex> pattern
    ) => throw ValidationRules<T>.Inert();

    /// <summary>Declares the anchored value's inclusive range.</summary>
    public static PropertyRules<T, TValue?> Range<T, TValue>(
        this PropertyRules<T, TValue?> rules,
        TValue min,
        TValue max
    )
        where TValue : struct, IComparable<TValue>, IFormattable =>
        throw ValidationRules<T>.Inert();

    /// <summary>
    /// The non-nullable form, for the chain <c>For</c> starts on a non-nullable value.
    /// </summary>
    /// <remarks>
    /// <c>For</c> anchors a chain on the value's own type. It cannot anchor a non-nullable value
    /// type on its nullable form instead, as the value-type rule methods do, because a
    /// <c>struct</c>-constrained twin of <c>For(TValue value)</c> collides with it as CS0111, and
    /// the unconstrained <c>For</c> is the one a type parameter in a generic fragment binds. So
    /// each value-type chain method is a pair instead. For a nullable receiver this overload infers
    /// <c>Nullable&lt;T&gt;</c>, which the <c>struct</c> constraint rejects, so the pair never
    /// ambiguates.
    /// </remarks>
    public static PropertyRules<T, TValue> Range<T, TValue>(
        this PropertyRules<T, TValue> rules,
        TValue min,
        TValue max
    )
        where TValue : struct, IComparable<TValue>, IFormattable =>
        throw ValidationRules<T>.Inert();

    /// <summary>Declares the anchored value's lower bound, with no upper one.</summary>
    public static PropertyRules<T, TValue?> RangeAtLeast<T, TValue>(
        this PropertyRules<T, TValue?> rules,
        TValue min
    )
        where TValue : struct, IComparable<TValue>, IFormattable =>
        throw ValidationRules<T>.Inert();

    /// <summary>
    /// The non-nullable form. See <c>Range</c> on why each value-type chain method is a pair.
    /// </summary>
    public static PropertyRules<T, TValue> RangeAtLeast<T, TValue>(
        this PropertyRules<T, TValue> rules,
        TValue min
    )
        where TValue : struct, IComparable<TValue>, IFormattable =>
        throw ValidationRules<T>.Inert();

    /// <summary>Declares the anchored value's upper bound, with no lower one.</summary>
    public static PropertyRules<T, TValue?> RangeAtMost<T, TValue>(
        this PropertyRules<T, TValue?> rules,
        TValue max
    )
        where TValue : struct, IComparable<TValue>, IFormattable =>
        throw ValidationRules<T>.Inert();

    /// <summary>
    /// The non-nullable form. See <c>Range</c> on why each value-type chain method is a pair.
    /// </summary>
    public static PropertyRules<T, TValue> RangeAtMost<T, TValue>(
        this PropertyRules<T, TValue> rules,
        TValue max
    )
        where TValue : struct, IComparable<TValue>, IFormattable =>
        throw ValidationRules<T>.Inert();

    /// <summary>Declares the anchored value's permitted set.</summary>
    public static PropertyRules<T, TValue> AllowedValues<T, TValue>(
        this PropertyRules<T, TValue> rules,
        params TValue[] allowed
    ) => throw ValidationRules<T>.Inert();

    /// <summary>Declares the anchored collection's element-count bounds.</summary>
    public static PropertyRules<T, IReadOnlyList<TElement>?> Count<T, TElement>(
        this PropertyRules<T, IReadOnlyList<TElement>?> rules,
        int min = 0,
        int max = int.MaxValue
    ) => throw ValidationRules<T>.Inert();

    /// <summary>Declares that the anchored collection's elements must all differ.</summary>
    public static PropertyRules<T, IEnumerable<TElement>?> Unique<T, TElement>(
        this PropertyRules<T, IEnumerable<TElement>?> rules
    ) => throw ValidationRules<T>.Inert();

    /// <summary>Declares that the anchored integral value must be an exact multiple of a divisor.</summary>
    public static PropertyRules<T, long?> MultipleOf<T>(
        this PropertyRules<T, long?> rules,
        long divisor
    ) => throw ValidationRules<T>.Inert();

    /// <summary>Declares that the anchored decimal value must be an exact multiple of a divisor.</summary>
    public static PropertyRules<T, decimal?> MultipleOf<T>(
        this PropertyRules<T, decimal?> rules,
        decimal divisor
    ) => throw ValidationRules<T>.Inert();

    /// <summary>Declares that the anchored floating-point value must be a multiple of a divisor.</summary>
    public static PropertyRules<T, double?> MultipleOf<T>(
        this PropertyRules<T, double?> rules,
        double divisor
    ) => throw ValidationRules<T>.Inert();

    /// <summary>
    /// Declares that the anchored number must be a multiple of a divisor, for the nullable numeric
    /// types the three overloads above do not name, such as the <c>int?</c> a <c>Range</c> on an
    /// <c>int</c> anchors.
    /// </summary>
    /// <remarks>
    /// On a <c>long?</c>, <c>decimal?</c> or <c>double?</c> chain the overload that names the type
    /// wins, because its parameter types are more specific.
    /// </remarks>
    public static PropertyRules<T, TValue?> MultipleOf<T, TValue>(
        this PropertyRules<T, TValue?> rules,
        TValue divisor
    )
        where TValue : struct, INumber<TValue> => throw ValidationRules<T>.Inert();

    /// <summary>
    /// The non-nullable form, for the chain <c>For</c> starts on a non-nullable number. See
    /// <c>Range</c> on why each value-type chain method is a pair.
    /// </summary>
    public static PropertyRules<T, TValue> MultipleOf<T, TValue>(
        this PropertyRules<T, TValue> rules,
        TValue divisor
    )
        where TValue : struct, INumber<TValue> => throw ValidationRules<T>.Inert();

    /// <summary>Descends into each element of the anchored collection.</summary>
    public static PropertyRules<T, IReadOnlyList<TElement>?> Each<T, TElement>(
        this PropertyRules<T, IReadOnlyList<TElement>?> rules
    )
        where TElement : class => throw ValidationRules<T>.Inert();

    /// <summary>
    /// Descends into each element of the anchored collection and chooses each element's validators
    /// by its type.
    /// </summary>
    public static PropertyRules<T, IReadOnlyList<TElement>?> Each<T, TElement>(
        this PropertyRules<T, IReadOnlyList<TElement>?> rules,
        Polymorphism polymorphism
    )
        where TElement : class => throw ValidationRules<T>.Inert();

    /// <summary>
    /// Anchors each string element of the anchored collection, so the rules that follow apply per
    /// element with indexed paths - <c>rules.Count(x.Steps, 1, 30).Each().Length(5, 500)</c>.
    /// </summary>
    public static PropertyRules<T, string?> Each<T>(
        this PropertyRules<T, IReadOnlyList<string>?> rules
    ) => throw ValidationRules<T>.Inert();

    /// <summary>Descends into the anchored object.</summary>
    public static PropertyRules<T, TValue?> Nested<T, TValue>(this PropertyRules<T, TValue?> rules)
        where TValue : class => throw ValidationRules<T>.Inert();

    /// <summary>
    /// Descends into the anchored object and chooses its validators by the value's type.
    /// </summary>
    public static PropertyRules<T, TValue?> Nested<T, TValue>(
        this PropertyRules<T, TValue?> rules,
        Polymorphism polymorphism
    )
        where TValue : class => throw ValidationRules<T>.Inert();
}
