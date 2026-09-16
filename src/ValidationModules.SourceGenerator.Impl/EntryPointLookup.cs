using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ValidationModules.SourceGenerator.Impl;

/// <summary>
/// A module entry point declared in this compilation, reduced to what the emitter needs.
/// </summary>
/// <remarks>
/// Strings and a bool rather than a symbol, so the value survives an incremental-generator
/// boundary and compares by value. A record struct gets that comparison for free, which is what
/// keeps the registration stage from re-running on every keystroke.
/// </remarks>
public readonly record struct ModuleEntryPoint(string Namespace, string Name, bool IsRecord)
{
    public string QualifiedName => Namespace.Length == 0 ? Name : Namespace + "." + Name;
}

/// <summary>
/// Finds the classes an assembly's validators should register into.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the entry point rather than a module beside it.</b> DependencyModules keys registrations
/// on the entry point type - <c>DependencyRegistry&lt;TEntryPoint&gt;</c> - and composes sibling
/// modules only through attributes someone writes. A generator cannot write one of those: the
/// attribute a module is composed with is itself generated, by DependencyModules' generator, and
/// generators cannot see each other's output. So a module emitted next to the entry point is never
/// loaded, and a partial of the entry point is.
/// </para>
/// <para>
/// <b>Why the attribute names are literals.</b> The honest test is "carries an attribute
/// implementing <c>IDependencyModuleProvider</c>", which is how DependencyModules recognises a
/// referenced module. It does not answer here. <c>[DependencyModule]</c> and
/// <c>[HardenedModule]</c> are both plain <c>Attribute</c> subclasses - the provider interface is
/// on the attribute a module <em>generates</em>, not on the one that marks it - so the two shapes
/// that matter are exactly these two names and nothing else resolves them.
/// </para>
/// <para>
/// <b>Why not DependencyModules' own reader.</b> These sources are compiled into
/// <c>Hardened.Validation.SourceGenerator</c>, which deliberately references no part of
/// DependencyModules, so nothing here may name a type from its generator or its runtime. The
/// names below are written as strings for the same reason <see cref="KnownTypes"/> is.
/// </para>
/// </remarks>
public static class EntryPointLookup
{
    /// <summary>Marks a DependencyModules module.</summary>
    public const string DependencyModuleAttribute =
        "DependencyModules.Runtime.Attributes.DependencyModuleAttribute";

    /// <summary>
    /// Marks a Hardened application or library, which is a DependencyModules module built from a
    /// different attribute: <c>HardenedSourceGenerator</c> names this one where
    /// DependencyModules' own generator names the one above.
    /// </summary>
    public const string HardenedModuleAttribute =
        "Hardened.Shared.Runtime.Attributes.HardenedModuleAttribute";

    /// <summary>
    /// The syntax filter, which has to stay cheap: it runs for every node in the compilation.
    /// </summary>
    /// <remarks>
    /// A record struct is excluded rather than accepted and dropped later. The partial this
    /// generator writes is a <c>class</c>, and a struct cannot be completed by one.
    /// </remarks>
    public static bool IsCandidate(SyntaxNode node) =>
        node switch
        {
            RecordDeclarationSyntax record => record.AttributeLists.Count > 0
                && !record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword),
            ClassDeclarationSyntax type => type.AttributeLists.Count > 0,
            _ => false,
        };

    /// <summary>
    /// The entry point <paramref name="context"/> declares, or null when it declares none this
    /// generator can register into.
    /// </summary>
    public static ModuleEntryPoint? Read(GeneratorSyntaxContext context, CancellationToken token)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;

        // Not partial means the developer's own declaration is the whole type, and a second
        // declaration of it is CS0260 in their build. DependencyModules reports that itself; this
        // stays quiet and emits nothing rather than adding a second error to the same line.
        if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return null;
        }

        if (
            context.SemanticModel.GetDeclaredSymbol(declaration, token)
            is not INamedTypeSymbol symbol
        )
        {
            return null;
        }

        // A module nested in another type cannot be completed from a namespace-level partial, so
        // the registration would land on a second, detached class. DependencyModules reports the
        // nesting; there is nothing useful to emit for it either way.
        if (symbol.ContainingType is not null || !DeclaresAModule(symbol))
        {
            return null;
        }

        return new ModuleEntryPoint(
            symbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : symbol.ContainingNamespace.ToDisplayString(),
            symbol.Name,
            symbol.IsRecord
        );
    }

    private static bool DeclaresAModule(INamedTypeSymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeClass)
            {
                continue;
            }

            var name = attributeClass.ToDisplayString();

            if (
                string.Equals(name, DependencyModuleAttribute, StringComparison.Ordinal)
                || string.Equals(name, HardenedModuleAttribute, StringComparison.Ordinal)
            )
            {
                return true;
            }
        }

        return false;
    }
}
