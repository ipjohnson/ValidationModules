using System.ComponentModel.DataAnnotations;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The checks generated validators call instead of writing a comparison.
/// </summary>
/// <remarks>
/// <para>
/// All are reached from emitted code rather than from a consumer's own source, so nothing else
/// would catch a change to them. The floating-point cases in particular are the whole argument for
/// <c>[MultipleOf]</c> accepting <c>double</c> at all, and are pinned here as evidence rather than
/// as a description.
/// </para>
/// <para>
/// The format checks claim bug-for-bug parity with <c>System.ComponentModel.DataAnnotations</c>,
/// so each is run against the real attribute over a canon chosen to reach every branch - the claim
/// is tested, not asserted. Null is outside the canon deliberately: the attributes pass null, and
/// the emitter expresses that as the null guard around the call, so the methods never see one.
/// </para>
/// </remarks>
public class ConstraintChecksTests {

    /// <summary>
    /// Every one of these fails a naive <c>value % 0.01</c> in binary floating point - 0.3 % 0.01 is
    /// 0.00999999999999998 - and every one is a value a specification author would call valid. This
    /// is why the check converts to decimal first.
    /// </summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(1.05)]
    [InlineData(2.10)]
    [InlineData(0.07)]
    [InlineData(99.99)]
    [InlineData(1234.56)]
    [InlineData(0.35)]
    public void IsMultipleOf_AcceptsValuesTheNaiveModuloRejects(double value) {
        Assert.False(value % 0.01 == 0, "the premise: this value fails a binary-domain check");
        Assert.True(ConstraintChecks.IsMultipleOf(value, 0.01m));
    }

    /// <summary>
    /// The double-to-decimal conversion rounds to 15 significant digits, so accumulated
    /// representation error cancels rather than compounding.
    /// </summary>
    [Fact]
    public void IsMultipleOf_CancelsAccumulatedError() {
        Assert.True(ConstraintChecks.IsMultipleOf(0.1 + 0.2, 0.1m));
    }

    [Theory]
    [InlineData(0.125, 0.01)]
    [InlineData(1.005, 0.01)]
    [InlineData(7.0, 5.0)]
    public void IsMultipleOf_RejectsWhatIsNotAMultiple(double value, double divisor) {
        Assert.False(ConstraintChecks.IsMultipleOf(value, (decimal)divisor));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(15.0)]
    public void IsMultipleOf_AcceptsZeroAndNegatives(double value) {
        Assert.True(ConstraintChecks.IsMultipleOf(value, 5m));
    }

    /// <summary>
    /// None of these can be shown to be a multiple of anything. Reported as failures rather than
    /// passes, because passing would claim a check ran that did not - and a conversion that
    /// overflows would throw, which this runtime does not do on a validation path.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e30)]
    [InlineData(-1e30)]
    public void IsMultipleOf_RejectsWhatItCannotEvaluate(double value) {
        Assert.False(ConstraintChecks.IsMultipleOf(value, 0.01m));
    }

    [Fact]
    public void IsMultipleOf_HandlesFloatThroughTheSamePath() {
        Assert.True(ConstraintChecks.IsMultipleOf(0.25f, 0.05m));
        Assert.False(ConstraintChecks.IsMultipleOf(0.26f, 0.05m));
    }

    [Fact]
    public void AllUnique_IsTrueForDistinctElements() {
        Assert.True(ConstraintChecks.AllUnique(new[] { "a", "b", "c" }));
    }

    [Fact]
    public void AllUnique_IsFalseForARepeat() {
        Assert.False(ConstraintChecks.AllUnique(new[] { "a", "b", "a" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AllUnique_IsTrueBelowTwoElements(int count) {
        Assert.True(ConstraintChecks.AllUnique(Enumerable.Range(0, count).ToList()));
    }

    /// <summary>
    /// Both sides of the pairwise/set threshold, since they are separate implementations.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(200)]
    public void AllUnique_AgreesAcrossTheThreshold(int count) {
        var distinct = Enumerable.Range(0, count).ToList();
        Assert.True(ConstraintChecks.AllUnique(distinct));

        var repeated = new List<int>(distinct) { 0 };
        Assert.False(ConstraintChecks.AllUnique(repeated));
    }

    /// <summary>
    /// An enumerable with no count and no indexer still validates - the check is typed on
    /// IEnumerable, so the fallback the count constraints need does not arise here.
    /// </summary>
    [Fact]
    public void AllUnique_WalksAnEnumerableWithNoCount() {
        static IEnumerable<int> Sequence() {
            yield return 1;
            yield return 2;
            yield return 1;
        }

        Assert.False(ConstraintChecks.AllUnique(Sequence()));
    }

    [Fact]
    public void AllUnique_UsesValueEqualityWhereTheElementHasIt() {
        Assert.False(ConstraintChecks.AllUnique(new[] { new Point(1, 2), new Point(1, 2) }));
    }

    /// <summary>
    /// The case VM0025 warns about, pinned so the warning's claim stays true: a class with no
    /// equality of its own compares by reference, and two equal-looking elements both pass.
    /// </summary>
    [Fact]
    public void AllUnique_ComparesByReferenceWhereTheElementHasNoEquality() {
        Assert.True(ConstraintChecks.AllUnique(new[] { new Opaque("x"), new Opaque("x") }));
    }

    [Fact]
    public void AllUnique_StopsAtNullsRatherThanThrowing() {
        Assert.False(ConstraintChecks.AllUnique(new string?[] { null, "a", null }));
        Assert.True(ConstraintChecks.AllUnique(new string?[] { null, "a" }));
    }

    private sealed record Point(int X, int Y);

    private sealed class Opaque(string value) {
        public string Value { get; } = value;
    }

    /// <summary>
    /// A float and a double describing the same value must agree. They did not: the float overload
    /// widened to double first, which surfaced the float's representation error and carried it into
    /// the decimal comparison.
    /// </summary>
    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.7f)]
    [InlineData(1.0f)]
    [InlineData(0.1f)]
    [InlineData(12.5f)]
    public void IsMultipleOf_Float_AgreesWithTheSameValueAsADouble(float value) {
        Assert.Equal(
            ConstraintChecks.IsMultipleOf((double)(decimal)value, 0.1m),
            ConstraintChecks.IsMultipleOf(value, 0.1m));
    }

    [Theory]
    [InlineData(0.3f, true)]
    [InlineData(0.7f, true)]
    [InlineData(0.25f, false)]
    [InlineData(0.05f, false)]
    public void IsMultipleOf_Float_MatchesWhatTheConstraintAuthorWrote(float value, bool expected) =>
        Assert.Equal(expected, ConstraintChecks.IsMultipleOf(value, 0.1m));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.MaxValue)]
    [InlineData(float.MinValue)]
    public void IsMultipleOf_Float_RejectsWhatDecimalCannotHold(float value) =>
        Assert.False(ConstraintChecks.IsMultipleOf(value, 0.1m));

    // The format checks. Each parity theory runs the same input through the real attribute and
    // through the reproduction; the pinned theories state the semantics a reader should be able
    // to learn from this file without opening the BCL.

    [Theory]
    [InlineData("a@b")]
    [InlineData("user@example.com")]
    [InlineData("root@localhost")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("@")]
    [InlineData("a@")]
    [InlineData("@b")]
    [InlineData("a@@b")]
    [InlineData("a@b@c")]
    [InlineData("a b@c d")]
    [InlineData("\"a@b\"@example.com")]
    [InlineData("a@b\r")]
    [InlineData("a@b\n")]
    [InlineData("a@\rb")]
    [InlineData("no-at-sign")]
    [InlineData("ünïcode@dömain")]
    [InlineData("a@b ")]
    public void IsEmail_MatchesEmailAddressAttribute(string value) =>
        Assert.Equal(new EmailAddressAttribute().IsValid(value), ConstraintChecks.IsEmail(value));

    /// <summary>
    /// The semantics in one row each: one interior '@', no line breaks, nothing else. 'a@b' passing
    /// is not leniency to apologise for - RFC 5322's addr-spec permits a dotless domain.
    /// </summary>
    [Theory]
    [InlineData("a@b", true)]
    [InlineData("root@localhost", true)]
    [InlineData("a b@c d", true)]
    [InlineData("@b", false)]
    [InlineData("a@", false)]
    [InlineData("a@b@c", false)]
    [InlineData("a@b\n", false)]
    public void IsEmail_PinsTheSemantics(string value, bool expected) =>
        Assert.Equal(expected, ConstraintChecks.IsEmail(value));

    [Theory]
    [InlineData("+1 (555) 123-4567")]
    [InlineData("555-1234 ext. 89")]
    [InlineData("555-1234 ext 89")]
    [InlineData("555-1234 x89")]
    [InlineData("555-1234 X89")]
    [InlineData("555-1234 ext.")]
    [InlineData("555-1234 ext. abc")]
    [InlineData("555 ex+t. 12")]
    [InlineData("x")]
    [InlineData("ext. 123")]
    [InlineData("+")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("123 456.789(0)")]
    [InlineData("123#456")]
    [InlineData("  123  ")]
    public void IsPhone_MatchesPhoneAttribute(string value) =>
        Assert.Equal(new PhoneAttribute().IsValid(value), ConstraintChecks.IsPhone(value));

    /// <summary>
    /// Above <c>PhoneStackLimit</c> the scratch copy moves to the heap; the answer must not.
    /// </summary>
    [Fact]
    public void IsPhone_LongValues_TakeTheHeapPathToTheSameAnswer() {
        var longValid = string.Concat(new string(' ', 200), "555-1234");
        var longInvalid = string.Concat(new string(' ', 200), "abc");

        Assert.Equal(new PhoneAttribute().IsValid(longValid), ConstraintChecks.IsPhone(longValid));
        Assert.True(ConstraintChecks.IsPhone(longValid));
        Assert.False(ConstraintChecks.IsPhone(longInvalid));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    [InlineData("ftp://files.example.com")]
    [InlineData("http://")]
    [InlineData("gopher://example.com")]
    [InlineData("example.com")]
    [InlineData("www.example.com")]
    [InlineData(" http://example.com")]
    [InlineData("")]
    public void IsUrl_String_MatchesUrlAttribute(string value) =>
        Assert.Equal(new UrlAttribute().IsValid(value), ConstraintChecks.IsUrl(value));

    /// <summary>
    /// The Uri overload carries the current BCL semantics: absolute, scheme http/https/ftp. On
    /// net8 the attribute itself rejects every <see cref="Uri"/> - the branch arrived later - and
    /// one semantics is emitted for both TFMs, so the parity assertion is version-fenced and the
    /// net8 fence pins the divergence as deliberate rather than hiding it.
    /// </summary>
    [Theory]
    [InlineData("http://example.com", true)]
    [InlineData("https://example.com/path", true)]
    [InlineData("ftp://files.example.com", true)]
    [InlineData("gopher://example.com", false)]
    [InlineData("mailto:a@b.com", false)]
    public void IsUrl_Uri_PinsTheSemantics(string uri, bool expected) {
        var value = new Uri(uri);

        Assert.Equal(expected, ConstraintChecks.IsUrl(value));

#if NET10_0_OR_GREATER
        Assert.Equal(new UrlAttribute().IsValid(value), ConstraintChecks.IsUrl(value));
#else
        Assert.False(new UrlAttribute().IsValid(value));
#endif
    }

    [Fact]
    public void IsUrl_Uri_RejectsARelativeUri() =>
        Assert.False(ConstraintChecks.IsUrl(new Uri("/path", UriKind.Relative)));

    [Theory]
    [InlineData("4111111111111111")]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData(" 4111111111111111 ")]
    [InlineData("4111111111111112")]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("abc")]
    [InlineData("41x11")]
    [InlineData("0")]
    [InlineData("59")]
    public void IsCreditCard_MatchesCreditCardAttribute(string value) =>
        Assert.Equal(new CreditCardAttribute().IsValid(value), ConstraintChecks.IsCreditCard(value));

    /// <summary>
    /// An empty string - and a string of only dashes and spaces - has checksum zero and passes,
    /// exactly as the attribute reads it. Pinned so the parity is visibly a choice: [Required] is
    /// the presence check, and [CreditCard] alone accepts absence-shaped values.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("- -")]
    [InlineData("0")]
    public void IsCreditCard_PassesTheChecksumOnNothing(string value) {
        Assert.True(ConstraintChecks.IsCreditCard(value));
        Assert.True(new CreditCardAttribute().IsValid(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("YWJj")]
    [InlineData("YWJjZA==")]
    [InlineData("YW Jj")]
    [InlineData("YWJj===")]
    [InlineData("!!!")]
    [InlineData("YWJ")]
    public void IsBase64_MatchesBase64StringAttribute(string value) =>
        Assert.Equal(new Base64StringAttribute().IsValid(value), ConstraintChecks.IsBase64(value));

    [Theory]
    [InlineData("photo.png")]
    [InlineData("photo.PNG")]
    [InlineData("photo.jpeg")]
    [InlineData("photo.txt")]
    [InlineData("photo")]
    [InlineData("")]
    [InlineData("photo.png ")]
    [InlineData("archive.tar.gz")]
    [InlineData("dir.png/file")]
    public void HasFileExtension_MatchesFileExtensionsAttribute(string value) {
        // The runtime receives the set the generator normalizes out of the attribute; this states
        // the default set post-normalization, which the reader's own tests pin against the source.
        string[] normalizedDefault = [".png", ".jpg", ".jpeg", ".gif"];

        Assert.Equal(
            new FileExtensionsAttribute().IsValid(value),
            ConstraintChecks.HasFileExtension(value, normalizedDefault));
    }

    [Fact]
    public void HasFileExtension_ComparesCaseInsensitively_AgainstTheLoweredSet() {
        Assert.True(ConstraintChecks.HasFileExtension("a.GIF", [".gif"]));
        Assert.False(ConstraintChecks.HasFileExtension("a.gif", [".png"]));
    }
}
