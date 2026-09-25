namespace ValidationModules.SourceGenerator.Impl.Models;

/// <summary>
/// What to do about a <c>[Pattern]</c> declared with an inline expression.
/// </summary>
/// <remarks>
/// The inline form is correct and AOT-clean, but constructing a Regex from a pattern string roots
/// the regex parser and interpreter, which makes a published AOT binary 356 KB larger than the
/// same pattern reached through a consumer-declared [GeneratedRegex]. Options or a timeout add
/// 486 KB more. So this is not a correctness gate; it is a size one, and the message says so.
/// </remarks>
public enum PatternPolicy
{
    /// <summary>Reject the inline form when the project is AOT-facing, allow it otherwise.</summary>
    Auto,

    /// <summary>Always reject it, so the failure lands in the library's own build rather than at an app's publish.</summary>
    Error,

    /// <summary>Report it and carry on.</summary>
    Warn,

    /// <summary>Accept it, for a project that genuinely wants the interpreted engine.</summary>
    Allow,
}
