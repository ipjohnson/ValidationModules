namespace ValidationModules;

/// <summary>
/// Whether a validation pass carries on after a failure was recorded.
/// </summary>
/// <remarks>
/// <para>
/// Returned by everything that records an error - <see cref="ValidationContext.Report(string,string,string,ValidationSeverity)"/>,
/// the <c>Report*</c> helpers, and <see cref="IValidatorFor{T}.Validate"/> - so a rule site can
/// stop without knowing why it is stopping. The decision belongs to
/// <see cref="ValidationErrorCollector.StopMode"/>; a rule site only obeys it.
/// </para>
/// <para>
/// <b>A struct rather than a <see langword="bool"/>.</b> The value answers "does the pass
/// continue", but every method returning it is named after a failure, so a bare bool reads as
/// whether the recording succeeded - and <c>if (!ctx.ReportRequired("Name"))</c> is a double
/// negative a reader has to decode. <c>.ShouldStop</c> cannot be misread. It wraps one field and is
/// returned in a register, so the codegen is what a bool would have produced.
/// </para>
/// <para>
/// <b><see langword="default"/> is <see cref="Continue"/>.</b> A defaulted flow, or one from a
/// hand-written rule that fell off the end of a branch, keeps validating rather than silently
/// truncating the pass. The safe direction: a missed stop costs the work a
/// <see cref="ValidationStopMode.CollectAll"/> pass would have done anyway, while a spurious stop
/// would drop errors the caller needed.
/// </para>
/// <para>
/// There is deliberately no conversion to <see cref="bool"/>. One would put the double negative
/// straight back at every call site that has an implicit conversion available.
/// </para>
/// </remarks>
public readonly struct ValidationFlow : IEquatable<ValidationFlow> {

    private readonly bool _stop;

    private ValidationFlow(bool stop) => _stop = stop;

    /// <summary>Keep evaluating the remaining rules. The default.</summary>
    public static ValidationFlow Continue => default;

    /// <summary>Stop this pass; the caller has what it asked for.</summary>
    public static ValidationFlow Stop => new(true);

    /// <summary>Whether the caller should return without evaluating anything further.</summary>
    public bool ShouldStop => _stop;

    /// <inheritdoc/>
    public bool Equals(ValidationFlow other) => _stop == other._stop;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ValidationFlow other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _stop.GetHashCode();

    /// <summary><c>Stop</c> or <c>Continue</c>, so a failing assertion names which it was.</summary>
    public override string ToString() => _stop ? nameof(Stop) : nameof(Continue);

    /// <summary>Whether two flows say the same thing.</summary>
    public static bool operator ==(ValidationFlow left, ValidationFlow right) => left.Equals(right);

    /// <summary>Whether two flows disagree.</summary>
    public static bool operator !=(ValidationFlow left, ValidationFlow right) => !left.Equals(right);
}
