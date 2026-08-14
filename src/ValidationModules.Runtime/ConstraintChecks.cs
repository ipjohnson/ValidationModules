using System.Collections.Generic;

namespace ValidationModules;

/// <summary>
/// The two checks that do not fit in a comparison, called from generated validators.
/// </summary>
/// <remarks>
/// <para>
/// Every other constraint compiles to a branch the emitter writes inline, because every other
/// constraint is a comparison. These two are not: <c>[UniqueItems]</c> has to look at elements
/// against each other, and <c>[MultipleOf]</c> on a floating-point member has to leave the binary
/// domain before <c>%</c> means anything. Both live here rather than being open-coded into every
/// validator, so there is one implementation to reason about and one to test.
/// </para>
/// <para>
/// Both are ordinary generic methods, instantiated by the emitter at a type it knows statically.
/// Nothing here constructs a type or looks one up.
/// </para>
/// </remarks>
public static class ConstraintChecks {

    /// <summary>
    /// Above this many elements, uniqueness allocates a set rather than comparing pairwise.
    /// </summary>
    /// <remarks>
    /// Pairwise is O(n²) and allocation-free; a set is O(n) and allocates once. At 16 elements
    /// pairwise is at most 120 comparisons, and request bodies overwhelmingly sit far below that -
    /// so the common case keeps the promise the rest of the runtime makes about a clean validation
    /// pass, and the pathological case does not degrade.
    /// </remarks>
    private const int PairwiseLimit = 16;

    /// <summary>
    /// The largest magnitude a <c>double</c> may have and still convert to <c>decimal</c>.
    /// Deliberately short of the true limit: a conversion that overflows throws, and the runtime
    /// does not throw on a validation path.
    /// </summary>
    private const double DecimalRange = 7.9e28;

    /// <summary>Whether every element differs from every other.</summary>
    /// <typeparam name="T">The element type. Compared with its default equality.</typeparam>
    /// <param name="items">The elements. Never null at the call site - the emitter guards first.</param>
    public static bool AllUnique<T>(IEnumerable<T> items) {
        // Indexable and small: no enumerator, no set, nothing on the heap.
        if (items is IReadOnlyList<T> list && list.Count <= PairwiseLimit) {
            var comparer = EqualityComparer<T>.Default;

            for (var i = 1; i < list.Count; i++) {
                for (var j = 0; j < i; j++) {
                    if (comparer.Equals(list[i], list[j])) {
                        return false;
                    }
                }
            }

            return true;
        }

        var seen = new HashSet<T>();

        foreach (var item in items) {
            if (!seen.Add(item)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a <c>double</c> is an exact multiple of a divisor, decided in the decimal domain.
    /// </summary>
    /// <remarks>
    /// <c>value % divisor</c> in binary floating point rejects 0.3, 1.05, 99.99 and 1234.56 against
    /// a divisor of 0.01 - every value a specification author would call valid. Converting to
    /// <c>decimal</c> first rounds to 15 significant digits, which cancels the representation error
    /// rather than compounding it, so <c>0.1 + 0.2</c> arrives as exactly 0.3.
    ///
    /// NaN, infinity and anything past <see cref="DecimalRange"/> are reported as failures. None can
    /// be shown to be a multiple of anything, and reporting them as passing would claim a check ran
    /// that did not.
    /// </remarks>
    public static bool IsMultipleOf(double value, decimal divisor) {
        if (double.IsNaN(value) || double.IsInfinity(value) ||
            value < -DecimalRange || value > DecimalRange) {
            return false;
        }

        return (decimal)value % divisor == 0m;
    }

    /// <summary>Whether a <c>float</c> is an exact multiple of a divisor. See the double overload.</summary>
    public static bool IsMultipleOf(float value, decimal divisor) => IsMultipleOf((double)value, divisor);
}
