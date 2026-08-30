namespace ValidationModules.Constraints;

/// <summary>
/// The string names a file whose extension is one of a permitted set. Emits code
/// <c>file_extension</c>.
/// </summary>
/// <remarks>
/// <para>
/// The name - plural, the BCL's own slightly awkward spelling - the <see cref="Extensions"/>
/// property and its default set are all <c>System.ComponentModel.DataAnnotations</c>' own, so
/// migrating a model is swapping a using directive. The set is normalized at build time exactly as
/// the BCL attribute normalizes it - spaces and dots removed, lowercased invariantly, split on
/// commas - so its quirks survive: an entry of <c>tar.gz</c> reads as <c>.targz</c> in both.
/// </para>
/// <para>
/// The comparison is case-insensitive and allocation-free - see
/// <c>ConstraintChecks.HasFileExtension</c>, where the semantics are pinned against the BCL
/// attribute.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [FileExtensions(Extensions = "pdf,docx")] public string? Attachment { get; init; }
/// </code>
/// </example>
public sealed class FileExtensionsAttribute : ValidationConstraintAttribute {

    /// <summary>
    /// The permitted extensions, comma-separated, dots optional. Defaults to the BCL's own set:
    /// <c>png,jpg,jpeg,gif</c>.
    /// </summary>
    public string? Extensions { get; init; }
}
