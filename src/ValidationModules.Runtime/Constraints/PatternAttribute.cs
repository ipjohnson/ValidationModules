using System.Text.RegularExpressions;

namespace ValidationModules.Constraints;

/// <summary>
/// The string must match a regular expression. Emits code <c>pattern</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two forms, and which one you want depends on whether the project targets Native AOT.
/// </para>
/// <para>
/// <b>The inline form</b> - <c>[Pattern("^[A-Z]{3}$")]</c> - compiles to a
/// <c>static readonly Regex</c> built once at type initialization. It never passes
/// <c>RegexOptions.Compiled</c>, so nothing reaches <c>Reflection.Emit</c> and it publishes
/// AOT-clean. What it costs is size: constructing a <see cref="Regex"/> from a pattern string
/// roots the regex parser and interpreter, which measures at <b>+1.16 MB</b> on a published AOT
/// binary against +16 KB for the same pattern through <c>[GeneratedRegex]</c>. A project that is
/// AOT-facing therefore rejects this form by default - see VM1301.
/// </para>
/// <para>
/// <b>The reference form</b> - <c>[Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]</c> -
/// points at a <c>[GeneratedRegex]</c> the consumer declared in their own source. That declaration
/// has to be theirs rather than ours: source generators cannot see each other's output, so a
/// <c>[GeneratedRegex]</c> this library emitted would never be implemented. Theirs is in the
/// original compilation, so the regex generator implements it and the generated validator calls it.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public static partial class PetPatterns {
///     [GeneratedRegex("^[A-Z]{3}$")] public static partial Regex Sku();
/// }
///
/// public record Pet {
///     [Pattern(typeof(PetPatterns), nameof(PetPatterns.Sku))]
///     public string? Sku { get; init; }
/// }
/// </code>
/// </example>
public sealed class PatternAttribute : ValidationConstraintAttribute {

    /// <summary>
    /// The inline form. The pattern must be a compile-time constant and is validated at generation
    /// time.
    /// </summary>
    /// <param name="pattern">A .NET regular expression.</param>
    public PatternAttribute(string pattern) {
        ArgumentNullException.ThrowIfNull(pattern);

        Pattern = pattern;
    }

    /// <summary>
    /// The reference form: use a <c>[GeneratedRegex]</c> the consumer declared.
    /// </summary>
    /// <remarks>
    /// The member is resolved and checked at generation time - it must exist, be static, be
    /// accessible from the validated type, and return <see cref="Regex"/> - so a typo is a build
    /// error rather than something that surfaces later.
    /// </remarks>
    /// <param name="regexProvider">The type declaring the regex member.</param>
    /// <param name="regexMember">Its name. Use <c>nameof</c> so a rename cannot silently break it.</param>
    public PatternAttribute(Type regexProvider, string regexMember) {
        ArgumentNullException.ThrowIfNull(regexProvider);
        ArgumentNullException.ThrowIfNull(regexMember);

        RegexProvider = regexProvider;
        RegexMember = regexMember;
    }

    /// <summary>The pattern, when declared inline. Null in the reference form.</summary>
    public string? Pattern { get; }

    /// <summary>The type declaring the regex member. Null in the inline form.</summary>
    public Type? RegexProvider { get; }

    /// <summary>The regex member's name. Null in the inline form.</summary>
    public string? RegexMember { get; }

    /// <summary>
    /// Options passed through to <c>[GeneratedRegex]</c>. <c>RegexOptions.Compiled</c> is
    /// meaningless here and is rejected by a diagnostic.
    /// </summary>
    public RegexOptions Options { get; init; }

    /// <summary>
    /// Match timeout, passed through to <c>[GeneratedRegex]</c>. Zero means no timeout. Worth
    /// setting for patterns that can backtrack catastrophically on hostile input.
    /// </summary>
    public int MatchTimeoutMilliseconds { get; init; }
}
