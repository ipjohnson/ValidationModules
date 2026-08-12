namespace ValidationModules;

/// <summary>
/// How badly a <see cref="ValidationError"/> failed.
/// </summary>
/// <remarks>
/// The names and values match FluentValidation's <c>Severity</c> exactly, which makes the adapter's
/// mapping a cast rather than a table, and puts <see cref="Error"/> at <c>default</c>.
/// </remarks>
public enum ValidationSeverity {

    /// <summary>The value is invalid. Only this severity makes <see cref="ValidationResult.IsValid"/> false.</summary>
    Error = 0,

    /// <summary>Worth surfacing, but the value is accepted.</summary>
    Warning = 1,

    /// <summary>Informational only.</summary>
    Info = 2,
}
