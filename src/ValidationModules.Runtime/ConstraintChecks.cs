using System.Buffers.Text;
using System.Collections.Generic;

namespace ValidationModules;

/// <summary>
/// The checks that do not fit in a comparison, called from generated validators.
/// </summary>
/// <remarks>
/// <para>
/// Most constraints compile to a branch the emitter writes inline, because most constraints are
/// comparisons. The ones here are not: <c>[UniqueItems]</c> has to look at elements against each
/// other, <c>[MultipleOf]</c> on a floating-point member has to leave the binary domain before
/// <c>%</c> means anything, and the DataAnnotations format validators each walk the value. All
/// live here rather than being open-coded into every validator, so there is one implementation to
/// reason about and one to test.
/// </para>
/// <para>
/// The <c>Is*</c> format checks reproduce <c>System.ComponentModel.DataAnnotations</c> exactly -
/// each summary states the semantics, which are the BCL's own and are by design
/// (dotnet/runtime#45670: "the current implementation is by design and not something we plan to
/// change"). Reproduced rather than called so that a validation pass constructs no attribute,
/// touches no <c>ValidationContext</c>, and allocates nothing; parity is pinned by
/// ConstraintChecksTests running the same inputs through the real attributes.
/// </para>
/// <para>
/// Everything here is an ordinary method, instantiated by the emitter at a type it knows
/// statically. Nothing constructs a type or looks one up.
/// </para>
/// </remarks>
public static class ConstraintChecks {

    private const string PhoneCharacters = "-.()";
    private const string PhoneExtensionExtDot = "ext.";
    private const string PhoneExtensionExt = "ext";
    private const string PhoneExtensionX = "x";

    /// <summary>
    /// Above this length, the phone check heap-allocates its scratch copy rather than growing the
    /// stack. Real phone numbers sit far below it, so the common case allocates nothing.
    /// </summary>
    private const int PhoneStackLimit = 128;

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
    /// <summary>
    /// Whether a <see cref="float"/> is a whole multiple of <paramref name="divisor"/>.
    /// </summary>
    /// <remarks>
    /// Converts straight to <see cref="decimal"/> rather than widening through
    /// <see cref="double"/> first. Widening is lossless in the sense that matters to a double, and
    /// exactly wrong here: it exposes the float's own representation error at double resolution,
    /// and the conversion to decimal then keeps those digits. 0.3f became 0.300000011920929 and
    /// stopped being a multiple of 0.1, while the same value as a double passed. float to decimal
    /// rounds to the seven significant digits a float actually carries, which is the precision the
    /// caller wrote the constraint against.
    /// </remarks>
    public static bool IsMultipleOf(float value, decimal divisor) {
        if (float.IsNaN(value) || float.IsInfinity(value) ||
            value < -DecimalRange || value > DecimalRange) {
            return false;
        }

        return (decimal)value % divisor == 0m;
    }

    /// <summary>
    /// <c>[EmailAddress]</c>: exactly one <c>'@'</c>, neither first nor last, and no line breaks.
    /// <c>"a@b"</c> passes.
    /// </summary>
    /// <remarks>
    /// RFC 5322's addr-spec permits a dotless domain - <c>root@localhost</c> is a valid address -
    /// so the check is an approximation of a deliberately permissive grammar, not a loose stand-in
    /// for a strict one. The line-break rejection is the BCL's own hardening and is kept.
    /// </remarks>
    public static bool IsEmail(string value) {
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0) {
            return false;
        }

        var index = value.IndexOf('@');

        return index > 0 &&
            index != value.Length - 1 &&
            index == value.LastIndexOf('@');
    }

    /// <summary>
    /// <c>[Phone]</c>: after stripping every <c>'+'</c>, trailing whitespace, and a trailing
    /// extension (<c>ext.</c>, <c>ext</c> or <c>x</c> followed by digits), the value must contain
    /// at least one digit and nothing but digits, whitespace and <c>- . ( )</c>.
    /// </summary>
    /// <remarks>
    /// The BCL strips <c>'+'</c> with <c>string.Replace</c>, which allocates on every call; this
    /// copies into stack scratch instead, above <see cref="PhoneStackLimit"/> falling back to the
    /// allocation DataAnnotations always makes. Same answer either way, including the quirk that
    /// a <c>'+'</c> inside <c>e+xt.</c> still reads as an extension marker, because the strip
    /// happens before the extension search there too.
    /// </remarks>
    public static bool IsPhone(string value) {
        Span<char> scratch = value.Length <= PhoneStackLimit
            ? stackalloc char[PhoneStackLimit]
            : new char[value.Length];
        var length = 0;

        foreach (var c in value) {
            if (c != '+') {
                scratch[length++] = c;
            }
        }

        var span = ((ReadOnlySpan<char>)scratch.Slice(0, length)).TrimEnd();

        span = RemovePhoneExtension(span);

        var digitFound = false;

        foreach (var c in span) {
            if (char.IsDigit(c)) {
                digitFound = true;
                break;
            }
        }

        if (!digitFound) {
            return false;
        }

        foreach (var c in span) {
            if (!(char.IsDigit(c) || char.IsWhiteSpace(c) || PhoneCharacters.Contains(c))) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>[Url]</c> on a string: the value must start with <c>http://</c>, <c>https://</c> or
    /// <c>ftp://</c>, case-insensitively. Nothing past the prefix is checked.
    /// </summary>
    public static bool IsUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>[Url]</c> on a <see cref="Uri"/>: absolute, with scheme http, https or ftp.
    /// </summary>
    /// <remarks>
    /// The <see cref="Uri"/> branch entered DataAnnotations after .NET 8, whose
    /// <c>UrlAttribute</c> rejects every non-string value. One semantics is emitted for both
    /// target frameworks, and it is the current one - the direction the BCL moved, and the only
    /// answer that does not fail a member for being better-typed.
    /// </remarks>
    public static bool IsUrl(Uri value) =>
        value.IsAbsoluteUri &&
        (value.Scheme == Uri.UriSchemeHttp ||
            value.Scheme == Uri.UriSchemeHttps ||
            value.Scheme == Uri.UriSchemeFtp);

    /// <summary>
    /// <c>[CreditCard]</c>: digits, with dashes and spaces skipped, passing the Luhn mod-10
    /// checksum.
    /// </summary>
    public static bool IsCreditCard(string value) {
        var checksum = 0;
        var evenDigit = false;

        for (var i = value.Length - 1; i >= 0; i--) {
            var digit = value[i];

            if (!char.IsAsciiDigit(digit)) {
                if (digit is '-' or ' ') {
                    continue;
                }

                return false;
            }

            var digitValue = (digit - '0') * (evenDigit ? 2 : 1);

            evenDigit = !evenDigit;

            while (digitValue > 0) {
                checksum += digitValue % 10;
                digitValue /= 10;
            }
        }

        return checksum % 10 == 0;
    }

    /// <summary>
    /// <c>[Base64String]</c>: whatever <see cref="Base64.IsValid(ReadOnlySpan{char})"/> accepts -
    /// well-formed Base64 as <c>Convert.FromBase64String</c> reads it, whitespace included.
    /// </summary>
    /// <remarks>
    /// A pass-through, kept so the six format checks live and are pinned in one place.
    /// </remarks>
    public static bool IsBase64(string value) => Base64.IsValid(value);

    /// <summary>
    /// <c>[FileExtensions]</c>: the file name's extension is one of the permitted set.
    /// </summary>
    /// <param name="value">The file name.</param>
    /// <param name="extensions">
    /// The permitted extensions, dot-prefixed and lowercased. Normalized at build time by the
    /// generator exactly as the attribute normalizes its <c>Extensions</c> property - spaces and
    /// dots removed, lowercased invariantly, split on commas - so the quirks survive: an entry of
    /// <c>tar.gz</c> becomes <c>.targz</c> there and becomes it here.
    /// </param>
    /// <remarks>
    /// The BCL lowercases the file's extension with <c>ToLowerInvariant</c> and compares
    /// ordinally, allocating the lowered copy; comparing case-insensitively against the
    /// already-lowered set gives the same answer for any extension representable in the attribute
    /// and allocates nothing.
    /// </remarks>
    public static bool HasFileExtension(string value, string[] extensions) {
        var extension = System.IO.Path.GetExtension(value.AsSpan());

        foreach (var candidate in extensions) {
            if (extension.Equals(candidate.AsSpan(), StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The trailing-extension strip, ported from <c>PhoneAttribute</c> verbatim: the last
    /// occurrence of each marker in turn, kept only when nothing but digits follows it.
    /// </summary>
    private static ReadOnlySpan<char> RemovePhoneExtension(ReadOnlySpan<char> potentialPhoneNumber) {
        var lastIndexOfExtension = potentialPhoneNumber
            .LastIndexOf(PhoneExtensionExtDot.AsSpan(), StringComparison.OrdinalIgnoreCase);

        if (lastIndexOfExtension >= 0) {
            var extension = potentialPhoneNumber.Slice(lastIndexOfExtension + PhoneExtensionExtDot.Length);

            if (MatchesPhoneExtension(extension)) {
                return potentialPhoneNumber.Slice(0, lastIndexOfExtension);
            }
        }

        lastIndexOfExtension = potentialPhoneNumber
            .LastIndexOf(PhoneExtensionExt.AsSpan(), StringComparison.OrdinalIgnoreCase);

        if (lastIndexOfExtension >= 0) {
            var extension = potentialPhoneNumber.Slice(lastIndexOfExtension + PhoneExtensionExt.Length);

            if (MatchesPhoneExtension(extension)) {
                return potentialPhoneNumber.Slice(0, lastIndexOfExtension);
            }
        }

        lastIndexOfExtension = potentialPhoneNumber
            .LastIndexOf(PhoneExtensionX.AsSpan(), StringComparison.OrdinalIgnoreCase);

        if (lastIndexOfExtension >= 0) {
            var extension = potentialPhoneNumber.Slice(lastIndexOfExtension + PhoneExtensionX.Length);

            if (MatchesPhoneExtension(extension)) {
                return potentialPhoneNumber.Slice(0, lastIndexOfExtension);
            }
        }

        return potentialPhoneNumber;
    }

    private static bool MatchesPhoneExtension(ReadOnlySpan<char> potentialExtension) {
        potentialExtension = potentialExtension.TrimStart();

        if (potentialExtension.Length == 0) {
            return false;
        }

        foreach (var c in potentialExtension) {
            if (!char.IsDigit(c)) {
                return false;
            }
        }

        return true;
    }
}
