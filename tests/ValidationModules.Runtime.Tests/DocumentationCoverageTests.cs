using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Cross-checks the pinned public API against <c>website/</c>: every public type is named
/// somewhere in the documentation, every constraint attribute has its section in the attributes
/// reference, and every code in the vocabulary appears in the codes reference.
/// </summary>
/// <remarks>
/// <para>
/// The rc1013 trial found eleven user-facing types no page named - two of them cost three of the
/// four models a documented detour into the nupkg. The API snapshots already enumerate the
/// surface and are already enforced; this closes the loop nothing was closing: nothing compared
/// them to the documentation.
/// </para>
/// <para>
/// The allow-list is for types deliberately undocumented, each with the reason on the record. An
/// entry whose type gains a mention - or leaves the API - fails the stale check, so the list can
/// only shrink truthfully.
/// </para>
/// </remarks>
public class DocumentationCoverageTests {

    /// <summary>
    /// Types deliberately not named in the documentation, and why. Reaching for this file to add
    /// an entry is the moment to ask whether the type should be documented instead.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Undocumented = new Dictionary<string, string> {
        // Extension-method holders: a reader calls the methods, which the guides show on their
        // subjects; the holder's name appears only in metadata and stack traces.
        ["ValidatorForExtensions"] = "extension-method holder",
        ["ValidationContextExtensions"] = "extension-method holder",
        ["ValidationModulesServiceCollectionExtensions"] = "extension-method holder",
        ["ValidationModulesEndpointExtensions"] = "extension-method holder",
        ["ValidationModulesAspNetCoreExtensions"] = "extension-method holder",

        // The generator's own wiring: consumers never write these names - generated code does.
        ["RuntimeContract"] = "written by the generator's version handshake, not by consumers",
        ["ValidatorRegistration"] = "written by generated registrations, not by consumers",
        ["DataAnnotationsSupport"] = "called by generated validators, not by consumers",

        // The chaining surface behind rules.Require(...).Length(...): a reader writes the calls,
        // which rules-api.md documents on the builder; the holder is plumbing.
        ["PropertyRulesExtensions"] = "extension-method holder",
    };

    // ---- W0.1: every public type is named somewhere ---------------------------------------

    [Fact]
    public void EveryPublicType_IsNamedSomewhereInTheDocumentation() {
        var documentation = DocumentationText();
        var missing = new List<string>();

        foreach (var type in PublicTypes()) {
            if (Undocumented.ContainsKey(type)) {
                continue;
            }

            if (!MentionedIn(documentation, type)) {
                missing.Add(type);
            }
        }

        Assert.True(missing.Count == 0,
            "Public types no page under website/ names (document them, or add them to the " +
            $"allow-list with a reason):{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", missing)}");
    }

    /// <summary>
    /// The allow-list can only shrink truthfully: an entry that gains a mention, or whose type
    /// leaves the API, is stale and fails here rather than lingering as a silent exemption.
    /// </summary>
    [Fact]
    public void TheAllowList_CarriesNoStaleEntries() {
        var documentation = DocumentationText();
        var types = PublicTypes().ToHashSet(StringComparer.Ordinal);
        var stale = new List<string>();

        foreach (var entry in Undocumented.Keys) {
            if (!types.Contains(entry)) {
                stale.Add($"{entry} (no longer in the public API)");
            } else if (MentionedIn(documentation, entry)) {
                stale.Add($"{entry} (now documented - remove the exemption)");
            }
        }

        Assert.True(stale.Count == 0,
            $"Stale allow-list entries:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", stale)}");
    }

    // ---- W0.2: the reference tables are complete ------------------------------------------

    /// <summary>
    /// <c>reference/attributes.md</c> opens with "Every attribute the generator reads", and this
    /// is what makes that sentence stay true: a heading per concrete constraint attribute, in the
    /// bracketed form a reader would type.
    /// </summary>
    [Fact]
    public void EveryConstraintAttribute_HasItsSectionInTheAttributesReference() {
        var reference = File.ReadAllText(Path.Combine(WebsiteRoot, "reference", "attributes.md"));
        var missing = new List<string>();

        foreach (var attribute in ConcreteAttributeTypes()) {
            var bare = attribute.Substring(0, attribute.Length - "Attribute".Length);

            if (!Regex.IsMatch(reference, $@"^#+ .*\[{Regex.Escape(bare)}\]", RegexOptions.Multiline)) {
                missing.Add($"[{bare}]");
            }
        }

        Assert.True(missing.Count == 0,
            "Constraint attributes with no section in reference/attributes.md:" +
            $"{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", missing)}");
    }

    [Fact]
    public void EveryValidationCode_AppearsInTheCodesReference() {
        var reference = File.ReadAllText(Path.Combine(WebsiteRoot, "reference", "codes.md"));
        var missing = new List<string>();

        foreach (Match code in Regex.Matches(
                     RuntimeSnapshot(), @"public const string \w+ = ""([a-z0-9_]+)"";")) {
            var value = code.Groups[1].Value;

            if (!Regex.IsMatch(reference, $@"(?<![a-z0-9_]){Regex.Escape(value)}(?![a-z0-9_])")) {
                missing.Add(value);
            }
        }

        Assert.True(missing.Count == 0,
            $"Codes missing from reference/codes.md:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", missing)}");
    }

    // ---- parsing --------------------------------------------------------------------------

    private static IEnumerable<string> PublicTypes() {
        var text = RuntimeSnapshot() + Environment.NewLine + AspNetCoreSnapshot();
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match declaration in Regex.Matches(
                     text, @"\b(?:class|interface|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)")) {
            names.Add(declaration.Groups[1].Value);
        }

        // Delegates carry their name between the return type and the parameter list.
        foreach (Match declaration in Regex.Matches(
                     text, @"\bdelegate\b[^;\n(]*?([A-Za-z_][A-Za-z0-9_]*)\s*[(<]")) {
            names.Add(declaration.Groups[1].Value);
        }

        return names;
    }

    private static IEnumerable<string> ConcreteAttributeTypes() =>
        Regex.Matches(RuntimeSnapshot(), @"\bpublic (?:sealed )?class ([A-Za-z0-9_]+Attribute)\b")
            .Select(match => match.Groups[1].Value);

    /// <summary>
    /// Whether the documentation names the type. The <c>Attribute</c> suffix matches in both
    /// directions, because the docs write <c>[Pattern]</c> for <c>PatternAttribute</c>.
    /// </summary>
    private static bool MentionedIn(string documentation, string type) {
        if (Regex.IsMatch(documentation, $@"\b{Regex.Escape(type)}\b")) {
            return true;
        }

        if (type.EndsWith("Attribute", StringComparison.Ordinal) && type.Length > "Attribute".Length) {
            var bare = type.Substring(0, type.Length - "Attribute".Length);
            return Regex.IsMatch(documentation, $@"\b{Regex.Escape(bare)}\b");
        }

        return false;
    }

    private static string DocumentationText() {
        var pages = Directory.EnumerateFiles(WebsiteRoot, "*.md", SearchOption.AllDirectories)
            .Where(file => !file.Contains("node_modules", StringComparison.Ordinal))
            .Select(File.ReadAllText);

        return string.Join(Environment.NewLine, pages);
    }

    private static string RuntimeSnapshot() => File.ReadAllText(Path.Combine(
        RepositoryRoot, "tests", "ValidationModules.Runtime.Tests",
        "Snapshots", "PublicApiTests.RuntimeApi.verified.txt"));

    private static string AspNetCoreSnapshot() => File.ReadAllText(Path.Combine(
        RepositoryRoot, "tests", "ValidationModules.AspNetCore.Tests",
        "Snapshots", "PublicApiTests.AspNetCoreApi.verified.txt"));

    private static string WebsiteRoot => Path.Combine(RepositoryRoot, "website");

    private static string RepositoryRoot { get; } = ResolveRepositoryRoot();

    private static string ResolveRepositoryRoot() {
        var configured = typeof(DocumentationCoverageTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepositoryRoot")?.Value;

        if (configured is null || !Directory.Exists(Path.Combine(configured, "website"))) {
            throw new InvalidOperationException(
                "RepositoryRoot assembly metadata is missing or does not contain website/.");
        }

        return configured;
    }
}
