using System.Text.RegularExpressions;
using ValidationModules.Constraints;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The constraint attributes as a public API surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing in the runtime ever constructs one of these.</b> The generator reads their arguments
/// out of metadata at build time and emits comparisons; no <c>ValidationAttribute</c> is
/// instantiated and no <c>IsValid</c> is called anywhere on the validation path. A coverage report
/// showing these files unexercised by anything but this file is the evidence for that claim, not a
/// gap in it.
/// </para>
/// <para>
/// What is still worth pinning is the shape a <i>reader</i> depends on: which constructor overloads
/// exist, and what an omitted bound defaults to. Both are load-bearing —
/// <c>[StringLength(Max = 500)]</c> reads as "no minimum" only because <c>Min</c> defaults to zero,
/// and the front end reads these same defaults out of metadata.
/// </para>
/// </remarks>
public class ConstraintAttributeTests {

    [Fact]
    public void StringLength_PositionalConstructor_TakesMinThenMax() {
        var attribute = new StringLengthAttribute(1, 100);

        Assert.Equal(1, attribute.Min);
        Assert.Equal(100, attribute.Max);
    }

    [Fact]
    public void StringLength_Parameterless_IsUnboundedUntilOneBoundIsNamed() {
        // What makes [StringLength(Max = 500)] readable: the other bound stays out of the way.
        var attribute = new StringLengthAttribute();

        Assert.Equal(0, attribute.Min);
        Assert.Equal(int.MaxValue, attribute.Max);
    }

    [Fact]
    public void StringLength_NamedBoundsOverrideTheDefaults() {
        Assert.Equal(500, new StringLengthAttribute { Max = 500 }.Max);
        Assert.Equal(0, new StringLengthAttribute { Max = 500 }.Min);
    }

    [Fact]
    public void ItemCount_HasTheSameBoundDefaultsAsStringLength() {
        // Deliberately identical: the two constraints differ in what they count, not in how bounds
        // are written, and a reader who has learnt one should not have to re-learn the other.
        var attribute = new ItemCountAttribute();

        Assert.Equal(0, attribute.Min);
        Assert.Equal(int.MaxValue, attribute.Max);
        Assert.Equal(1, new ItemCountAttribute(1, 10).Min);
        Assert.Equal(10, new ItemCountAttribute(1, 10).Max);
    }

    [Fact]
    public void Required_DoesNotAllowEmptyStringsByDefault() {
        // Plan §12 Q5 — still open as policy, pinned here as behaviour so a change is deliberate.
        Assert.False(new RequiredAttribute().AllowEmptyStrings);
        Assert.True(new RequiredAttribute { AllowEmptyStrings = true }.AllowEmptyStrings);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void Range_IntegralOverload_KeepsBothBounds(int min, int max) {
        var attribute = new RangeAttribute(min, max);

        Assert.Equal(min, attribute.Min);
        Assert.Equal(max, attribute.Max);
    }

    [Fact]
    public void Range_HasAnOverloadPerConstantForm() {
        // int, long, double and string, because a constant of the remaining useful types cannot be
        // written in metadata at all. The string overload is the one RangeStringBoundsTests in the
        // generator project records as unimplemented downstream.
        Assert.Equal(0, new RangeAttribute(0, 30).Min);
        Assert.Equal(0L, new RangeAttribute(0L, 30L).Min);
        Assert.Equal(0.0, new RangeAttribute(0.0, 1.0).Min);
        Assert.Equal("2000-01-01", new RangeAttribute("2000-01-01", "2100-01-01").Min);
    }

    [Fact]
    public void Range_BoundsAreInclusiveUnlessMadeExclusive() {
        Assert.False(new RangeAttribute(0, 30).ExclusiveMin);
        Assert.False(new RangeAttribute(0, 30).ExclusiveMax);
        Assert.True(new RangeAttribute(0.0, 1.0) { ExclusiveMax = true }.ExclusiveMax);
    }

    [Fact]
    public void Pattern_InlineForm_CarriesThePatternAndNoProvider() {
        var attribute = new PatternAttribute("^[A-Z]{3}$");

        Assert.Equal("^[A-Z]{3}$", attribute.Pattern);
        Assert.Null(attribute.RegexProvider);
        Assert.Null(attribute.RegexMember);
    }

    [Fact]
    public void Pattern_ReferenceForm_CarriesTheProviderAndNoPattern() {
        // The AOT-clean form: it resolves to a member the consumer declared with [GeneratedRegex],
        // so the regex parser is never rooted.
        var attribute = new PatternAttribute(typeof(SamplePatterns), nameof(SamplePatterns.Sku));

        Assert.Null(attribute.Pattern);
        Assert.Equal(typeof(SamplePatterns), attribute.RegexProvider);
        Assert.Equal(nameof(SamplePatterns.Sku), attribute.RegexMember);
    }

    [Fact]
    public void Pattern_HasNoRegexOptionsByDefault() {
        Assert.Equal(default, new PatternAttribute("^a$").Options);
    }

    [Fact]
    public void AllowedValues_KeepsItsValuesInDeclarationOrder() {
        // Order is what the message renders, so it is part of what a caller reads back.
        var attribute = new AllowedValuesAttribute("available", "pending", "sold");

        Assert.Equal(new object[] { "available", "pending", "sold" }, attribute.Values);
    }

    [Fact]
    public void AllowedValues_ComparesOrdinallyByDefault() {
        Assert.Equal(StringComparison.Ordinal, new AllowedValuesAttribute("a").Comparison);
    }

    [Fact]
    public void EveryConstraintAttribute_TargetsPropertiesAndParameters() {
        // The property target is what a record's positional parameter needs to reach — and the
        // reason [property: Required] is required there rather than optional.
        foreach (var type in new[] {
                     typeof(RequiredAttribute), typeof(StringLengthAttribute), typeof(RangeAttribute),
                     typeof(PatternAttribute), typeof(AllowedValuesAttribute), typeof(ItemCountAttribute),
                     typeof(ValidateNestedAttribute),
                 }) {

            var usage = (AttributeUsageAttribute?)Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute));

            Assert.NotNull(usage);
            Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Property), $"{type.Name} does not target properties");
        }
    }

    [Fact]
    public void EveryConstraintAttribute_DerivesFromTheSharedBase() {
        // The base is how the front end recognises the vocabulary without naming each attribute.
        foreach (var type in new[] {
                     typeof(RequiredAttribute), typeof(StringLengthAttribute), typeof(RangeAttribute),
                     typeof(PatternAttribute), typeof(AllowedValuesAttribute), typeof(ItemCountAttribute),
                 }) {

            Assert.True(typeof(ValidationConstraintAttribute).IsAssignableFrom(type), $"{type.Name} does not");
        }
    }

    private static class SamplePatterns {
        public static Regex Sku() => new("^[A-Z]{3}$");
    }
}
