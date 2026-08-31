using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins the shape-key vocabulary language packs are authored against.
/// </summary>
/// <remarks>
/// <para>
/// A pack is a JSON file in a consumer's own repository holding these keys verbatim, so the
/// vocabulary is a file format rather than an API. Nothing else in the suite notices a rename:
/// the keys are dictionary values, so the public API snapshot pins only that
/// <see cref="ValidationMessageTemplates.TemplatesByKey"/> exists, and the generator's inventory
/// is checked against this map, which passes when both sides move together.
/// </para>
/// <para>
/// The failure a rename produces is silent, which is what makes it worth a contract of its own. A
/// pack carrying the old key still compiles and still registers; the entry is skipped, and that
/// one shape renders in the default language inside an otherwise translated application.
/// </para>
/// <para>
/// This follows <see cref="CodeDerivationContractTests"/>: a snapshot for review, and a checksum
/// against a constant in product source so that accepting the snapshot is deliberately not enough.
/// </para>
/// </remarks>
public class ShapeVocabularyContractTests {

    /// <summary>
    /// A template's argument count, read from its highest numbered hole. Derived rather than
    /// declared so the count cannot drift from the template it describes.
    /// </summary>
    private static int ArityOf(string template) {
        var holes = Regex.Matches(template, @"\{(\d)\}")
            .Cast<Match>()
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToList();

        return holes.Count == 0 ? 0 : holes.Max() + 1;
    }

    private static IEnumerable<(string Key, int Arity)> Vocabulary() =>
        ValidationMessageTemplates.TemplatesByKey
            .Select(entry => (entry.Key, ArityOf(entry.Value)))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal);

    [Fact]
    public void TheVocabulary_IsTheseKeysAndArities() {
        var builder = new StringBuilder();

        builder.Append("Shape vocabulary: ")
            .Append(ValidationMessageTemplates.TemplatesByKey.Count)
            .Append(" keys\n\n");

        foreach (var (key, arity) in Vocabulary()) {
            builder.Append(key).Append("  ").Append(arity).Append('\n');
        }

        Snapshot.Match(builder.ToString());
    }

    [Fact]
    public void TheVocabulary_MatchesTheChecksumInProductSource() {
        var actual = Checksum(Vocabulary().Select(entry => entry.Key + "=" + entry.Arity));

        Assert.True(
            actual == ValidationMessageTemplates.ShapeVocabularyChecksum,
            $"""
            The shape vocabulary moved.

            These keys are a file format: a consumer's language pack holds them verbatim, and a
            renamed key is skipped in silence rather than reported. Accepting the snapshot is
            deliberately not enough.

            Adding a key is safe and only needs this checksum re-pinned. Renaming one, removing
            one, or changing a template's argument count is a breaking change to every pack that
            carries it.

            If the change is intended:
              1. set ValidationMessageTemplates.ShapeVocabularyChecksum to "{actual}"
              2. run UPDATE_SNAPSHOTS=1 to record the new vocabulary

            expected {ValidationMessageTemplates.ShapeVocabularyChecksum}
            actual   {actual}
            """);
    }

    [Fact]
    public void EveryKey_IsLowerSnakeCase() {
        // The vocabulary is authored by hand in JSON, so a key that does not follow the convention
        // is one a pack author will spell the other way and lose to a silent skip.
        Assert.All(ValidationMessageTemplates.TemplatesByKey.Keys,
            key => Assert.Matches(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)?$", key));
    }

    [Fact]
    public void EveryKeysFirstSegment_IsAKnownCode() {
        // The generator's typo heuristic keys on the segment before the first dot: an unknown key
        // whose prefix is a known code is judged a misspelling and reported, and anything else is
        // taken for the consumer's own code and compiled untouched. A shape key whose prefix is not
        // a code would put every misspelling of it in the second bucket, silently.
        var codes = typeof(ValidationCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string)field.GetValue(null)!)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = ValidationMessageTemplates.TemplatesByKey.Keys
            .Select(key => key.Split('.')[0])
            .Where(prefix => !codes.Contains(prefix))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(prefix => prefix, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphans.Count == 0,
            "Shape keys whose first segment is not a ValidationCodes value: " + string.Join(", ", orphans));
    }

    private static string Checksum(IEnumerable<string> lines) {
        using var sha = SHA256.Create();

        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));

        return Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
    }
}
