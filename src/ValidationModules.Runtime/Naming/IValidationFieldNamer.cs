namespace ValidationModules.Naming;

/// <summary>
/// Turns CLR property names into the field names that appear in errors, and joins them into paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>Naming is a generation-time decision that the runtime can read back.</b> The generated engine
/// bakes field names in as string literals and never calls a namer per validation. This interface
/// exists so that engines which cannot do that - the FluentValidation adapter, which receives
/// <c>Home.PostalCode</c> at runtime - apply the same policy, rather than emitting a different path
/// shape for the same field.
/// </para>
/// <para>
/// The generator picks the policy from the <c>ValidationModules_FieldNaming</c> MSBuild property
/// and emits which one it used, so the adapter can resolve the matching namer instead of guessing.
/// </para>
/// </remarks>
public interface IValidationFieldNamer {

    /// <summary>
    /// Converts one CLR property name - <c>PostalCode</c> - to its field name - <c>postalCode</c>.
    /// </summary>
    string ToFieldName(string clrPropertyName);

    /// <summary>
    /// Appends a field to a path. <c>("home", "postalCode")</c> becomes <c>home.postalCode</c>.
    /// </summary>
    /// <param name="parentPath">The path so far. Empty at the root.</param>
    /// <param name="fieldName">The field being appended, already converted.</param>
    string Combine(string parentPath, string fieldName);

    /// <summary>
    /// Appends an indexed field to a path. <c>("", "toys", 3)</c> becomes <c>toys[3]</c>.
    /// </summary>
    /// <param name="parentPath">The path so far. Empty at the root.</param>
    /// <param name="fieldName">The collection field, already converted.</param>
    /// <param name="index">The element's position.</param>
    string CombineIndex(string parentPath, string fieldName, int index);
}
