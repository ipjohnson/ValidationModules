using System.Text;

namespace ValidationModules.Rules;

/// <summary>
/// The selector and predicate text transforms, shared verbatim by both engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is compiled into ValidationModules.SourceGenerator.Impl as well as into the
/// runtime.</b> API-SURFACE.md §19.5 rests on the generated validator and
/// <see cref="DescribedValidator{T}"/> producing byte-identical messages for the same rule, and two
/// implementations of the same string transform would only agree until someone edited one of them.
/// Sharing the source removes the possibility rather than testing for it.
/// </para>
/// <para>
/// Consequently it must compile as netstandard2.0 under LangVersion 10 as well as net8.0+: no
/// collection expressions, no ranges, no <c>string.Contains(char)</c>, nothing newer than the
/// generator project allows.
/// </para>
/// <para>
/// <b>Whitespace is normalised first, always.</b> The runtime receives this text from the compiler
/// through <c>CallerArgumentExpression</c> and the generator reads it off the syntax node; both are
/// the expression's source span and should agree byte for byte, but interior trivia in a multi-line
/// lambda is where they would not. Collapsing runs of whitespace makes the question moot, and stops
/// a reformatted lambda changing an error message.
/// </para>
/// </remarks>
internal static class RuleText {

    /// <summary>
    /// The property name a selector reads - <c>"x =&gt; x.Age"</c> gives <c>"Age"</c>, and
    /// <c>"x =&gt; x.Home.PostalCode"</c> gives <c>"Home"</c>, because the outermost property is
    /// what the error is pathed against.
    /// </summary>
    /// <returns>Null when the body is not a member access on the parameter, which is VM0071.</returns>
    public static string? PropertyOfSelector(string? selectorText) {
        var body = BodyOf(selectorText, out var parameter);

        if (body is null || parameter is null) {
            return null;
        }

        var index = 0;
        var member = ReadParameterMember(body, ref index, parameter);

        if (member is null) {
            return null;
        }

        // Anything after the property path means this is an expression rather than a selector -
        // "x => x.Age + 1" reads a property but is not one, and naming the error "age" would be a
        // guess. Trailing member accesses are fine: they are still one path.
        while (index < body.Length && body[index] == '.') {
            index++;
            if (ReadIdentifier(body, ref index) is null) {
                return null;
            }
        }

        return index == body.Length ? member : null;
    }

    /// <summary>
    /// The property an <c>Ensure</c> anchors to: the first member read off the parameter anywhere in
    /// the predicate. <c>"x =&gt; x.Start &lt; x.End"</c> anchors to <c>"Start"</c>.
    /// </summary>
    /// <returns>Null when the predicate never touches its parameter, which is VM0075.</returns>
    public static string? AnchorOfPredicate(string? predicateText) {
        var body = BodyOf(predicateText, out var parameter);

        if (body is null || parameter is null) {
            return null;
        }

        var index = 0;

        while (index < body.Length) {
            if (SkipNonIdentifier(body, ref index)) {
                continue;
            }

            var member = ReadParameterMember(body, ref index, parameter);

            if (member is not null) {
                return member;
            }

            // ReadParameterMember restores the index when the identifier was not the parameter, so
            // it has to be consumed here or the scan never advances. Leaving that out is an infinite
            // loop on any predicate whose first identifier is something else - "x => Constants.On" -
            // which is exactly the shape this method returns null for.
            ReadIdentifier(body, ref index);
        }

        return null;
    }

    /// <summary>
    /// Renders a predicate as its own error message: the parameter stripped, members off it in wire
    /// names, whitespace normalised, one trailing period.
    /// </summary>
    /// <remarks>
    /// The message is the rule, so it cannot drift from what is actually checked the way a composed
    /// message repeating a bound can. It carries only compile-time source and therefore only schema,
    /// never a runtime value - which is why <c>Ensure</c> sits outside the redaction ladder in
    /// HANDOFF.md §3.2 rather than needing a policy of its own.
    /// </remarks>
    /// <param name="predicateText">The predicate's source, as written.</param>
    /// <param name="fieldNamer">Applied to each member read directly off the parameter.</param>
    public static string RenderPredicate(string? predicateText, Func<string, string> fieldNamer) {
        var body = BodyOf(predicateText, out var parameter);

        if (body is null) {
            return string.Empty;
        }

        var builder = new StringBuilder(body.Length + 1);
        var index = 0;

        while (index < body.Length) {
            var start = index;

            if (SkipNonIdentifier(body, ref index)) {
                builder.Append(body, start, index - start);
                continue;
            }

            var memberStart = index;
            var member = parameter is null ? null : ReadParameterMember(body, ref index, parameter);

            if (member is not null) {
                builder.Append(fieldNamer(member));
                continue;
            }

            index = memberStart;
            var identifier = ReadIdentifier(body, ref index);
            builder.Append(identifier);
        }

        var rendered = builder.ToString().Trim();

        return rendered.Length == 0 || rendered[rendered.Length - 1] == '.'
            ? rendered
            : rendered + ".";
    }

    /// <summary>
    /// Splits a lambda into its parameter name and its body, with whitespace already normalised.
    /// </summary>
    /// <remarks>
    /// Accepts the forms a caller actually writes: <c>x =&gt; …</c>, <c>(x) =&gt; …</c>,
    /// <c>(Pet x) =&gt; …</c> and any of them prefixed <c>static</c>. A method group or anything
    /// else with no <c>=&gt;</c> has no parameter to strip, so the body is the whole text and
    /// nothing is rewritten.
    /// </remarks>
    private static string? BodyOf(string? text, out string? parameter) {
        parameter = null;

        if (text is null) {
            return null;
        }

        var normalized = NormalizeWhitespace(text);

        if (normalized.Length == 0) {
            return null;
        }

        var arrow = IndexOfArrow(normalized);

        if (arrow < 0) {
            return normalized;
        }

        var head = normalized.Substring(0, arrow).Trim();
        var body = normalized.Substring(arrow + 2).Trim();

        if (head.StartsWith("static", StringComparison.Ordinal)) {
            head = head.Substring("static".Length).Trim();
        }

        if (head.Length > 1 && head[0] == '(' && head[head.Length - 1] == ')') {
            head = head.Substring(1, head.Length - 2).Trim();
        }

        // "(Pet x)" leaves "Pet x"; the parameter is the last identifier either way. A tuple or
        // multi-parameter head is not something this DSL produces, and taking the last identifier
        // degrades to rewriting nothing rather than rewriting the wrong thing.
        if (head.IndexOf(',') >= 0) {
            return body;
        }

        var space = head.LastIndexOf(' ');
        var name = space < 0 ? head : head.Substring(space + 1);

        if (name.Length > 0 && IsIdentifierStart(name[0])) {
            parameter = name;
        }

        return body;
    }

    /// <summary>
    /// Finds the lambda arrow, ignoring one inside a string or character literal.
    /// </summary>
    private static int IndexOfArrow(string text) {
        var index = 0;

        while (index < text.Length - 1) {
            if (text[index] == '"' || text[index] == '\'') {
                SkipLiteral(text, ref index);
                continue;
            }

            if (text[index] == '=' && text[index + 1] == '>') {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>
    /// Reads <c>parameter.Member</c> at <paramref name="index"/> and returns the member name,
    /// leaving the index after it. Returns null and restores the index otherwise.
    /// </summary>
    private static string? ReadParameterMember(string body, ref int index, string parameter) {
        var start = index;
        var identifier = ReadIdentifier(body, ref index);

        if (identifier == parameter && index < body.Length && body[index] == '.') {
            index++;
            var member = ReadIdentifier(body, ref index);

            if (member is not null) {
                return member;
            }
        }

        index = start;
        return null;
    }

    /// <summary>
    /// Advances past everything that cannot begin an identifier, copying literals whole.
    /// </summary>
    /// <remarks>
    /// An identifier immediately preceded by a dot is a member of something else, not a reference to
    /// the lambda parameter - <c>other.x.Name</c> must not have its <c>x</c> rewritten - so a dot
    /// consumes the identifier that follows it here rather than leaving it to the caller.
    /// </remarks>
    /// <returns>True when it advanced, so the caller should re-test rather than read an identifier.</returns>
    private static bool SkipNonIdentifier(string body, ref int index) {
        if (index >= body.Length) {
            return false;
        }

        var character = body[index];

        if (character == '"' || character == '\'') {
            SkipLiteral(body, ref index);
            return true;
        }

        if (character == '.') {
            index++;
            ReadIdentifier(body, ref index);
            return true;
        }

        if (IsIdentifierStart(character)) {
            return false;
        }

        index++;
        return true;
    }

    private static void SkipLiteral(string text, ref int index) {
        var quote = text[index];
        index++;

        while (index < text.Length) {
            if (text[index] == '\\') {
                index += 2;
                continue;
            }

            if (text[index] == quote) {
                index++;
                return;
            }

            index++;
        }
    }

    private static string? ReadIdentifier(string text, ref int index) {
        if (index >= text.Length || !IsIdentifierStart(text[index])) {
            return null;
        }

        var start = index;
        index++;

        while (index < text.Length && IsIdentifierPart(text[index])) {
            index++;
        }

        return text.Substring(start, index - start);
    }

    private static bool IsIdentifierStart(char character) =>
        char.IsLetter(character) || character == '_' || character == '@';

    private static bool IsIdentifierPart(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    /// <summary>
    /// Collapses runs of whitespace to a single space, leaving string and character literals alone.
    /// </summary>
    private static string NormalizeWhitespace(string text) {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        var pendingSpace = false;

        while (index < text.Length) {
            var character = text[index];

            if (char.IsWhiteSpace(character)) {
                pendingSpace = builder.Length > 0;
                index++;
                continue;
            }

            if (pendingSpace) {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (character == '"' || character == '\'') {
                var start = index;
                SkipLiteral(text, ref index);
                builder.Append(text, start, index - start);
                continue;
            }

            builder.Append(character);
            index++;
        }

        return builder.ToString();
    }
}
