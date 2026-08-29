using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using ValidationModules.SourceGenerator.Impl.Models;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// Reads one <c>*.validation-messages.json</c> file into a <see cref="LanguagePackModel"/>,
/// validating it against the shape-key inventory - which is the reason packs are compiled rather
/// than loaded: an unknown shape key, a hole beyond a shape's argument contract, a duplicate key,
/// all become diagnostics at the build they affect (docs/language-packs.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>The JSON reader is deliberately hand-rolled.</b> A generator that references
/// System.Text.Json inherits whatever version the compiler host loaded, which is a classic
/// analyzer failure mode; the schema here is one object with a string and a string map, and a
/// hundred lines with no dependencies reads it deterministically everywhere the compiler runs.
/// </para>
/// <para>
/// <b>Unknown bare keys compile silently.</b> <c>date_order</c> is a user code, and translating
/// user codes is the point. The typo heuristic fires only for a dotted key whose prefix is a
/// known code but whose whole is not (<c>string_length.atmost</c>) - a shape a user code is
/// unlikely to imitate and a misspelling is near-certain to produce.
/// </para>
/// </remarks>
public static class LanguagePackReader {

    /// <summary>The shape-key inventory with each key's argument count - the arity contract holes are checked against.</summary>
    /// <remarks>Mirrors <c>ValidationModules.ValidationMessageTemplates.TemplatesByKey</c>; the parity test pins the two together.</remarks>
    public static readonly ImmutableDictionary<string, int> ShapeInventory =
        new Dictionary<string, int>(StringComparer.Ordinal) {
            ["required"] = 0,
            ["string_length.between"] = 2,
            ["string_length.between_singular"] = 2,
            ["string_length.at_most"] = 1,
            ["string_length.at_most_singular"] = 1,
            ["string_length.at_least"] = 1,
            ["string_length.at_least_singular"] = 1,
            ["array_bounds.between"] = 2,
            ["array_bounds.between_singular"] = 2,
            ["array_bounds.at_most"] = 1,
            ["array_bounds.at_most_singular"] = 1,
            ["array_bounds.at_least"] = 1,
            ["array_bounds.at_least_singular"] = 1,
            ["range.between"] = 2,
            ["range.greater_and_at_most"] = 2,
            ["range.at_least_and_less"] = 2,
            ["range.greater_and_less"] = 2,
            ["range.at_least"] = 1,
            ["range.greater_than"] = 1,
            ["range.at_most"] = 1,
            ["range.less_than"] = 1,
            ["multiple_of"] = 1,
            ["unique_items"] = 0,
            ["pattern"] = 0,
            ["enum"] = 1,
            ["enum.denied"] = 1,
            ["enum.flags"] = 1,
            ["email"] = 0,
            ["phone"] = 0,
            ["url"] = 0,
            ["credit_card"] = 0,
            ["base64"] = 0,
            ["file_extension"] = 1,
            ["custom"] = 0,
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>The wire codes, for the dotted-typo heuristic.</summary>
    private static readonly ImmutableHashSet<string> KnownCodes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "required", "string_length", "range", "pattern", "enum", "array_bounds", "multiple_of",
        "unique_items", "invalid", "email", "phone", "url", "credit_card", "base64",
        "file_extension", "custom", "predicate");

    public readonly record struct Outcome(LanguagePackModel? Model, ImmutableArray<Diagnostic> Diagnostics);

    /// <summary>Reads and validates one file. A model is produced whenever the file parsed, even beside warnings.</summary>
    /// <param name="file">The file's path and content.</param>
    /// <param name="index">This file's position in the additional-files order, for a unique class name.</param>
    public static Outcome Read(LanguagePackFile file, int index) {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var at = FileLocation(file.Path);

        if (!MiniJson.TryParse(file.Content, out var culture, out var entries, out var parseError)) {
            diagnostics.Add(Diagnostic.Create(
                ValidationDiagnostics.LanguagePackUnreadable, at, PackName(file.Path), parseError));

            return new Outcome(null, diagnostics.ToImmutable());
        }

        if (string.IsNullOrWhiteSpace(culture)) {
            diagnostics.Add(Diagnostic.Create(
                ValidationDiagnostics.LanguagePackUnreadable, at, PackName(file.Path),
                "it declares no \"culture\""));

            return new Outcome(null, diagnostics.ToImmutable());
        }

        // messages.fr.validation-messages.json says "fr" twice; when the two disagree, the body
        // wins and the name gets a warning - explicit over filename magic, but a mismatch is a
        // copy-paste story worth hearing.
        if (FileNameCulture(file.Path) is { } named &&
            !string.Equals(named, culture, StringComparison.OrdinalIgnoreCase)) {
            diagnostics.Add(Diagnostic.Create(
                ValidationDiagnostics.LanguagePackNameMismatch, at, PackName(file.Path), named, culture));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<LanguagePackEntry>(entries.Count);

        foreach (var (key, template) in entries) {
            if (!seen.Add(key)) {
                diagnostics.Add(Diagnostic.Create(
                    ValidationDiagnostics.LanguagePackDuplicateKey, at, key, PackName(file.Path)));
                continue;
            }

            if (ShapeInventory.TryGetValue(key, out var arity)) {
                if (HighestHole(template) is { } hole && hole >= arity) {
                    diagnostics.Add(Diagnostic.Create(
                        ValidationDiagnostics.LanguagePackHoleOutOfRange, at,
                        key, hole, arity, PackName(file.Path)));
                    continue;
                }
            }
            else if (key.IndexOf('.') is var dot && dot > 0 && KnownCodes.Contains(key.Substring(0, dot))) {
                diagnostics.Add(Diagnostic.Create(
                    ValidationDiagnostics.LanguagePackUnknownShape, at,
                    key, PackName(file.Path), NearestKey(key)));
                continue;
            }

            // A bare unknown key is a user code and compiles as written - holes unchecked,
            // because only the code's author knows its arity, and the renderer is tolerant.
            kept.Add(new LanguagePackEntry(key, template));
        }

        var missing = ShapeInventory.Keys.Where(key => !seen.Contains(key)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        if (missing.Count > 0 && kept.Count > 0) {
            diagnostics.Add(Diagnostic.Create(
                ValidationDiagnostics.LanguagePackCoverage, at,
                culture, ShapeInventory.Count - missing.Count, ShapeInventory.Count,
                string.Join(", ", missing.Take(6)) + (missing.Count > 6 ? ", …" : string.Empty)));
        }

        var model = new LanguagePackModel(
            culture!,
            $"{Identifier(culture!)}LanguagePack{index}",
            $"LanguagePack.{culture}.{index}.g.cs",
            new EquatableArray<LanguagePackEntry>(kept.ToImmutableArray()));

        return new Outcome(model, diagnostics.ToImmutable());
    }

    /// <summary>"fr" → Fr, "zh-Hans" → ZhHans: the culture as a class-name prefix.</summary>
    private static string Identifier(string culture) {
        var builder = new StringBuilder(culture.Length);
        var upper = true;

        foreach (var character in culture) {
            if (char.IsLetterOrDigit(character)) {
                builder.Append(upper ? char.ToUpperInvariant(character) : character);
                upper = false;
            }
            else {
                upper = true;
            }
        }

        return builder.Length == 0 ? "Neutral" : builder.ToString();
    }

    /// <summary>The highest {n} hole in a template, escapes skipped, or null when it has none.</summary>
    private static int? HighestHole(string template) {
        int? highest = null;

        for (var i = 0; i < template.Length - 1; i++) {
            if (template[i] != '{') {
                continue;
            }

            if (template[i + 1] == '{') {
                i++;
                continue;
            }

            if (i + 2 < template.Length && template[i + 2] == '}' && template[i + 1] is >= '0' and <= '9') {
                var hole = template[i + 1] - '0';

                highest = highest is { } current && current > hole ? current : hole;
            }
        }

        return highest;
    }

    /// <summary>The inventory key nearest the misspelling, for the VM0101 message.</summary>
    private static string NearestKey(string key) {
        var best = "";
        var bestDistance = int.MaxValue;

        foreach (var candidate in ShapeInventory.Keys) {
            var distance = Distance(key, candidate);

            if (distance < bestDistance) {
                (best, bestDistance) = (candidate, distance);
            }
        }

        return best;
    }

    /// <summary>Plain Levenshtein; the strings are short and this runs only on a diagnostic path.</summary>
    private static int Distance(string a, string b) {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++) {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++) {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);

                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static Location FileLocation(string path) =>
        Location.Create(path, default, default);

    private static string PackName(string path) => System.IO.Path.GetFileName(path);

    /// <summary>The culture a file name claims - the token before the suffix - or null when it claims none.</summary>
    private static string? FileNameCulture(string path) {
        const string suffix = ".validation-messages.json";
        var name = System.IO.Path.GetFileName(path);

        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || name.Length <= suffix.Length) {
            return null;
        }

        var stem = name.Substring(0, name.Length - suffix.Length);
        var dot = stem.LastIndexOf('.');
        var token = dot >= 0 ? stem.Substring(dot + 1) : stem;

        // Only tokens that look like a culture name make a claim; "overrides" or "messages" do not.
        return token.Length is >= 2 and <= 11 &&
            token.All(c => char.IsLetter(c) || c == '-') && token.Any(char.IsLetter) &&
            (token.Length <= 3 || token.IndexOf('-') >= 0)
                ? token
                : null;
    }

    /// <summary>
    /// The schema-directed reader: one object, a "culture" string, a "templates" object of
    /// strings. Anything else is tolerated and ignored, so the format can grow additively.
    /// </summary>
    private static class MiniJson {

        public static bool TryParse(
            string text,
            out string? culture,
            out List<(string Key, string Value)> templates,
            out string error) {
            culture = null;
            templates = new List<(string, string)>();
            error = string.Empty;

            var position = 0;

            try {
                SkipWhitespace(text, ref position);
                Expect(text, ref position, '{');

                var first = true;

                while (true) {
                    SkipWhitespace(text, ref position);

                    if (Peek(text, position) == '}') {
                        position++;
                        break;
                    }

                    if (!first) {
                        Expect(text, ref position, ',');
                        SkipWhitespace(text, ref position);
                    }

                    first = false;

                    var name = ReadString(text, ref position);

                    SkipWhitespace(text, ref position);
                    Expect(text, ref position, ':');
                    SkipWhitespace(text, ref position);

                    if (string.Equals(name, "culture", StringComparison.Ordinal)) {
                        culture = ReadString(text, ref position);
                    }
                    else if (string.Equals(name, "templates", StringComparison.Ordinal)) {
                        ReadTemplates(text, ref position, templates);
                    }
                    else {
                        SkipValue(text, ref position);
                    }
                }

                return true;
            }
            catch (FormatException formatError) {
                error = $"{formatError.Message} at offset {position} (line {Line(text, position)})";

                return false;
            }
        }

        private static void ReadTemplates(string text, ref int position, List<(string, string)> templates) {
            Expect(text, ref position, '{');

            var first = true;

            while (true) {
                SkipWhitespace(text, ref position);

                if (Peek(text, position) == '}') {
                    position++;
                    return;
                }

                if (!first) {
                    Expect(text, ref position, ',');
                    SkipWhitespace(text, ref position);
                }

                first = false;

                var key = ReadString(text, ref position);

                SkipWhitespace(text, ref position);
                Expect(text, ref position, ':');
                SkipWhitespace(text, ref position);
                templates.Add((key, ReadString(text, ref position)));
            }
        }

        private static string ReadString(string text, ref int position) {
            Expect(text, ref position, '"');

            var builder = new StringBuilder();

            while (true) {
                if (position >= text.Length) {
                    throw new FormatException("unterminated string");
                }

                var current = text[position++];

                if (current == '"') {
                    return builder.ToString();
                }

                if (current != '\\') {
                    builder.Append(current);
                    continue;
                }

                if (position >= text.Length) {
                    throw new FormatException("unterminated escape");
                }

                var escape = text[position++];

                builder.Append(escape switch {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' => Unicode(text, ref position),
                    _ => throw new FormatException($"unknown escape '\\{escape}'"),
                });
            }
        }

        private static char Unicode(string text, ref int position) {
            if (position + 4 > text.Length) {
                throw new FormatException("truncated \\u escape");
            }

            var value = 0;

            for (var i = 0; i < 4; i++) {
                var digit = text[position++];

                value = (value << 4) + digit switch {
                    >= '0' and <= '9' => digit - '0',
                    >= 'a' and <= 'f' => digit - 'a' + 10,
                    >= 'A' and <= 'F' => digit - 'A' + 10,
                    _ => throw new FormatException($"'{digit}' is not a hex digit"),
                };
            }

            return (char)value;
        }

        /// <summary>Skips any value shape, for top-level members this schema does not know.</summary>
        private static void SkipValue(string text, ref int position) {
            switch (Peek(text, position)) {
                case '"':
                    ReadString(text, ref position);
                    return;
                case '{':
                case '[':
                    var open = text[position];
                    var close = open == '{' ? '}' : ']';
                    var depth = 0;

                    while (position < text.Length) {
                        var current = text[position];

                        if (current == '"') {
                            ReadString(text, ref position);
                            continue;
                        }

                        position++;

                        if (current == open) {
                            depth++;
                        }
                        else if (current == close && --depth == 0) {
                            return;
                        }
                    }

                    throw new FormatException("unterminated value");
                default:
                    while (position < text.Length && text[position] is not (',' or '}' or ']')) {
                        position++;
                    }

                    return;
            }
        }

        private static void SkipWhitespace(string text, ref int position) {
            // A leading BOM arrives with files saved as UTF-8-with-BOM, which translators' tools love.
            while (position < text.Length && (char.IsWhiteSpace(text[position]) || text[position] == '\uFEFF')) {
                position++;
            }
        }

        private static char Peek(string text, int position) =>
            position < text.Length ? text[position] : throw new FormatException("unexpected end of file");

        private static void Expect(string text, ref int position, char expected) {
            if (position >= text.Length || text[position] != expected) {
                throw new FormatException($"expected '{expected}'");
            }

            position++;
        }

        private static int Line(string text, int position) {
            var line = 1;

            for (var i = 0; i < position && i < text.Length; i++) {
                if (text[i] == '\n') {
                    line++;
                }
            }

            return line;
        }
    }
}
