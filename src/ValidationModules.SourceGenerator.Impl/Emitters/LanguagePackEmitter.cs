using CSharpAuthor;
using Microsoft.CodeAnalysis.CSharp;
using ValidationModules.SourceGenerator.Impl.Models;
using static ValidationModules.SourceGenerator.Impl.Emitters.EmitterOutput;

namespace ValidationModules.SourceGenerator.Impl.Emitters;

/// <summary>
/// Emits one language pack: a sealed <c>IValidationLanguagePack</c> whose <c>Template</c> is a
/// chain of literal returns - zero static initialization, no dictionary object, literals
/// deduplicated in the metadata heap, and a pack nothing registers trims to nothing. Storage is an
/// implementation detail of this emitter (docs/language-packs.md): the authoring format stays a
/// data file whatever this becomes.
/// </summary>
public sealed class LanguagePackEmitter {

    private static readonly ITypeDefinition PackInterface = TypeDefinition.Get(
        TypeDefinitionEnum.InterfaceDefinition, "ValidationModules", "IValidationLanguagePack");

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

        var culture = pack.AddProperty(typeof(string), "Culture");

        culture.Set = null;
        culture.Get.LambdaSyntax = true;
        culture.Get.AddIndentedStatement(Literal(model.Culture));

        var template = pack.AddMethod("Template");

        template.SetReturnType(TypeDefinition.Get(typeof(string)).MakeNullable());
        template.AddParameter(TypeDefinition.Get(typeof(string)), "key");

        foreach (var entry in model.Entries) {
            template.If($"key == {Literal(entry.Key)}").Return(Literal(entry.Template));
        }

        template.Return("null");

        return Render(file, style);
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
