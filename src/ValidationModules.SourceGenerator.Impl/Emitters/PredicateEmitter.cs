using CSharpAuthor;
using CSharpAuthor.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ValidationModules.SourceGenerator.Impl.FrontEnds;
using static ValidationModules.SourceGenerator.Impl.Emitters.EmitterOutput;

namespace ValidationModules.SourceGenerator.Impl.Emitters;

/// <summary>
/// Lifts a rules class's <c>Ensure</c> predicates into static methods the validator can call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why lift rather than inline.</b> A predicate's source resolves against the using directives of
/// the file that declared it - <c>x =&gt; x.Status == Status.Active</c> needs the <c>using</c> that
/// was in the author's file - and the validator is emitted into a different file that does not have
/// them. The alternatives were a symbol-qualifying rewriter, which has to reduce extension-method
/// invocations to their static form to be correct, or this: emit one file per rules class carrying
/// that file's own usings, and call into it. The call costs nothing an inliner will not remove, and
/// the predicate stays readable in <c>obj/…/generated</c>, which HANDOFF.md §3.5 leans on.
/// </para>
/// <para>
/// This is also the honest account of what <c>Ensure</c> is: sugar over <c>Apply</c>, where the
/// generator writes the static method the author would otherwise have written by hand.
/// </para>
/// <para>
/// The predicate bodies and the copied usings are the deliberate exception to the no-usings,
/// everything-<c>global::</c> posture the other emitters hold: they are the author's source, and
/// resolving it the way the author's file did is the whole point of this file. The container's own
/// structure - the class, the signatures, the target type - still goes through the type model, with
/// the target converted from its symbol so a generic or nested target is spelled correctly rather
/// than parsed back out of a display string.
/// </para>
/// <para>
/// The one Roslyn-coupled emitter, because it copies syntax. The others take only the IR.
/// </para>
/// </remarks>
public sealed class PredicateEmitter {

    /// <summary>The class name predicates from <paramref name="rulesClass"/> are lifted into.</summary>
    public static string ContainerFor(INamedTypeSymbol rulesClass) => $"{rulesClass.Name}_Rules";

    /// <summary>Emits the container, or null when the rules class declared no predicates.</summary>
    public string? Emit(RulesDeclaration declaration) {
        if (declaration.Predicates.Count == 0) {
            return null;
        }

        var ns = declaration.RulesClass.ContainingNamespace;
        var file = GeneratedFile(ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString());

        // The declaring file's usings, copied over. This is the whole reason the file exists;
        // without them a predicate naming any type by its short name fails to compile, in generated
        // code, which plan §7.5 calls the worst possible place for an error to land. The directives
        // are handed to the file as imports rather than written as text, so they are deduplicated
        // with anything else the file asks for; static and alias forms ride along inside the
        // directive's own text.
        foreach (var directive in UsingsOf(declaration.RulesClass)) {
            if (ImportOf(directive) is { } import) {
                file.AddUsingNamespace(import);
            }
        }

        var container = file.AddClass(ContainerFor(declaration.RulesClass));

        container.Modifiers = ComponentModifier.Internal | ComponentModifier.Static;
        container.Comment = $"The Ensure predicates of {declaration.RulesClass.Name}, as static methods.";

        // From the symbol rather than a display string, so a nested or generic target keeps its
        // shape through the type model instead of being parsed back out of its own rendering. The
        // bridge shares TypeDefinition's empty-namespace gap - a global-namespace type renders
        // bare, which the enclosing namespace could capture - so the simple case routes through
        // NamedType; a generic global-namespace target keeps the bridge's spelling.
        var target = declaration.Target.ContainingNamespace.IsGlobalNamespace
                     && !declaration.Target.IsGenericType
            ? NamedType(string.Empty, declaration.Target.Name)
            : declaration.Target.GetTypeDefinition();

        foreach (var predicate in declaration.Predicates) {
            var parameter = ParameterOf(predicate.Lambda) ?? "value";
            var method = container.AddMethod(predicate.MethodName);

            method.Modifiers = ComponentModifier.Public | ComponentModifier.Static;
            method.SetReturnType(typeof(bool));
            method.AddParameter(target, parameter);
            method.LambdaSyntax = true;
            method.Return(predicate.Body);
        }

        return Render(file);
    }

    /// <summary>
    /// What a copied directive imports: the text between <c>using</c> and <c>;</c>, with a
    /// <c>global</c> modifier dropped because it is only legal before namespace declarations and
    /// the plain form imports the same names here.
    /// </summary>
    private static string? ImportOf(string directive) {
        var text = directive.Trim();

        if (text.StartsWith("global ", StringComparison.Ordinal)) {
            text = text.Substring("global ".Length).TrimStart();
        }

        if (!text.StartsWith("using ", StringComparison.Ordinal)) {
            return null;
        }

        text = text.Substring("using ".Length).TrimStart();

        if (text.EndsWith(";", StringComparison.Ordinal)) {
            text = text.Substring(0, text.Length - 1).TrimEnd();
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Every using directive in scope at the rules class, innermost namespace outwards.
    /// </summary>
    private static IEnumerable<string> UsingsOf(INamedTypeSymbol rulesClass) {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in rulesClass.DeclaringSyntaxReferences) {
            var node = reference.GetSyntax();

            for (var current = node; current is not null; current = current.Parent) {
                var directives = current switch {
                    BaseNamespaceDeclarationSyntax declaration => declaration.Usings,
                    CompilationUnitSyntax unit => unit.Usings,
                    _ => default,
                };

                foreach (var directive in directives) {
                    var text = directive.ToString();

                    if (seen.Add(text)) {
                        yield return text;
                    }
                }
            }
        }
    }

    private static string? ParameterOf(ExpressionSyntax lambda) => lambda switch {
        SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.Text,
        ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1
            => parenthesized.ParameterList.Parameters[0].Identifier.Text,
        _ => null,
    };

}
