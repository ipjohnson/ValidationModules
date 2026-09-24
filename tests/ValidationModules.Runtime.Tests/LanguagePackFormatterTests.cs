using System.Globalization;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The merged-table formatter: precedence semantics that must survive the flattening, the folded
/// culture chain, and the tolerant edges. The storage decision itself is benchmarked in
/// <c>benchmarks/…/Design/LanguagePackStorageBenchmarks.cs</c>; these pin the behavior.
/// </summary>
public class LanguagePackFormatterTests
{
    private sealed record Pack(string Culture, params KeyValuePair<string, string>[] Entries)
        : IValidationLanguagePack
    {
        public IReadOnlyList<KeyValuePair<string, string>> Templates => Entries;
    }

    /// <summary>One entry. Target-typed new() inside a params expansion resolves to the array (CS8752), so this names it.</summary>
    private static KeyValuePair<string, string> E(string key, string template) =>
        new(key, template);

    private static string Under(
        string culture,
        LanguagePackFormatter formatter,
        in ValidationError error
    )
    {
        var previous = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

            return error.ToMessage(formatter);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static ValidationError AtMost() =>
        new(
            "name",
            ValidationCodes.StringLength,
            null,
            new ValidationMessageInfo(ValidationMessageTemplates.StringLengthAtMost, 10)
        );

    [Fact]
    public void ALaterPacksCodeEntry_BeatsAnEarlierPacksShapeEntry()
    {
        // The wholesale-reword story: a later pack that rewords the whole code takes all of its
        // shapes, which is what lets a one-entry pack override a family. Recency beats
        // specificity across layers; the merge must not lose that.
        var formatter = new LanguagePackFormatter([
            new Pack("fr", E("string_length.at_most", "précis : au plus {0}.")),
            new Pack("fr", E("string_length", "générique pour tout string_length.")),
        ]);

        Assert.Equal("générique pour tout string_length.", Under("fr", formatter, AtMost()));
    }

    [Fact]
    public void WithinOneLayer_TheShapeKeyBeatsTheCode()
    {
        var formatter = new LanguagePackFormatter([
            new Pack(
                "fr",
                E("string_length", "générique."),
                E("string_length.at_most", "précis : au plus {0}.")
            ),
        ]);

        Assert.Equal("précis : au plus 10.", Under("fr", formatter, AtMost()));
    }

    [Fact]
    public void TheFoldedChain_PrefersTheRegionalPack_AndFallsToItsParent()
    {
        var formatter = new LanguagePackFormatter([
            new Pack("fr", E("required", "parent."), E("string_length.at_most", "parent : {0}.")),
            new Pack("fr-CA", E("required", "régional.")),
        ]);

        var required = new ValidationError(
            "name",
            ValidationCodes.Required,
            null,
            ValidationMessageInfo.Required
        );

        Assert.Equal("régional.", Under("fr-CA", formatter, required));
        Assert.Equal("parent : 10.", Under("fr-CA", formatter, AtMost()));
        Assert.Equal("parent.", Under("fr", formatter, required));
    }

    [Fact]
    public void AFinishedStringError_MatchesAtTheCodeLevel_FieldOnlyTemplates()
    {
        var formatter = new LanguagePackFormatter([
            new Pack("fr", E("date_order", "{field} doit suivre la date de début.")),
        ]);

        var error = new ValidationError("end", "date_order", "end >= start.");

        Assert.Equal("end doit suivre la date de début.", Under("fr", formatter, error));
    }

    /// <summary>
    /// A finished-string error has no arguments, so a template that needs one cannot translate it.
    /// Rendering it anyway showed the reader a literal hole, as in "doit être l'une de ces valeurs
    /// : {0}."; the error keeps its own message instead.
    /// </summary>
    [Theory]
    [InlineData(ValidationCodes.Enum, "{field} doit être l'une de ces valeurs : {0}.")]
    [InlineData(ValidationCodes.MultipleOf, "{field} doit être un multiple de {0}.")]
    [InlineData(
        ValidationCodes.FileExtension,
        "{field} doit porter l'une de ces extensions : {0}."
    )]
    public void AFinishedStringError_KeepsItsMessage_WhenTheTemplateNeedsAnArgument(
        string code,
        string template
    )
    {
        var formatter = new LanguagePackFormatter([new Pack("fr", E(code, template))]);

        var error = new ValidationError("status", code, "status must be open or closed.");

        Assert.Equal("status must be open or closed.", Under("fr", formatter, error));
    }

    [Fact]
    public void AnEscapedBrace_IsNotAnArgumentHole()
    {
        var formatter = new LanguagePackFormatter([
            new Pack("fr", E("device_unknown", "{field} : code {{0}} inconnu.")),
        ]);

        var error = new ValidationError("deviceId", "device_unknown", "device is not registered.");

        Assert.Equal("deviceId : code {0} inconnu.", Under("fr", formatter, error));
    }

    /// <summary>
    /// What counts as an argument hole: a single digit in braces. Escaped braces are text, and
    /// <c>{field}</c> is filled from the error, as the renderer reads both.
    /// </summary>
    [Theory]
    [InlineData("{0}", true)]
    [InlineData("{field} doit être {9}.", true)]
    [InlineData("{{{0}}}", true)]
    [InlineData("}}{0}", true)]
    [InlineData("{field}", false)]
    [InlineData("{field} est obligatoire.", false)]
    [InlineData("{{0}}", false)]
    [InlineData("{10}", false)]
    [InlineData("{ 0}", false)]
    [InlineData("{0", false)]
    [InlineData("", false)]
    public void HasArgumentHole_FindsOnlyASingleDigitHole(string template, bool expected)
    {
        Assert.Equal(expected, ValidationMessageInfo.HasArgumentHole(template));
    }

    [Fact]
    public void NoPacks_AndUnpackedCultures_KeepTheDefaultRender()
    {
        var empty = new LanguagePackFormatter([]);
        var packed = new LanguagePackFormatter([new Pack("fr", E("required", "fr."))]);
        var error = new ValidationError(
            "name",
            ValidationCodes.Required,
            null,
            ValidationMessageInfo.Required
        );

        Assert.Equal("name is required.", Under("fr", empty, error));
        Assert.Equal("name is required.", Under("de", packed, error));
    }

    [Fact]
    public void AnAuthoredMessage_BeatsABareCodeKey()
    {
        // The rule the rc1015 trial showed nobody could see: whether a custom Message survived
        // used to turn on whether the pack happened to carry a bare key for the code. Authored
        // text now returns before any table lookup.
        var formatter = new LanguagePackFormatter([
            new Pack("fr", E("required", "{field} est requis.")),
        ]);

        var authored = new ValidationError("name", ValidationCodes.Required, "pick a handle")
        {
            MessageIsAuthored = true,
        };

        Assert.Equal("pick a handle", Under("fr", formatter, authored));
    }

    [Fact]
    public void AFinishedStringThatIsNotAuthored_StillTranslates()
    {
        // The escape hatch stays: a hand-written Report(field, code, message) with the rule's own
        // code is the documented route for wording custom text per culture, and it goes through
        // the bare code key exactly as before.
        var formatter = new LanguagePackFormatter([
            new Pack("fr", E("device_unknown", "{field} est inconnu.")),
        ]);

        var error = new ValidationError("deviceId", "device_unknown", "device is not registered.");

        Assert.Equal("deviceId est inconnu.", Under("fr", formatter, error));
    }
}
