namespace ValidationModules.SourceGenerator.Impl.Models;

/// <summary>One translated template: a stable key and the text that renders for it.</summary>
/// <param name="Key">A wire code, or a shape key beneath one - <c>string_length.at_most</c>.</param>
/// <param name="Template">The template, holes included, exactly as authored.</param>
public readonly record struct LanguagePackEntry(string Key, string Template);

/// <summary>
/// One <c>*.validation-messages.json</c> file, read and validated: what the pack emitter turns
/// into a sealed <c>IValidationLanguagePack</c> and the registration emitter registers.
/// </summary>
/// <param name="Culture">The culture the file declared - <c>"fr"</c>, <c>"zh"</c>.</param>
/// <param name="ClassName">
/// The emitted class - culture plus a per-file index (<c>FrLanguagePack0</c>), because one
/// assembly may carry several files for one culture and each stays its own layer.
/// </param>
/// <param name="HintName">The generated file's hint name, unique per file.</param>
/// <param name="Entries">In file order, which the layering rule deliberately never depends on -
/// only registration order across packs matters.</param>
public sealed record LanguagePackModel(
    string Culture,
    string ClassName,
    string HintName,
    EquatableArray<LanguagePackEntry> Entries);

/// <summary>An additional file the pack pipeline considers: its path and its text.</summary>
public sealed record LanguagePackFile(string Path, string Content);
