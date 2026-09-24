using CSharpAuthor;
using CSharpAuthor.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ValidationModules.SourceGenerator.Impl.FrontEnds;
using static ValidationModules.SourceGenerator.Impl.Emitters.EmitterOutput;

namespace ValidationModules.SourceGenerator.Impl.Emitters;

/// <summary>
/// Writes the companion files transcription produces: one region method per rules class, and one
/// container of fragment methods per fragment-declaring type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why companions rather than statements in the validator.</b> Transcribed code is the author's
/// source, and it resolves against the using directives of the file that declared it - the
/// validator file deliberately has none. The companion carries the declaring file's usings, so
/// <c>x.Status == Status.Active</c> compiles exactly as it did where it was written. This is the
/// recorded CSharpAuthor exception the predicate lifting established, extended not multiplied:
/// the structure is CSharpAuthor; the statements are the author's source plus the checks expanded
/// from it.
/// </para>
/// <para>
/// <b>Fragments get their own containers for the same reason.</b> A fragment lives in its own file
/// with its own usings; expanding it into the caller's companion would re-create the hazard the
/// companion exists to remove. Each fragment becomes a method in a container carrying the
/// fragment's file usings, one instantiation per concrete target, shared by every caller.
/// </para>
/// <para>
/// The one Roslyn-coupled emitter, because it copies syntax. The others take only the IR.
/// </para>
/// </remarks>
public sealed class RegionEmitter
{
    /// <summary>
    /// Emits the region companion for one rules class - every region it declares, one
    /// <c>Describe</c> overload per target, in one file. One file because two would collide on the
    /// hint name, and <c>AddSource</c> throwing on a collision fails the whole generator rather
    /// than one type.
    /// </summary>
    public string EmitRegion(
        IReadOnlyList<RulesDeclaration> declarations,
        BraceStyle style = BraceStyle.Allman
    )
    {
        var rulesClass = declarations[0].RulesClass;
        var ns = rulesClass.ContainingNamespace;
        var file = GeneratedFile(ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString());

        CopyUsings(file, rulesClass);

        var container = file.AddClass(GeneratedNames.Companion(rulesClass));

        container.Modifiers = ComponentModifier.Internal | ComponentModifier.Static;
        container.Comment =
            declarations.Count == 1
                ? $"The transcribed Describe body of {rulesClass.Name}: read from the rules class, run from here."
                : $"The transcribed Describe bodies of {rulesClass.Name}, one region per target: read from the rules class, run from here.";

        foreach (var declaration in declarations)
        {
            Fields(container, declaration.Fields);
            MessageInfos(container, declaration.MessageInfos);
        }

        foreach (var declaration in declarations)
        {
            // Overloads on the subject type: the validator's call site passes its own value, so
            // resolution lands each target on its own region.
            var method = container.AddMethod("Describe");

            method.Modifiers = ComponentModifier.Public | ComponentModifier.Static;
            method.SetReturnType(TypeDefinition.Get("ValidationModules", "ValidationFlow"));
            method
                .AddParameter(TypeDefinition.Get("ValidationModules", "ValidationContext"), "ctx")
                .Modifier = ParameterModifier.Ref;
            method.AddParameter(SymbolType(declaration.Target), declaration.SubjectParameterName);

            foreach (var dependency in declaration.Dependencies)
            {
                method.AddParameter(
                    ValidatorFor(TypeRef(dependency.ElementQualifiedType)).MakeArray(),
                    dependency.ParameterName
                );
            }

            Body(method, declaration.Body);
            method.Return("global::ValidationModules.ValidationFlow.Continue");
        }

        return Render(file, style);
    }

    /// <summary>Emits one declaring type's fragment methods, or null when it has none.</summary>
    public string? EmitFragments(FragmentContainer fragments, BraceStyle style = BraceStyle.Allman)
    {
        if (fragments.Methods.Count == 0)
        {
            return null;
        }

        var file = GeneratedFile(fragments.Namespace);

        CopyUsings(file, fragments.DeclaringType);

        var container = file.AddClass(fragments.Name);

        container.Modifiers = ComponentModifier.Internal | ComponentModifier.Static;
        container.Comment =
            $"The transcribed fragments of {fragments.DeclaringType.Name}, one method per concrete target.";

        foreach (var fragment in fragments.Methods)
        {
            Fields(container, fragment.Fields);
            MessageInfos(container, fragment.MessageInfos);

            var method = container.AddMethod(fragment.Name);

            method.Modifiers = ComponentModifier.Public | ComponentModifier.Static;
            method.SetReturnType(TypeDefinition.Get("ValidationModules", "ValidationFlow"));
            method
                .AddParameter(TypeDefinition.Get("ValidationModules", "ValidationContext"), "ctx")
                .Modifier = ParameterModifier.Ref;

            if (fragment.Subject is { } subject)
            {
                method.AddParameter(SymbolType(fragment.Target), subject.Name);
            }

            foreach (var extra in fragment.ExtraParameters)
            {
                method.AddParameter(extra.Type.GetTypeDefinition(), extra.Name);
            }

            Body(method, fragment.Body);
            method.Return("global::ValidationModules.ValidationFlow.Continue");
        }

        return Render(file, style);
    }

    /// <summary>
    /// The lazily-built facet validators a region caches: nullable static fields, filled on first
    /// use with the benign race the validator's own nested arrays already accept.
    /// </summary>
    private static void Fields(
        ClassDefinition container,
        IReadOnlyList<FrontEnds.CompanionField> fields
    )
    {
        foreach (var field in fields)
        {
            container.AddField(TypeRef(field.TypeQualified).MakeNullable(), field.Name).Modifiers =
                ComponentModifier.Private | ComponentModifier.Static;
        }
    }

    /// <summary>
    /// The message infos a region hoists for the rules on a labelled property, built once at type
    /// initialization as the validator's own are.
    /// </summary>
    private static void MessageInfos(
        ClassDefinition container,
        IReadOnlyList<(string Field, string Initializer)> infos
    )
    {
        foreach (var (field, initializer) in infos)
        {
            var info = container.AddField(
                TypeDefinition.Get("ValidationModules", "ValidationMessageInfo"),
                field
            );

            info.Modifiers =
                ComponentModifier.Private | ComponentModifier.Static | ComponentModifier.Readonly;
            info.InitializeValue = new CodeOutputComponent(initializer) { Indented = false };
        }
    }

    /// <summary>
    /// The transcribed statements, each block's braces placed by the output context.
    /// </summary>
    private static void Body(BaseBlockDefinition block, IReadOnlyList<RegionStatement> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case RegionBlock nested:
                    Body(block.Add(new TranscribedBlock(nested)), nested.Body);
                    break;

                case RegionCode code:
                    block.Add(new TranscribedCode(code.Text));
                    break;
            }
        }
    }

    /// <summary>A transcribed statement, a line at a time at the current indent.</summary>
    private sealed class TranscribedCode : BaseOutputComponent
    {
        private readonly string _text;

        public TranscribedCode(string text) => _text = text;

        protected override void WriteComponentOutput(IOutputContext output) =>
            WriteLines(output, _text);
    }

    /// <summary>
    /// A transcribed block: its header, then its statements inside braces the output context
    /// places.
    /// </summary>
    /// <remarks>
    /// A bare block has no header for a K&amp;R brace to join, so its braces are on lines of their
    /// own in either style. A switch section has no braces, and its statements are indented under
    /// its labels as <see cref="CaseBlockDefinition"/> indents them.
    /// </remarks>
    private sealed class TranscribedBlock : BaseBlockDefinition
    {
        private readonly RegionBlock _block;

        public TranscribedBlock(RegionBlock block) => _block = block;

        protected override void WriteComponentOutput(IOutputContext output)
        {
            if (_block.Header is not { } header)
            {
                output.WriteIndentedLine("{");
                WriteIndented(output);
                output.WriteIndentedLine("}");
            }
            else if (_block.Braced)
            {
                WriteLines(output, header);
                WriteBlock(output);
            }
            else
            {
                WriteLines(output, header);
                WriteIndented(output);
            }

            if (_block.Footer is { } footer)
            {
                WriteLines(output, footer);
            }
        }

        private void WriteIndented(IOutputContext output)
        {
            output.IncrementIndent();

            foreach (var statement in StatementList)
            {
                statement.WriteOutput(output);
            }

            output.DecrementIndent();
        }
    }

    /// <summary>
    /// Each line at the current indent. An empty line gets no indent, so it carries no trailing
    /// whitespace.
    /// </summary>
    private static void WriteLines(IOutputContext output, string text)
    {
        foreach (var line in LinesOf(text))
        {
            if (line.Length == 0)
            {
                output.WriteLine();
            }
            else
            {
                output.WriteIndentedLine(line);
            }
        }
    }

    /// <summary>
    /// The lines of transcribed text, split only at line breaks between tokens.
    /// </summary>
    /// <remarks>
    /// A line break inside a token, such as a verbatim string's, is part of the token's value. It
    /// does not start a line, so no indent is written into the value.
    /// </remarks>
    private static IReadOnlyList<string> LinesOf(string text)
    {
        if (text.IndexOf('\n') < 0)
        {
            return new[] { text };
        }

        var lines = new List<string>();
        var start = 0;

        foreach (var token in SyntaxFactory.ParseTokens(text))
        {
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    lines.Add(text.Substring(start, trivia.SpanStart - start));
                    start = trivia.Span.End;
                }
            }
        }

        lines.Add(text.Substring(start));

        return lines;
    }

    /// <summary>
    /// From the symbol rather than a display string, so a nested or generic target keeps its shape
    /// through the type model. The simple global-namespace case routes through NamedType for the
    /// same empty-namespace reason the predicate emitter did.
    /// </summary>
    private static ITypeDefinition SymbolType(INamedTypeSymbol type) =>
        type.ContainingNamespace.IsGlobalNamespace && !type.IsGenericType
            ? NamedType(string.Empty, type.Name)
            : type.GetTypeDefinition();

    /// <summary>
    /// The declaring file's usings, copied over, plus <c>ValidationModules</c> itself - reporter
    /// calls transcribe in reduced extension form, which a fully qualified rules class would
    /// otherwise leave unresolvable. Handed to the file as imports so they deduplicate.
    /// </summary>
    private static void CopyUsings(CSharpFileDefinition file, INamedTypeSymbol declaringType)
    {
        file.AddUsingNamespace("ValidationModules");

        foreach (var directive in UsingsOf(declaringType))
        {
            if (ImportOf(directive) is { } import)
            {
                file.AddUsingNamespace(import);
            }
        }
    }

    /// <summary>
    /// What a copied directive imports: the text between <c>using</c> and <c>;</c>, with a
    /// <c>global</c> modifier dropped because it is only legal before namespace declarations and
    /// the plain form imports the same names here.
    /// </summary>
    private static string? ImportOf(string directive)
    {
        var text = directive.Trim();

        if (text.StartsWith("global ", StringComparison.Ordinal))
        {
            text = text.Substring("global ".Length).TrimStart();
        }

        if (!text.StartsWith("using ", StringComparison.Ordinal))
        {
            return null;
        }

        text = text.Substring("using ".Length).TrimStart();

        if (text.EndsWith(";", StringComparison.Ordinal))
        {
            text = text.Substring(0, text.Length - 1).TrimEnd();
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Every using directive in scope at the declaring type, innermost namespace outwards.
    /// </summary>
    private static IEnumerable<string> UsingsOf(INamedTypeSymbol declaringType)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in declaringType.DeclaringSyntaxReferences)
        {
            var node = reference.GetSyntax();

            for (var current = node; current is not null; current = current.Parent)
            {
                var directives = current switch
                {
                    BaseNamespaceDeclarationSyntax declaration => declaration.Usings,
                    CompilationUnitSyntax unit => unit.Usings,
                    _ => default,
                };

                foreach (var directive in directives)
                {
                    var text = directive.ToString();

                    if (seen.Add(text))
                    {
                        yield return text;
                    }
                }
            }
        }
    }
}
