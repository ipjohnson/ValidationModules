using Microsoft.CodeAnalysis;

namespace ValidationModules.SourceGenerator.Impl;

/// <summary>
/// The names of the classes the generator emits for a type: its validator, a rules class's
/// companion and a fragment container. Each class is declared in one file and referenced from
/// others, so both sides take the name from here.
/// </summary>
/// <remarks>
/// A nested type's names carry its containing types' names, so <c>Order.Item</c> gets
/// <c>Order_ItemValidator</c>. The simple name alone gave <c>Order.Item</c> and
/// <c>Invoice.Item</c> the same validator name in one namespace, and the duplicate hint name failed
/// the generator. The name depends only on the type's own declaration, so declaring another type
/// never renames an existing validator. The cost is that <c>Order.Item</c> and a top-level
/// <c>Order_Item</c> still share a name, which VM1013 reports.
/// </remarks>
internal static class GeneratedNames
{
    /// <summary>
    /// The type's name after its containing types' names, joined with underscores:
    /// <c>Outer_Middle_Inner</c>. A top-level type's is its simple name.
    /// </summary>
    public static string Flattened(INamedTypeSymbol type) =>
        type.ContainingType is { } containing ? $"{Flattened(containing)}_{type.Name}" : type.Name;

    public static string Validator(INamedTypeSymbol type) => $"{Flattened(type)}Validator";

    /// <summary>The validator's name qualified by its type's namespace, as generated code names it.</summary>
    public static string QualifiedValidator(INamedTypeSymbol type) =>
        type.ContainingNamespace.IsGlobalNamespace
            ? $"global::{Validator(type)}"
            : $"global::{type.ContainingNamespace.ToDisplayString()}.{Validator(type)}";

    /// <summary>The class a rules class's transcribed regions are emitted into.</summary>
    public static string Companion(INamedTypeSymbol rulesClass) => $"{Flattened(rulesClass)}_Rules";

    /// <summary>The class the fragments a type declares are emitted into.</summary>
    public static string FragmentContainer(INamedTypeSymbol declaringType) =>
        $"{Flattened(declaringType)}_Fragments";
}
