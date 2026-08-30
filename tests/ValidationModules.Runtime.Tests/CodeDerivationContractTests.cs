using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ValidationModules.Naming;
using ValidationModules.Rules;
using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins every code <see cref="RuleText.CodeOfPredicate"/> derives, as a contract rather than as a
/// convenience.
/// </summary>
/// <remarks>
/// <para>
/// A derived code is what a client switches on and what a translation catalogue is keyed by, so a
/// change here reaches into applications nobody in this repository can see. The corpus below is the
/// evidence of what the mapping currently is; the checksum is what stops it moving quietly.
/// </para>
/// <para>
/// <b>The snapshot alone would not be enough.</b> <c>UPDATE_SNAPSHOTS=1</c> exists so an intended
/// change to generated output is cheap to accept, and that is exactly the wrong property here. So
/// the same corpus is checksummed against a constant in product source: accepting the snapshot
/// leaves this test failing until someone edits <see cref="RuleText.CodeDerivationChecksum"/>, and
/// that edit sits next to the comment saying what moving it means.
/// </para>
/// </remarks>
public class CodeDerivationContractTests {

    private static string Name(string clr) => CamelCaseFieldNamer.Instance.ToFieldName(clr);

    /// <summary>
    /// Every shape the derivation treats differently, grouped by what it exercises. A predicate
    /// belongs here when changing the mechanics would change its code.
    /// </summary>
    private static readonly (string Group, string Predicate)[] Corpus = [
        ("comparison", "x => x.Start < x.End"),
        ("comparison", "x => x.Start <= x.End"),
        ("comparison", "x => x.Start > x.End"),
        ("comparison", "x => x.Start >= x.End"),
        ("comparison", "x => x.Status == x.Wanted"),
        ("comparison", "x => x.Status != x.Wanted"),
        ("comparison", "x => x.Age >= 18"),
        ("comparison", "x => x.Ratio <= 0.5"),

        ("logical", "x => x.Paid && x.Shipped"),
        ("logical", "x => x.Paid || x.Waived"),
        ("logical", "x => !x.Cancelled"),
        ("logical", "x => !(x.Paid && x.Shipped)"),
        ("logical", "x => !x.Paid && x.Shipped"),
        ("logical", "x => x.Paid && (x.Shipped || x.Waived)"),

        ("arithmetic", "x => x.Total * 2 > x.Limit"),
        ("arithmetic", "x => x.Total - x.Paid > 0"),
        ("arithmetic", "x => x.Total + x.Tax <= x.Limit"),
        ("arithmetic", "x => x.Total / x.Count > 1"),
        ("arithmetic", "x => x.Total % 5 == 0"),

        ("null", "x => x.Name != null"),
        ("null", "x => x.Name == null"),
        ("null", "x => x.Name is null"),
        ("null", "x => x.Name is not null"),
        ("null", "x => string.IsNullOrEmpty(x.Name)"),
        ("null", "x => !string.IsNullOrEmpty(x.Name)"),
        ("null", "x => string.IsNullOrWhiteSpace(x.Name)"),
        ("null", "x => !string.IsNullOrWhiteSpace(x.Name)"),

        ("emptiness", "x => x.Items.Count > 0"),
        ("emptiness", "x => x.Items.Count == 0"),
        ("emptiness", "x => x.Items.Length > 0"),
        ("emptiness", "x => x.Items.Any()"),
        ("emptiness", "x => !x.Items.Any()"),

        ("member path", "x => x.Home.PostalCode != null"),
        ("member path", "x => x.Home?.PostalCode != null"),
        ("member path", "x => x.Name.Length > 3"),
        ("member path", "x => x.HTTPStatus == 200"),
        ("member path", "x => x.CustomsCode != null"),

        ("literal", "x => x.Status == \"active\""),
        ("literal", "x => x.Status == \"pending\""),
        ("literal", "x => x.Email.Contains(\"@\")"),
        ("literal", "x => x.Email.Contains(\".\")"),
        ("literal", "x => x.Sku.StartsWith(\"AB-\")"),
        ("literal", "x => x.Name == \"two words\""),

        ("lambda", "x => x.Lines.Sum(l => l.Price) > 0"),
        ("lambda", "x => x.Lines.Sum(line => line.Price) > 0"),
        ("lambda", "x => x.Lines.All(l => l.Qty > 0)"),
        ("lambda", "x => x.Lines.Any(l => l.Sku == null)"),

        ("call", "x => Patterns.Sku().IsMatch(x.Sku)"),
        ("call", "x => x.Start.AddDays(x.Nights) <= x.End"),
        ("call", "x => x.Name.Trim().Length > 0"),

        ("pattern", "x => x.Age is >= 0 and <= 30"),
        ("pattern", "x => x.Status is Status.Active or Status.Pending"),

        ("local", "x => total <= x.CreditLimit"),
        ("local", "x => runningTotal <= x.CreditLimit"),
    ];

    private static IEnumerable<(string Group, string Predicate, string Code)> Derived() =>
        Corpus.Select(entry => (entry.Group, entry.Predicate,
            Code: RuleText.CodeOfPredicate(entry.Predicate, Name) ?? "(none)"));

    [Fact]
    public void TheCorpus_DerivesTheseCodes() {
        var builder = new StringBuilder();
        var group = string.Empty;

        builder.Append("Code derivation contract ")
            .Append(RuleText.CodeDerivationContract.ToString(CultureInfo.InvariantCulture))
            .Append('\n');

        foreach (var (entryGroup, predicate, code) in Derived()) {
            if (entryGroup != group) {
                group = entryGroup;
                builder.Append('\n').Append("-- ").Append(group).Append('\n');
            }

            builder.Append(predicate).Append('\n').Append("  -> ").Append(code).Append('\n');
        }

        Snapshot.Match(builder.ToString());
    }

    [Fact]
    public void TheCorpus_MatchesTheChecksumInProductSource() {
        var actual = Checksum(Derived().Select(entry => entry.Predicate + "=" + entry.Code));

        Assert.True(
            actual == RuleText.CodeDerivationChecksum,
            $"""
            The derived codes moved.

            Every code here is a wire contract: a client switches on it and a translation
            catalogue is keyed by it, so moving one reaches applications this repository
            cannot see. Accepting the snapshot is deliberately not enough.

            If the change is intended, and this is a major release or 1.0.0 has not shipped:
              1. set RuleText.CodeDerivationChecksum to "{actual}"
              2. bump RuleText.CodeDerivationContract if any previously-derived code changed
              3. run UPDATE_SNAPSHOTS=1 to record the new corpus

            expected {RuleText.CodeDerivationChecksum}
            actual   {actual}
            """);
    }

    [Fact]
    public void EveryCorpusEntry_DerivesSomething() {
        // A null here means the caller falls back to the generic code, which for a real condition
        // would be the collision this whole change exists to remove.
        Assert.All(Derived(), entry => Assert.NotEqual("(none)", entry.Code));
    }

    /// <summary>
    /// Codes two corpus entries are meant to share, because the predicates say the same thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>x.Home.PostalCode</c> and <c>x.Home?.PostalCode</c> are one rule written two ways. The
    /// difference is what happens when <c>Home</c> is null, which is a null-safety choice in the
    /// author's own code rather than a different thing being asserted, so one key is right.
    /// </para>
    /// <para>
    /// <c>Sum(l =&gt; l.Price)</c> and <c>Sum(line =&gt; line.Price)</c> are the same rule under two
    /// parameter names. Sharing a code here is the fix rather than the defect: a code that moved
    /// when the parameter was renamed would be churn with no semantic change behind it.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> IntendedCollisions = [
        "home_postal_code_not_equal_null",
        "lines_sum_price_greater_than_0",
    ];

    [Fact]
    public void NoTwoCorpusEntries_ShareACode() {
        // Two different rules with one code is the failure the derivation is for. Any pair that
        // collides is either a defect or a shape the corpus should stop claiming to distinguish.
        var collisions = Derived()
            .Where(entry => !IntendedCollisions.Contains(entry.Code))
            .GroupBy(entry => entry.Code)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}\n    {string.Join("\n    ", group.Select(e => e.Predicate))}")
            .ToList();

        Assert.True(collisions.Count == 0, "Distinct predicates share a code:\n  " + string.Join("\n  ", collisions));
    }

    private static string Checksum(IEnumerable<string> lines) {
        using var sha = SHA256.Create();

        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));

        return Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
    }
}
