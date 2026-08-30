using System.Text;

namespace ValidationModules.Rules;

/// <summary>
/// The selector and predicate text transforms, shared verbatim by both engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is compiled into ValidationModules.SourceGenerator.Impl as well as into the
/// runtime.</b> The runtime engine that once ran these transforms is gone - rules classes are read
/// by the generator, never run - but the file stays at this path because both generator projects
/// link it from here, and a single implementation of the render is still the point: the message an
/// <c>Ensure</c> bakes into generated code comes from exactly one transform.
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
    /// never a runtime value, which is why <c>Ensure</c> sits outside the redaction ladder rather
    /// than needing a policy of its own.
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
    /// The wire code for a predicate, derived from the same render its message comes from:
    /// <c>x =&gt; x.Start &lt; x.End</c> gives <c>start_less_than_end</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derived from the render, not from the source.</b> Going through
    /// <see cref="RenderPredicate"/> is what puts members under their wire names, so a property
    /// renamed in C# behind a pinned <c>[JsonPropertyName]</c> moves neither the message nor the
    /// code. It also makes the two incapable of disagreeing, since one is a transform of the other.
    /// </para>
    /// <para>
    /// <b>The code moves when the rule moves, and that is the point.</b> Widening <c>&lt;</c> to
    /// <c>&lt;=</c> changes what the user is told and what a client should do about it, so a key
    /// that survived the edit would be asserting that nothing happened - and a translation carried
    /// across it would be quietly wrong. Pass <c>code:</c> to pin one rule against that.
    /// </para>
    /// <para>
    /// <b>The operator spelling is a wire contract.</b> It is the
    /// <c>System.Linq.Expressions.ExpressionType</c> names in snake_case, which is also how
    /// FluentValidation names its comparison validators, and it matches the spelled-out house style
    /// of <c>string_length</c> and <c>multiple_of</c>. The abbreviated dialects disagree with each
    /// other - OData spells <c>&lt;=</c> as <c>le</c> where MongoDB spells it <c>lte</c> - so there
    /// was no short convention to adopt. Respelling any of this later churns every derived code in
    /// every consuming application at once, which is the one kind of churn nothing above justifies.
    /// </para>
    /// </remarks>
    /// <param name="predicateText">The predicate's source, as written.</param>
    /// <param name="fieldNamer">Applied to each member read directly off the parameter.</param>
    /// <returns>Null when nothing derivable is left, so the caller keeps the generic code.</returns>
    public static string? CodeOfPredicate(string? predicateText, Func<string, string> fieldNamer) {
        var rendered = RenderPredicate(predicateText, fieldNamer);

        if (rendered.Length == 0) {
            return null;
        }

        var builder = new StringBuilder(rendered.Length + 16);

        // RenderPredicate appends one period. It is punctuation on the sentence, not part of the
        // rule, and it is the only trailing character that is ever there.
        var end = rendered[rendered.Length - 1] == '.' ? rendered.Length - 1 : rendered.Length;
        var index = 0;

        while (index < end) {
            var character = rendered[index];

            if (character == '"' || character == '\'') {
                var start = index;
                SkipLiteral(rendered, ref index);

                // The contents without the quotes. A compared literal is part of what the rule
                // means, so changing it is a rule change and moves the code with it.
                var length = index - start - 2;
                AppendWords(builder, length > 0 ? rendered.Substring(start + 1, length) : string.Empty);
                continue;
            }

            if (IsIdentifierStart(character)) {
                AppendWords(builder, ReadIdentifier(rendered, ref index)!);
                continue;
            }

            if (char.IsDigit(character)) {
                var start = index;

                while (index < end && (char.IsDigit(rendered[index]) || rendered[index] == '.')) {
                    index++;
                }

                AppendWords(builder, rendered.Substring(start, index - start));
                continue;
            }

            var word = ReadOperator(rendered, ref index);

            if (word is not null) {
                AppendSegment(builder, word);
                continue;
            }

            // Whitespace, parentheses, commas, and the dots inside a member path. All of them are
            // structure rather than meaning, and the underscore between segments carries it.
            index++;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// Reads one operator at <paramref name="index"/>, longest first, and returns its wire word.
    /// </summary>
    private static string? ReadOperator(string text, ref int index) {
        if (index + 1 < text.Length) {
            var word = TwoCharacterOperator(text[index], text[index + 1]);

            if (word is not null) {
                index += 2;
                return word;
            }
        }

        var single = OneCharacterOperator(text[index]);

        if (single is null) {
            return null;
        }

        index++;
        return single;
    }

    /// <remarks>
    /// <c>AndAlso</c> and <c>OrElse</c> are the two <c>ExpressionType</c> names not taken verbatim.
    /// They describe C#'s short-circuiting, which is a fact about evaluation rather than about what
    /// the rule means, and neither reads as anything on a wire.
    /// </remarks>
    private static string? TwoCharacterOperator(char first, char second) {
        if (first == '<' && second == '=') { return "less_than_or_equal"; }
        if (first == '>' && second == '=') { return "greater_than_or_equal"; }
        if (first == '=' && second == '=') { return "equal"; }
        if (first == '!' && second == '=') { return "not_equal"; }
        if (first == '&' && second == '&') { return "and"; }
        if (first == '|' && second == '|') { return "or"; }

        return null;
    }

    private static string? OneCharacterOperator(char character) {
        switch (character) {
            case '<': return "less_than";
            case '>': return "greater_than";
            case '!': return "not";
            case '+': return "plus";
            case '-': return "minus";
            case '*': return "times";
            case '/': return "divided_by";
            case '%': return "modulo";
            default: return null;
        }
    }

    /// <summary>
    /// Appends one identifier as snake_case segments, splitting camel humps and acronym runs so
    /// <c>creditLimit</c> gives <c>credit_limit</c> and <c>HTTPStatus</c> gives <c>http_status</c>.
    /// </summary>
    private static void AppendWords(StringBuilder builder, string text) {
        var start = 0;

        for (var index = 0; index < text.Length; index++) {
            var character = text[index];

            if (!char.IsLetterOrDigit(character)) {
                AppendSegment(builder, text.Substring(start, index - start));
                start = index + 1;
                continue;
            }

            // A hump starts at an upper following a non-upper, and an acronym run ends at the
            // upper before a lower - which is the letter that starts the next word, not the last
            // of the acronym.
            if (index > start && char.IsUpper(character) &&
                (!char.IsUpper(text[index - 1]) ||
                 (index + 1 < text.Length && char.IsLower(text[index + 1])))) {
                AppendSegment(builder, text.Substring(start, index - start));
                start = index;
            }
        }

        AppendSegment(builder, text.Substring(start));
    }

    private static void AppendSegment(StringBuilder builder, string segment) {
        if (segment.Length == 0) {
            return;
        }

        if (builder.Length > 0) {
            builder.Append('_');
        }

        for (var index = 0; index < segment.Length; index++) {
            var character = segment[index];

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
        }
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
