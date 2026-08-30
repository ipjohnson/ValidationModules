using System.Collections.Generic;
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
    /// The version of <see cref="CodeOfPredicate"/>'s output. Every code it derives is a wire
    /// contract, so this moves only in a major release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this pins.</b> Not an API shape but a mapping: every predicate a consumer has
    /// written, to the code their clients switch on and their translators key by. Respelling one
    /// operator, recognising one more idiom, or changing how a literal is encoded moves codes for
    /// rules nobody edited - churn with no semantic reason, which is the one kind the derivation's
    /// own argument does not justify.
    /// </para>
    /// <para>
    /// <b>How it is enforced.</b> A corpus of predicates is rendered to codes and checksummed
    /// against <see cref="CodeDerivationChecksum"/>. Accepting the snapshot is deliberately not
    /// enough to move a code: the constant lives here, in product source, so changing the mechanics
    /// requires an edit a reviewer sees next to this comment.
    /// </para>
    /// <para>
    /// <b>Before 1.0.0 this is free to move.</b> No release has shipped a derived code, so the
    /// corpus can be re-pinned as often as the mechanics improve. After 1.0.0 it cannot.
    /// </para>
    /// </remarks>
    public const int CodeDerivationContract = 1;

    /// <summary>
    /// The pinned corpus's checksum under <see cref="CodeDerivationContract"/>. Changing this
    /// without bumping the contract is the mistake the pairing exists to catch.
    /// </summary>
    public const string CodeDerivationChecksum = "6f8d03d0c8bf49dc";

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

        // Lambda parameters are noise twice over: they say nothing about the rule, and renaming
        // one would move the code with no semantic change at all.
        var lambdaParameters = new List<string>();

        // Whether a '(' here opens a call rather than a group. Only a group carries meaning, and
        // only a group needs marking, because dropping it loses precedence.
        var afterValue = false;

        while (index < end) {
            var character = rendered[index];

            if (character == '"' || character == '\'') {
                var start = index;
                SkipLiteral(rendered, ref index);

                // The contents without the quotes. A compared literal is part of what the rule
                // means, so changing it is a rule change and moves the code with it.
                var length = index - start - 2;
                AppendLiteral(builder, length > 0 ? rendered.Substring(start + 1, length) : string.Empty);
                afterValue = true;
                continue;
            }

            if (character == '(') {
                if (afterValue) {
                    // A call's own parentheses: the name before them already said what happens.
                    index++;
                    afterValue = false;
                    continue;
                }

                if (ReadLambdaHead(rendered, ref index, lambdaParameters)) {
                    afterValue = false;
                    continue;
                }

                // Grouping. Unmarked, '!(a && b)' and '!a && b' would derive one code for two
                // different rules, which is the collision the whole derivation exists to remove.
                AppendSegment(builder, "group");
                index++;
                afterValue = false;
                continue;
            }

            if (character == ')') {
                index++;
                afterValue = true;
                continue;
            }

            if (IsIdentifierStart(character)) {
                if (TryAppendBlankIdiom(rendered, ref index, builder)) {
                    afterValue = true;
                    continue;
                }

                var identifier = ReadIdentifier(rendered, ref index)!;

                if (TryAppendEmptinessIdiom(identifier, rendered, ref index, end, builder)) {
                    afterValue = true;
                    continue;
                }

                if (IsLambdaArrowAhead(rendered, index)) {
                    // "l => …": the parameter and its arrow are both structure.
                    lambdaParameters.Add(identifier);
                    SkipLambdaArrow(rendered, ref index);
                    afterValue = false;
                    continue;
                }

                if (lambdaParameters.Contains(identifier)) {
                    // "l.Price" is "price". The receiver names an element, not a rule.
                    if (index < rendered.Length && rendered[index] == '.') {
                        index++;
                    }

                    afterValue = true;
                    continue;
                }

                AppendWords(builder, identifier);
                afterValue = true;
                continue;
            }

            if (char.IsDigit(character)) {
                var start = index;

                while (index < end && (char.IsDigit(rendered[index]) || rendered[index] == '.')) {
                    index++;
                }

                AppendWords(builder, rendered.Substring(start, index - start));
                afterValue = true;
                continue;
            }

            if (character == '!' && afterValue && (index + 1 >= end || rendered[index + 1] != '=')) {
                // The null-forgiving operator, not a negation. It asserts something about what the
                // compiler knows rather than about the value, so it is not part of the rule - and
                // reading it as "not" made x.Name!.Length and x.Name.Length two different rules.
                index++;
                continue;
            }

            if (TryAppendNullIdiom(rendered, ref index, end, builder)) {
                afterValue = true;
                continue;
            }

            var word = ReadOperator(rendered, ref index);

            if (word is not null) {
                AppendSegment(builder, word);
                afterValue = false;
                continue;
            }

            // Whitespace, commas, and the dots inside a member path. Structure rather than
            // meaning, and the underscore between segments carries it.
            index++;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// The recognised idioms, named for what they assert rather than for the tokens that spell
    /// them: <c>!string.IsNullOrWhiteSpace(name)</c> is <c>name_is_not_null_or_blank</c> rather
    /// than <c>not_string_is_null_or_white_space_name</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every entry is a wire contract, so the table only holds shapes whose meaning is not in
    /// doubt, and it can only grow in a major release. Recognising one more idiom later moves the
    /// codes of rules nobody edited.
    /// </para>
    /// <para>
    /// <c>Any()</c> is deliberately absent. Its useful form is the negated one, and inverting
    /// <c>!x.Items.Any()</c> to <c>items_is_empty</c> means knowing the <c>!</c> covers the whole
    /// call rather than its receiver, which is parsing rather than matching. It derives
    /// <c>items_any</c> and reads well enough.
    /// </para>
    /// </remarks>
    private static bool TryAppendBlankIdiom(string text, ref int index, StringBuilder builder) {
        string suffix;

        if (Matches(text, index, "string.IsNullOrEmpty(")) {
            suffix = "is_null_or_empty";
            index += "string.IsNullOrEmpty(".Length;
        } else if (Matches(text, index, "string.IsNullOrWhiteSpace(")) {
            suffix = "is_null_or_blank";
            index += "string.IsNullOrWhiteSpace(".Length;
        } else {
            return false;
        }

        var close = MatchingParenthesis(text, index);

        if (close < 0) {
            return false;
        }

        // The '!' is already in the builder as its own segment. Popping it and inverting is what
        // puts the subject first, which is the whole readability gain.
        if (PopTrailingNot(builder)) {
            suffix = suffix == "is_null_or_empty" ? "is_not_null_or_empty" : "is_not_null_or_blank";
        }

        AppendWords(builder, text.Substring(index, close - index));
        AppendSegment(builder, suffix);

        index = close + 1;
        return true;
    }

    /// <summary>
    /// <c>.Count == 0</c> and its family. The receiver is already in the builder, so this appends
    /// what the comparison asserts and drops the count itself.
    /// </summary>
    private static bool TryAppendEmptinessIdiom(
        string identifier, string text, ref int index, int end, StringBuilder builder) {

        if (identifier != "Count" && identifier != "Length") {
            return false;
        }

        var scan = index;

        while (scan < end && text[scan] == ' ') {
            scan++;
        }

        string suffix;

        if (Matches(text, scan, "== 0")) {
            suffix = "is_empty";
        } else if (Matches(text, scan, "> 0") || Matches(text, scan, "!= 0")) {
            suffix = "is_not_empty";
        } else {
            return false;
        }

        // Only when the comparison ends the expression. "x.Items.Count > 0 && …" is a count being
        // used, not an emptiness test, and rewriting it would drop the rest of the rule's shape.
        var after = scan + (suffix == "is_empty" ? 4 : Matches(text, scan, "> 0") ? 3 : 4);

        while (after < end && text[after] == ' ') {
            after++;
        }

        if (after != end) {
            return false;
        }

        AppendSegment(builder, suffix);
        index = end;
        return true;
    }

    /// <summary>
    /// <c>== null</c> and <c>!= null</c>, so they agree with the <c>is null</c> spelling the walk
    /// already produces rather than deriving a second code for the same assertion.
    /// </summary>
    private static bool TryAppendNullIdiom(string text, ref int index, int end, StringBuilder builder) {
        string suffix;
        int width;

        if (Matches(text, index, "== null")) {
            suffix = "is_null";
            width = "== null".Length;
        } else if (Matches(text, index, "!= null")) {
            suffix = "is_not_null";
            width = "!= null".Length;
        } else {
            return false;
        }

        if (index + width > end) {
            return false;
        }

        AppendSegment(builder, suffix);
        index += width;
        return true;
    }

    private static bool Matches(string text, int index, string expected) {
        if (index < 0 || index + expected.Length > text.Length) {
            return false;
        }

        for (var offset = 0; offset < expected.Length; offset++) {
            if (text[index + offset] != expected[offset]) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The index of the parenthesis closing the one whose contents start at the index.</summary>
    private static int MatchingParenthesis(string text, int index) {
        var depth = 1;

        while (index < text.Length) {
            if (text[index] == '"' || text[index] == '\'') {
                SkipLiteral(text, ref index);
                continue;
            }

            if (text[index] == '(') {
                depth++;
            } else if (text[index] == ')' && --depth == 0) {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>Removes a trailing <c>not</c> segment, reporting whether there was one.</summary>
    private static bool PopTrailingNot(StringBuilder builder) {
        const string Segment = "not";

        if (builder.Length == Segment.Length && Matches(builder.ToString(), 0, Segment)) {
            builder.Length = 0;
            return true;
        }

        if (builder.Length > Segment.Length + 1 &&
            Matches(builder.ToString(), builder.Length - Segment.Length - 1, "_" + Segment)) {
            builder.Length -= Segment.Length + 1;
            return true;
        }

        return false;
    }

    /// <summary>Whether a lambda arrow follows, skipping spaces.</summary>
    private static bool IsLambdaArrowAhead(string text, int index) {
        while (index < text.Length && text[index] == ' ') {
            index++;
        }

        return index + 1 < text.Length && text[index] == '=' && text[index + 1] == '>';
    }

    private static void SkipLambdaArrow(string text, ref int index) {
        while (index < text.Length && text[index] == ' ') {
            index++;
        }

        index += 2;
    }

    /// <summary>
    /// Reads <c>(a, b) =&gt;</c> at a grouping parenthesis, recording the parameters and leaving
    /// the index after the arrow. Restores the index and returns false when it is a real group.
    /// </summary>
    private static bool ReadLambdaHead(string text, ref int index, List<string> parameters) {
        var start = index;
        var scan = index + 1;
        var names = new List<string>();

        while (scan < text.Length && text[scan] != ')') {
            if (text[scan] == ' ' || text[scan] == ',') {
                scan++;
                continue;
            }

            var name = ReadIdentifier(text, ref scan);

            if (name is null) {
                index = start;
                return false;
            }

            names.Add(name);
        }

        if (scan >= text.Length || !IsLambdaArrowAhead(text, scan + 1)) {
            index = start;
            return false;
        }

        // "(Line l) => …" names a type then a parameter; the parameter is the last identifier,
        // the same reading BodyOf takes of a lambda head.
        if (names.Count > 0) {
            parameters.Add(names[names.Count - 1]);
        }

        index = scan + 1;
        SkipLambdaArrow(text, ref index);
        return true;
    }

    /// <summary>
    /// Appends a string literal's contents. Punctuation is named rather than dropped, because
    /// <c>Contains("@")</c> and <c>Contains(".")</c> are different rules.
    /// </summary>
    private static void AppendLiteral(StringBuilder builder, string text) {
        var start = 0;

        for (var index = 0; index < text.Length; index++) {
            var character = text[index];

            if (char.IsLetterOrDigit(character)) {
                continue;
            }

            AppendWords(builder, text.Substring(start, index - start));
            start = index + 1;

            if (!char.IsWhiteSpace(character)) {
                AppendSegment(builder, PunctuationWord(character));
            }
        }

        AppendWords(builder, text.Substring(start));
    }

    /// <remarks>
    /// The named set is the punctuation that turns up in validated data. Anything else takes its
    /// code point, which reads badly and collides with nothing - the property that matters.
    /// </remarks>
    private static string PunctuationWord(char character) {
        switch (character) {
            case '@': return "at";
            case '.': return "dot";
            case '-': return "dash";
            case '_': return "underscore";
            case '/': return "slash";
            case '\\': return "backslash";
            case ':': return "colon";
            case ';': return "semicolon";
            case ',': return "comma";
            case '+': return "plus";
            case '*': return "star";
            case '#': return "hash";
            case '%': return "percent";
            case '&': return "amp";
            case '?': return "question";
            case '!': return "bang";
            case '=': return "equals";
            case '|': return "pipe";
            case '^': return "caret";
            case '~': return "tilde";
            case '$': return "dollar";
            case '\'': case '"': return "quote";
            case '(': return "lparen";
            case ')': return "rparen";
            case '[': return "lbracket";
            case ']': return "rbracket";
            case '{': return "lbrace";
            case '}': return "rbrace";
            case '<': return "lt";
            case '>': return "gt";
            default: return "cp" + ((int)character).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }
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
