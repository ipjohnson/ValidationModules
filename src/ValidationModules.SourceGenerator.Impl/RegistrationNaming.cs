namespace ValidationModules.SourceGenerator.Impl;

/// <summary>
/// The identifier the registration surface derives from an assembly name, shared by the
/// registration emitter (<c>Add{Identifier}Validators</c>, <c>{Identifier}ValidationExtensions</c>)
/// and the rules front end's facet-miss message, which names the same method.
/// </summary>
/// <remarks>
/// PascalCase per segment, splitting on a namespace's dots and on the underscores sanitization
/// introduces: <c>app2-signupapi</c> sanitizes to <c>app2_signupapi</c> and names
/// <c>AddApp2SignupapiValidators</c>, where collapsing separators alone produced
/// <c>Addapp2_signupapiValidators</c>. An already-Pascal <c>My.App</c> still names
/// <c>AddMyAppValidators</c>. The method name is public generated API, so this was decided before
/// 1.0.0 - the last moment the change is cheap.
/// </remarks>
internal static class RegistrationNaming {

    /// <summary>
    /// The single identifier for a sanitized assembly namespace - letters, digits, underscores
    /// and dots, which is what <c>SanitizeNamespace</c> guarantees.
    /// </summary>
    public static string Identifier(string assemblyNamespace) {
        var builder = new System.Text.StringBuilder(assemblyNamespace.Length);
        var startOfSegment = true;

        foreach (var character in assemblyNamespace) {
            if (character is '.' or '_') {
                startOfSegment = true;
                continue;
            }

            builder.Append(startOfSegment ? char.ToUpperInvariant(character) : character);
            startOfSegment = false;
        }

        if (builder.Length == 0) {
            return "Generated";
        }

        // Dropping the separators can surface a leading digit, which the extension class's name
        // cannot begin with; restore the underscore sanitization would have supplied.
        if (char.IsDigit(builder[0])) {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }
}
