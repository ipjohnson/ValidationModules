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
    /// VM0051 and VM0065 were here and are now wired up; VM0007 is what is left. It fires when
    /// <c>[ValidateNested]</c> targets a type with no rules, which today descends into nothing and
    /// says nothing — a real gap, and the mildest of the three, because the result is a rule that
    /// does not run rather than one that runs where it should not.
    ///
    /// Listed rather than asserted-unreachable: removing an entry here should mean writing its
    /// coverage, and this test says so out loud.
    /// </remarks>
    private static readonly HashSet<string> NeverReported = new() {
        "VM0007",   // NestedTypeHasNoRules
    };

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
        // Fails in both directions: implementing one of the three without deleting its entry here
        // fails, and letting a fourth descriptor go unreported fails too.
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
