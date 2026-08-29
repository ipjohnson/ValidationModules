namespace ValidationModules;

/// <summary>
/// The narrow reporting view of a validation pass: record a failure, learn whether to stop.
/// </summary>
/// <remarks>
/// <para>
/// What free-form code in a rules class reaches through <c>rules.Context</c>, and the constraint
/// the <c>Report*</c> helpers in <see cref="ValidationContextExtensions"/> bind to - one home for
/// the helpers across generated validators, hand-written validators, <c>Apply</c> methods and
/// rules-class bodies, so a new helper lights up everywhere the day it is written.
/// <see cref="ValidationContext"/> implements it with the members it already had.
/// </para>
/// <para>
/// <b>Deliberately absent:</b> <c>Push*</c>, <c>HasErrors</c>, <c>ErrorCount</c>, <c>Services</c>,
/// <c>StopMode</c>. Escalation for structural work is <c>Nested</c>/<c>Each</c>, then <c>Apply</c>,
/// which reaches the full context. A read can be added later, additively, if real demand shows.
/// </para>
/// <para>
/// <b>Values may reach messages here.</b> The composed vocabulary and <c>Ensure</c> are
/// redaction-safe by construction - no runtime value can reach their text. <c>Report</c>
/// deliberately reopens that, at an explicit call site, on the author's head; that is the point of
/// the tier.
/// </para>
/// </remarks>
public interface IValidationContextReporter {

    /// <summary>
    /// Records a failure against a field of the current object, and answers whether the pass
    /// carries on.
    /// </summary>
    /// <param name="field">The field name, appended to the current path.</param>
    /// <param name="code">A stable machine-readable code - see the vocabulary in API-SURFACE.md §4.1.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    ValidationFlow Report(
        string field,
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error);

    /// <summary>
    /// Records a failure against the current object itself, for type-level and cross-field rules.
    /// </summary>
    /// <param name="code">A stable machine-readable code.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    ValidationFlow ReportHere(
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error);
}
