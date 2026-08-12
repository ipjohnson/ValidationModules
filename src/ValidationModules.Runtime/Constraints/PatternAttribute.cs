using System.Text.RegularExpressions;

namespace ValidationModules.Constraints;

/// <summary>
/// The string must match a regular expression. Emits code <c>pattern</c>.
/// </summary>
/// <remarks>
/// Compiles to a <c>[GeneratedRegex]</c> partial method on the generated validator. There is no
/// code path in this library that constructs a <see cref="Regex"/> at runtime - <c>new Regex(...,
/// RegexOptions.Compiled)</c> emits IL through <c>Reflection.Emit</c>, which is both a per-call
/// cost and an AOT failure, and is one of the concrete defects this library was written to remove.
/// </remarks>
public sealed class PatternAttribute : ValidationConstraintAttribute {

    /// <summary>
    /// The pattern. Must be a compile-time constant, and is validated at generation time.
    /// </summary>
    /// <param name="pattern">A .NET regular expression.</param>
    public PatternAttribute(string pattern) {
        ArgumentNullException.ThrowIfNull(pattern);

        Pattern = pattern;
    }

    /// <summary>The pattern.</summary>
    public string Pattern { get; }

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
