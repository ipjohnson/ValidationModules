using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The root <c>README.md</c> is packed into every shipping package, and nuget.org renders it as
/// CommonMark with no raw HTML. These are the two rules that follow from that.
/// </summary>
/// <remarks>
/// <para>
/// Both were broken for fourteen release candidates, in the one file nothing tested. A
/// <c>&lt;picture&gt;</c> element for a theme-switched logo arrived on the package page as its
/// own literal markup, printed across the title; a <c>](LICENSE.txt)</c> link became
/// <c>href=""</c>. Neither shows up in a build, in the docs site (VitePress renders HTML), or in
/// a GitHub preview - only on the package page, after publishing, where a version can be
/// unlisted but never replaced.
/// </para>
/// <para>
/// The rules are deliberately narrow. HTML inside a fenced code block is displayed rather than
/// interpreted, so C# generics and XML in samples are fine, and the anchor form is allowed
/// because nuget.org generates heading ids - the anchor test below checks each one resolves.
/// </para>
/// </remarks>
public class PackageReadmeTests {

    /// <summary>
    /// The README with fenced code blocks and HTML comments removed: what nuget.org actually
    /// interprets. Comments are dropped by the renderer, and a fence's contents are shown as
    /// text.
    /// </summary>
    private static string Interpreted() {
        var text = Readme();
        var withoutFences = Regex.Replace(text, "```.*?```", string.Empty, RegexOptions.Singleline);

        return Regex.Replace(withoutFences, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
    }

    [Fact]
    public void TheReadme_CarriesNoRawHtml() {
        // <https://…> is CommonMark's autolink, not an HTML tag.
        var tags = Regex.Matches(Interpreted(), @"</?[a-zA-Z][^>\n]*>")
            .Select(match => match.Value)
            .Where(tag => !tag.StartsWith("<http", StringComparison.Ordinal))
            .ToList();

        Assert.True(tags.Count == 0,
            "nuget.org escapes raw HTML into visible text on the package page. Use CommonMark " +
            $"instead:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", tags)}");
    }

    [Fact]
    public void EveryLink_IsAbsoluteOrAResolvingAnchor() {
        var body = Interpreted();
        var links = Regex.Matches(body, @"!?\]\(([^)]+)\)")
            .Select(match => match.Groups[1].Value)
            .ToList();

        // A relative path has nothing to resolve against on nuget.org: the link renders with an
        // empty href, which is how the licence badge pointed at nothing.
        var relative = links
            .Where(link => !link.StartsWith("https://", StringComparison.Ordinal))
            .Where(link => !link.StartsWith("#", StringComparison.Ordinal))
            .ToList();

        Assert.True(relative.Count == 0,
            "nuget.org resolves no relative path; link to the absolute URL on github.com " +
            $"instead:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", relative)}");

        // Anchors are fine, but only to a heading that exists - nuget.org slugifies headings the
        // same way, so a stale anchor is a dead link on the package page.
        var headings = Regex.Matches(Readme(), "^#+ (.+)$", RegexOptions.Multiline)
            .Select(match => Regex.Replace(match.Groups[1].Value.ToLowerInvariant(), "[^a-z0-9 -]", string.Empty)
                .Trim()
                .Replace(' ', '-'))
            .ToHashSet(StringComparer.Ordinal);

        var dangling = links
            .Where(link => link.StartsWith("#", StringComparison.Ordinal))
            .Where(link => !headings.Contains(link.Substring(1)))
            .ToList();

        Assert.True(dangling.Count == 0,
            $"Anchors naming no heading:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", dangling)}");
    }

    /// <summary>
    /// The header mark has to be a file that needs no sizing and no theme switching, because the
    /// README can express neither. <c>assets/logo-readme.svg</c> is that file: intrinsic size,
    /// its own background.
    /// </summary>
    [Fact]
    public void TheHeaderImage_IsTheSelfContainedReadmeMark() {
        var image = Assert.Single(
            Regex.Matches(Interpreted(), @"!\[[^\]]*\]\(([^)]+)\)").Select(match => match.Groups[1].Value),
            url => url.Contains("/assets/", StringComparison.Ordinal));

        Assert.EndsWith("/assets/logo-readme.svg", image, StringComparison.Ordinal);
        Assert.StartsWith("https://raw.githubusercontent.com/", image, StringComparison.Ordinal);

        var mark = Path.Combine(RepositoryRoot, "assets", "logo-readme.svg");
        Assert.True(File.Exists(mark), $"{mark} is referenced by the README and does not exist.");

        // Intrinsic width and height, since the README cannot supply them. The width follows the
        // viewBox aspect ratio, so it is not always whole.
        var svg = File.ReadAllText(mark);
        Assert.Matches(@"<svg[^>]*\swidth=""\d+(\.\d+)?""", svg);
        Assert.Matches(@"<svg[^>]*\sheight=""\d+(\.\d+)?""", svg);
    }

    /// <summary>Alt text on every image: nuget.org prints a placeholder in its place.</summary>
    [Fact]
    public void EveryImage_CarriesAltText() {
        var untexted = Regex.Matches(Interpreted(), @"!\[([^\]]*)\]\(([^)]+)\)")
            .Where(match => match.Groups[1].Value.Trim().Length == 0)
            .Select(match => match.Groups[2].Value)
            .ToList();

        Assert.True(untexted.Count == 0,
            "nuget.org replaces missing alt text with \"alternate text is missing from this " +
            $"package README image\":{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", untexted)}");
    }

    private static string Readme() => File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));

    private static string RepositoryRoot { get; } = ResolveRepositoryRoot();

    private static string ResolveRepositoryRoot() {
        var configured = typeof(PackageReadmeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepositoryRoot")?.Value;

        if (configured is null || !File.Exists(Path.Combine(configured, "README.md"))) {
            throw new InvalidOperationException(
                "RepositoryRoot assembly metadata is missing or does not contain README.md.");
        }

        return configured;
    }
}
