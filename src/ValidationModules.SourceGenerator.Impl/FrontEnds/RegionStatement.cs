using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// A statement in a transcribed region: code written as it reads, or a block of statements.
/// </summary>
/// <remarks>
/// The text carries no braces and no indentation. <see cref="Emitters.RegionEmitter"/> writes both
/// through the output context, so a region follows <c>GeneratedCodeStyle</c> as the rest of its
/// file does.
/// </remarks>
public abstract class RegionStatement { }

/// <summary>
/// A statement written as it reads, such as a declaration, a call or a <c>return</c>.
/// </summary>
public sealed class RegionCode : RegionStatement
{
    public RegionCode(string text) => Text = text;

    /// <summary>The statement with its own <c>;</c>. It can run over several lines.</summary>
    public string Text { get; }
}

/// <summary>A block of statements and the header that introduces it.</summary>
public sealed class RegionBlock : RegionStatement
{
    public RegionBlock(string? header, bool braced = true)
    {
        Header = header;
        Braced = braced;
    }

    /// <summary>
    /// What introduces the block, such as <c>if (x.Age &gt; 1)</c>, <c>else</c> or a local
    /// function's signature. Null for a bare block.
    /// </summary>
    public string? Header { get; }

    /// <summary>
    /// False for a switch section. Its header is its labels, one to a line, and its statements are
    /// indented under them without braces.
    /// </summary>
    public bool Braced { get; }

    /// <summary>
    /// What follows the closing brace, which is the <c>while (...);</c> of a <c>do</c>.
    /// </summary>
    public string? Footer { get; set; }

    public List<RegionStatement> Body { get; } = new();
}

/// <summary>
/// Rewritten statement syntax as region statements: a block for every construct that holds
/// statements, and the normalized text of every other statement.
/// </summary>
/// <remarks>
/// The reader interprets a Describe body's own control flow, because rules can stand inside it.
/// This covers what it copies whole, which is a local function and everything inside one.
/// </remarks>
internal static class RegionSyntax
{
    public static void Add(List<RegionStatement> into, StatementSyntax statement)
    {
        var node = (StatementSyntax)statement.NormalizeWhitespace("    ", "\n");
        var text = node.ToFullString();

        // A directive's #if and #endif can sit on tokens that land in different statements, so a
        // statement holding one is kept whole.
        if (node.ContainsDirectives)
        {
            into.Add(new RegionCode(text));
            return;
        }

        switch (node)
        {
            case BlockSyntax block:
                Nest(into, null, block);
                return;

            case IfStatementSyntax conditional:
                Nest(into, Header(text, 0, conditional.Statement), conditional.Statement);

                for (var clause = conditional.Else; clause is not null; )
                {
                    var start = clause.ElseKeyword.SpanStart;

                    if (clause.Statement is IfStatementSyntax chained)
                    {
                        Nest(into, Header(text, start, chained.Statement), chained.Statement);
                        clause = chained.Else;
                    }
                    else
                    {
                        Nest(into, Header(text, start, clause.Statement), clause.Statement);
                        clause = null;
                    }
                }

                return;

            case DoStatementSyntax loop:
                Nest(into, Header(text, 0, loop.Statement), loop.Statement).Footer = text.Substring(
                    loop.WhileKeyword.SpanStart,
                    loop.SemicolonToken.Span.End - loop.WhileKeyword.SpanStart
                );
                return;

            case SwitchStatementSyntax dispatch:
            {
                var block = new RegionBlock(
                    text.Substring(0, dispatch.OpenBraceToken.GetPreviousToken().Span.End)
                );

                foreach (var section in dispatch.Sections)
                {
                    var labels = section.Labels.Select(label =>
                        label.NormalizeWhitespace("    ", "\n").ToFullString().TrimEnd()
                    );
                    var arm = new RegionBlock(string.Join("\n", labels), braced: false);

                    foreach (var inner in section.Statements)
                    {
                        Add(arm.Body, inner);
                    }

                    block.Body.Add(arm);
                }

                into.Add(block);
                return;
            }

            case TryStatementSyntax attempt:
                Nest(into, Header(text, 0, attempt.Block), attempt.Block);

                foreach (var handler in attempt.Catches)
                {
                    Nest(into, Header(text, handler.SpanStart, handler.Block), handler.Block);
                }

                if (attempt.Finally is { } cleanup)
                {
                    Nest(into, Header(text, cleanup.SpanStart, cleanup.Block), cleanup.Block);
                }

                return;

            case LabeledStatementSyntax labeled:
                into.Add(new RegionCode(text.Substring(0, labeled.ColonToken.Span.End)));
                Add(into, labeled.Statement);
                return;

            default:
                if (BodyOf(node) is { } body)
                {
                    Nest(into, Header(text, 0, body), body);
                }
                else
                {
                    into.Add(new RegionCode(text));
                }

                return;
        }
    }

    /// <summary>
    /// The statement a construct with one body governs, or null for a simple statement.
    /// </summary>
    private static StatementSyntax? BodyOf(StatementSyntax statement) =>
        statement switch
        {
            LocalFunctionStatementSyntax function => function.Body,
            ForStatementSyntax loop => loop.Statement,
            CommonForEachStatementSyntax loop => loop.Statement,
            WhileStatementSyntax loop => loop.Statement,
            UsingStatementSyntax scope => scope.Statement,
            LockStatementSyntax scope => scope.Statement,
            FixedStatementSyntax scope => scope.Statement,
            CheckedStatementSyntax scope => scope.Block,
            UnsafeStatementSyntax scope => scope.Block,
            _ => null,
        };

    /// <summary>
    /// The text from <paramref name="start"/> to the last token before <paramref name="body"/>.
    /// </summary>
    /// <remarks>
    /// That token's trailing trivia is left out. A comment there would otherwise end the line, and
    /// a K&amp;R brace joined to it would be commented out.
    /// </remarks>
    private static string Header(string text, int start, StatementSyntax body) =>
        text.Substring(start, body.GetFirstToken().GetPreviousToken().Span.End - start);

    /// <summary>
    /// A block under <paramref name="header"/> holding <paramref name="body"/>'s statements, or
    /// <paramref name="body"/> itself when it is a single statement without braces.
    /// </summary>
    private static RegionBlock Nest(
        List<RegionStatement> into,
        string? header,
        StatementSyntax body
    )
    {
        var block = new RegionBlock(header);

        if (body is BlockSyntax braced)
        {
            foreach (var statement in braced.Statements)
            {
                Add(block.Body, statement);
            }
        }
        else
        {
            Add(block.Body, body);
        }

        into.Add(block);

        return block;
    }
}
