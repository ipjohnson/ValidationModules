using System.Text;
using CSharpAuthor;
using Microsoft.CodeAnalysis.CSharp;
using ValidationModules.SourceGenerator.Impl.Models;
using static ValidationModules.SourceGenerator.Impl.Emitters.EmitterOutput;

namespace ValidationModules.SourceGenerator.Impl.Emitters;

/// <summary>
/// Emits one language pack: a sealed <c>IValidationLanguagePack</c> whose entries are one
/// <c>static readonly</c> array of pairs - pure data, boxed nowhere, initialized from literals
/// the string heap deduplicates across every pack in the assembly, and trimmed to nothing when
/// nothing registers it.
/// </summary>
/// <remarks>
/// A pack deliberately answers no lookups: <c>LanguagePackFormatter</c> enumerates every
/// registered pack once into a merged table per requested culture, so per-error reads are
/// constant-time however many packs and assemblies contribute. The first cut emitted a
/// per-pack lookup chain instead; the storage benchmarks
/// (<c>benchmarks/…/Design/LanguagePackStorageBenchmarks.cs</c>) retired it - linear degradation
/// against flat - and docs/language-packs.md records the decision. Storage remains this
/// emitter's implementation detail: the authoring format stays a data file whatever this becomes.
/// </remarks>
public sealed class LanguagePackEmitter {

    private static readonly ITypeDefinition PackInterface = TypeDefinition.Get(
        TypeDefinitionEnum.InterfaceDefinition, "ValidationModules", "IValidationLanguagePack");

    private const string Pair = "global::System.Collections.Generic.KeyValuePair<string, string>";

    /// <param name="model">The pack to emit, already read and validated.</param>
    /// <param name="assemblyNamespace">The sanitized assembly namespace the class lands in.</param>
    /// <param name="style">Where the braces go, from the shared build property.</param>
    public string Emit(LanguagePackModel model, string assemblyNamespace, BraceStyle style = BraceStyle.Allman) {
        var file = new CSharpFileDefinition();

        Header(file);

        var ns = new NamespaceDefinition(assemblyNamespace);

        file.AddComponent(ns);

        var pack = ns.AddClass(model.ClassName);

        pack.Modifiers = ComponentModifier.Internal | ComponentModifier.Sealed;
        pack.Comment =
            $"The '{model.Culture}' message templates this assembly compiled from a\n" +
            "*.validation-messages.json file. Keys are the stable vocabulary - wire codes and\n" +
            "shape keys - never wording, which is why rewording a default breaks no pack.";
        pack.AddBaseType(PackInterface);

        var entries = pack.AddField(
            TypeDefinition.Get(typeof(KeyValuePair<string, string>)).MakeArray(), "Entries");

        entries.Modifiers = ComponentModifier.Private | ComponentModifier.Static | ComponentModifier.Readonly;
        entries.InitializeValue = new CodeOutputComponent(EntriesInitializer(model)) { Indented = false };

        var culture = pack.AddProperty(typeof(string), "Culture");

        culture.Set = null;
        culture.Get.LambdaSyntax = true;
        culture.Get.AddIndentedStatement(Literal(model.Culture));

        var templates = pack.AddProperty(
            TypeDefinition.Get(typeof(IReadOnlyList<KeyValuePair<string, string>>)), "Templates");

        templates.Set = null;
        templates.Get.LambdaSyntax = true;
        templates.Get.AddIndentedStatement("Entries");

        return Render(file, style);
    }

    /// <summary>
    /// The array initializer, one pair per line: readable in the emitted file, and every string a
    /// literal so identical keys across an assembly's packs share one heap entry.
    /// </summary>
    private static string EntriesInitializer(LanguagePackModel model) {
        var builder = new StringBuilder();

        builder.Append("new ").Append(Pair).Append("[]\n        {\n");

        foreach (var entry in model.Entries) {
            builder
                .Append("            new(")
                .Append(Literal(entry.Key))
                .Append(", ")
                .Append(Literal(entry.Template))
                .Append("),\n");
        }

        builder.Append("        }");

        return builder.ToString();
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
