using System.Reflection;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Properties of the VM#### catalogue as a whole, rather than of any one diagnostic.
/// </summary>
/// <remarks>
/// A declared descriptor is a published promise: it appears in
/// <c>AnalyzerReleases.Unshipped.md</c>, it can be tuned in an <c>.editorconfig</c>, and a reader
/// finding it in the reference has every reason to expect it to fire. Nothing in Roslyn checks that
/// a descriptor is ever reported, so that is checked here.
/// </remarks>
public class DiagnosticCatalogueTests {

    private static readonly IReadOnlyList<DiagnosticDescriptor> Declared =
        typeof(ValidationModules.SourceGenerator.Impl.ValidationDiagnostics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(DiagnosticDescriptor))
            .Select(field => (DiagnosticDescriptor)field.GetValue(null)!)
            .ToList();

    /// <summary>
    /// Declared, released, and never constructed anywhere in the product.
    /// </summary>
    /// <remarks>
    /// <b>Empty, and keeping it empty is the point.</b> VM0051 and VM0065 were here and were wired
    /// up; VM0007 was the last entry and its descriptor was deleted rather than implemented -
    /// <c>[ValidateNested]</c> on a type with no rules still says nothing, but a descriptor nothing
    /// constructs is a promise the catalogue does not keep, and deleting it is honest where
    /// carrying it was not.
    ///
    /// So every declared descriptor is now reachable. A new one added without a report site fails
    /// <see cref="DescriptorsThatAreNeverReported_AreExactlyTheOnesRecordedAsSuch"/> immediately,
    /// which is a stronger gate than this list ever was. Add an entry only to record a deliberate,
    /// temporary gap - and expect to justify it.
    /// </remarks>
    private static readonly HashSet<string> NeverReported = [];

    [Fact]
    public void EveryDescriptor_HasATestReferencingItsId() {
        // The coverage gate. A new descriptor with no test fails here rather than shipping unproven.
        var covered = TestSourceIds();

        var uncovered = Declared
            .Select(descriptor => descriptor.Id)
            .Where(id => !covered.Contains(id) && !NeverReported.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(uncovered);
    }

    [Fact]
    public void DescriptorsThatAreNeverReported_AreExactlyTheOnesRecordedAsSuch() {
        // Fails in both directions: implementing a recorded gap without deleting its entry here
        // fails, and letting a new descriptor go unreported fails too. With the list empty, the
        // second direction is the whole test - every descriptor must have a report site.
        var reported = ProductSourceIds();

        var actuallyDead = Declared
            .Select(descriptor => descriptor.Id)
            .Where(id => !reported.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(NeverReported.OrderBy(id => id, StringComparer.Ordinal).ToList(), actuallyDead);
    }

    [Fact]
    public void EveryDescriptor_AppearsInTheUnshippedReleaseFile() {
        // RS2008's requirement, checked here too because the analyzer only runs on the generator
        // project and a drifted file is invisible until someone tries to ship a release.
        var released = File.ReadAllText(ReleaseFile());

        Assert.All(Declared, descriptor => Assert.Contains(descriptor.Id, released));
    }

    [Fact]
    public void ReleaseFile_DeclaresNothingThatDoesNotExist() {
        var declaredIds = Declared.Select(descriptor => descriptor.Id).ToHashSet(StringComparer.Ordinal);

        var orphans = System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(ReleaseFile()), @"^VM\d{4}",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Value)
            .Where(id => !declaredIds.Contains(id))
            .ToList();

        Assert.Empty(orphans);
    }

    [Fact]
    public void DocumentationClaimsOfNeverReported_MatchTheRecordedSet() {
        // The class of bug this pins: guide/nesting.md said VM0007 was "never reported" while
        // guide/troubleshooting.md documented it firing - and the report site existed all along.
        // NeverReported above is the authority, so a doc paragraph may make that claim only about
        // an id recorded there. With the set empty, the claim is banned outright.
        var claim = new System.Text.RegularExpressions.Regex(
            @"never\s+(reported|fires)|not\s+reported|nothing\s+in\s+the\s+product\s+reports",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var id = new System.Text.RegularExpressions.Regex(@"VM\d{4}");
        var offending = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepositoryRoot(), "website"), "*.md", SearchOption.AllDirectories)) {
            foreach (var paragraph in File.ReadAllText(file).Split("\n\n")) {
                if (!claim.IsMatch(paragraph)) {
                    continue;
                }

                foreach (System.Text.RegularExpressions.Match match in id.Matches(paragraph)) {
                    if (!NeverReported.Contains(match.Value)) {
                        offending.Add($"{Path.GetFileName(file)}: claims {match.Value} is never reported");
                    }
                }
            }
        }

        Assert.Empty(offending);
    }

    [Fact]
    public void EveryDescriptor_SharesTheOneCategory() {
        // An .editorconfig severity override is written per category as often as per id, so a stray
        // category silently escapes a consumer's blanket rule.
        Assert.All(Declared, descriptor => Assert.Equal("ValidationModules.Usage", descriptor.Category));
    }

    [Fact]
    public void EveryDescriptor_IsEnabledByDefault() {
        Assert.All(Declared, descriptor => Assert.True(descriptor.IsEnabledByDefault));
    }

    [Fact]
    public void EveryDescriptor_HasATitleAndAMessageThatSaysMoreThanTheTitle() {
        Assert.All(Declared, descriptor => {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Title.ToString()));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.MessageFormat.ToString()));
            Assert.NotEqual(descriptor.Title.ToString(), descriptor.MessageFormat.ToString());
        });
    }

    [Fact]
    public void DescriptorIds_AreUnique() {
        var duplicates = Declared
            .GroupBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    private static HashSet<string> TestSourceIds() => IdsUnder(RepositoryRoot(), "tests");

    private static HashSet<string> ProductSourceIds() {
        // Every id mentioned in the product outside the descriptor declarations themselves, which
        // is what "is reported somewhere" reduces to once the descriptors are looked up by name.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var declarations = Path.Combine(RepositoryRoot(), "src",
            "ValidationModules.SourceGenerator.Impl", "ValidationDiagnostics.cs");

        var byName = typeof(ValidationModules.SourceGenerator.Impl.ValidationDiagnostics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(DiagnosticDescriptor))
            .ToDictionary(field => field.Name, field => ((DiagnosticDescriptor)field.GetValue(null)!).Id);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs",
                     SearchOption.AllDirectories)) {

            if (string.Equals(file, declarations, StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
                continue;
            }

            var text = File.ReadAllText(file);

            foreach (var pair in byName) {
                if (text.Contains($"ValidationDiagnostics.{pair.Key}", StringComparison.Ordinal)) {
                    reported.Add(pair.Value);
                }
            }
        }

        return reported;
    }

    private static HashSet<string> IdsUnder(string root, string folder) {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories)) {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"VM\d{4}")) {
                ids.Add(match.Value);
            }
        }

        return ids;
    }

    private static string ReleaseFile() => Path.Combine(RepositoryRoot(), "src",
        "ValidationModules.SourceGenerator.Impl", "AnalyzerReleases.Unshipped.md");

    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ValidationModules.sln"))) {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
