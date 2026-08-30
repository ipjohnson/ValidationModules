namespace ValidationModules.Constraints;

/// <summary>
/// The value must read as a web address. On a string: it must start with <c>http://</c>,
/// <c>https://</c> or <c>ftp://</c>, case-insensitively, and nothing past the prefix is checked.
/// On a <see cref="Uri"/> member: it must be absolute with one of those three schemes. Emits code
/// <c>url</c>.
/// </summary>
/// <remarks>
/// The name and the semantics are <c>System.ComponentModel.DataAnnotations</c>' own - see
/// <c>ConstraintChecks.IsUrl</c>, where both overloads are pinned against the BCL attribute - so
/// migrating a model is swapping a using directive. The prefix check is genuinely that loose; a
/// rule that wants a real grammar is a <see cref="PatternAttribute"/>, or a <see cref="Uri"/>
/// member.
/// </remarks>
/// <example>
/// <code>
/// [Url] public string? Homepage { get; init; }
/// </code>
/// </example>
public sealed class UrlAttribute : ValidationConstraintAttribute;
