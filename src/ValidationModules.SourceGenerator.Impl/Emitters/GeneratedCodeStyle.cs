using CSharpAuthor;

namespace ValidationModules.SourceGenerator.Impl.Emitters;

/// <summary>
/// The <c>GeneratedCodeStyle</c> MSBuild property: which brace style generated files are written
/// in.
/// </summary>
/// <remarks>
/// <para>
/// The name carries no <c>ValidationModules_</c> prefix on purpose - it is shared with other
/// source generators (DependencyModules reads the same property), so one csproj line styles all of
/// them. The parse below matches DependencyModules'
/// <c>BaseSourceGenerator.GetCodeStyle</c> exactly, because the two answering differently for the
/// same value would style one project's generated code two ways.
/// </para>
/// <para>
/// Public rather than internal: framework authors compile Impl in and drive the emitters from
/// their own generators, and they read the property themselves.
/// </para>
/// </remarks>
public static class GeneratedCodeStyle {

    /// <summary>The key <c>AnalyzerConfigOptions.GlobalOptions</c> exposes the property under.</summary>
    public const string BuildProperty = "build_property.GeneratedCodeStyle";

    /// <summary>
    /// <c>KAndR</c> (also accepted as <c>K&amp;R</c>), case-insensitively; anything else -
    /// including absent and misspelled - is <see cref="BraceStyle.Allman"/>. Falling back rather
    /// than diagnosing matches DependencyModules, and the property only moves braces: a wrong
    /// value cannot change what the generated code does.
    /// </summary>
    public static BraceStyle Parse(string? value) {
        if (value is null) {
            return BraceStyle.Allman;
        }

        switch (value.Trim().ToLowerInvariant()) {
            case "kandr":
            case "k&r":
                return BraceStyle.KAndR;
            default:
                return BraceStyle.Allman;
        }
    }
}
