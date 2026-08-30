namespace ValidationModules.SourceGenerator.Impl;

/// <summary>
/// Applies the assembly's <c>ValidationModules_CodeNamespace</c> to the codes it owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authored and derived codes only.</b> The built-in vocabulary is never prefixed.
/// <c>required</c> has to stay <c>required</c> in every assembly, because a fixed vocabulary is
/// what lets a client switch on a code without knowing which engine produced the error, and
/// namespacing it would defeat the point of having one. What collides across assemblies is the
/// codes people invent, which is exactly what this covers.
/// </para>
/// <para>
/// <b>Opt-in, and a once-per-assembly decision.</b> Switching it on changes every code the
/// assembly emits, which is a wire-contract change for anything reading them.
/// </para>
/// <para>
/// The separator is a dot: it survives being a JSON key and a URL fragment untouched, and it is
/// the separator a .NET audience already reads as namespacing.
/// </para>
/// </remarks>
internal static class CodeNaming {

    /// <summary>The MSBuild property, spelled once.</summary>
    public const string BuildProperty = "build_property.ValidationModules_CodeNamespace";

    /// <summary>
    /// <paramref name="code"/> under <paramref name="codeNamespace"/>, or unchanged when no
    /// namespace was set or the code already carries it.
    /// </summary>
    public static string? Apply(string? codeNamespace, string? code) {
        if (code is null || string.IsNullOrWhiteSpace(codeNamespace)) {
            return code;
        }

        var prefix = codeNamespace!.Trim();

        // Idempotent, so a code read back through a second pass is not prefixed twice.
        return code.StartsWith(prefix + ".", System.StringComparison.Ordinal)
            ? code
            : prefix + "." + code;
    }
}
