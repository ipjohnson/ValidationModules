namespace ValidationModules;

/// <summary>
/// The contract a constraint attribute of your own implements when its check wants an instance:
/// state precomputed in the constructor, the pass's context, its own codes and messages, or more
/// than one error from one check. Implement <see cref="IsValid"/>; override <see cref="Validate"/>
/// only to own the reporting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where this sits among the custom shapes.</b> <c>CustomConstraintAttribute</c> is the
/// cheapest: a static check compiled into the validator like a built-in, no instance anywhere. A
/// DataAnnotations <c>ValidationAttribute</c> subclass is the compatibility shape: invoked with
/// DataAnnotations semantics and DataAnnotations costs. This is the shape in between - the
/// generator constructs the attribute once from its declaration, holds it in a static field on the
/// validator, and weaves direct calls to it - for the check that a static method cannot express
/// because it needs something built once and kept: a lookup table from the constructor's
/// arguments, a <c>SearchValues</c>, a handle to something external.
/// </para>
/// <para>
/// <b>Two members because the validator has two paths.</b> Generated validators answer
/// <c>IsValid</c> without building a collector, a path that allocates nothing; a contract with
/// only a reporting method could not appear on it. <see cref="IsValid"/> is that path's form of
/// the check and must return the blocking verdict: false when an error would be reported, true
/// otherwise - a check that would report only a <see cref="ValidationSeverity.Warning"/> is not
/// invalid and must return true. <see cref="Validate"/> is the reporting path, and its default
/// implementation keeps the two consistent by asking <see cref="IsValid"/> and reporting code
/// <see cref="ValidationCodes.Custom"/> with the terse composed message. An override owes the same
/// agreement: report a blocking error exactly when <see cref="IsValid"/> says false.
/// </para>
/// <para>
/// <b>One instance, shared and called concurrently.</b> The generator constructs the attribute
/// once per declaration site and every validation pass in the process uses that instance, so a
/// conforming implementation is immutable after construction. A check that must keep per-call
/// state in fields - wrapping something genuinely not thread-safe - opts out with
/// <c>[PerValidationInstance]</c> on the attribute class, and the generator constructs a fresh
/// instance at every check instead, stating the allocation with an Info at each use site.
/// </para>
/// <para>
/// <b>Null never arrives.</b> The generated guard skips the check on a null member, as it does for
/// every constraint except <c>[Required]</c>, and a nullable value type is unwrapped - a
/// <c>decimal?</c> member matches <c>IConstraintFor&lt;decimal&gt;</c>. Declare <c>[Required]</c>
/// beside the attribute when absence should fail.
/// </para>
/// <para>
/// <b>The base knobs work, split between the two engines.</b> An attribute that also derives from
/// <c>ValidationModules.Constraints.ValidationConstraintAttribute</c> gets <c>When</c> and
/// <c>Unless</c> enforced by the generator, outside the call. <c>Code</c> and <c>Message</c> ride
/// into the instance like any other property and are honoured by the default
/// <see cref="Validate"/> - <c>{field}</c> in a message is substituted when the failure is
/// reported, since the instance is shared across fields. An override that means to support them
/// reads its own properties.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class AllowedSchemeAttribute : Attribute, IConstraintFor&lt;Uri&gt; {
///     private readonly string[] _schemes;
///
///     public AllowedSchemeAttribute(params string[] schemes) { _schemes = schemes; }
///
///     public bool IsValid(Uri value) =&gt; Array.IndexOf(_schemes, value.Scheme) &gt;= 0;
///
///     public ValidationFlow Validate(ref ValidationContext context, Uri value, string field) =&gt;
///         IsValid(value)
///             ? ValidationFlow.Continue
///             : context.Report(field, "scheme", $"{field} must use {string.Join(" or ", _schemes)}.");
/// }
///
/// public record Link {
///     [AllowedScheme("https")]
///     public Uri? Target { get; init; }
/// }
/// </code>
/// </example>
/// <typeparam name="T">
/// The member type the check reads. An attribute may implement several instantiations; the
/// generator picks the member's own type, or the single implemented one the member converts to.
/// </typeparam>
public interface IConstraintFor<in T> {

    /// <summary>
    /// The verdict, with nothing recorded: false when the value would fail with an
    /// <see cref="ValidationSeverity.Error"/>, true otherwise. Never called with null.
    /// </summary>
    /// <param name="value">The member's value.</param>
    bool IsValid(T value);

    /// <summary>
    /// The reporting form of the check. The default asks <see cref="IsValid"/> and reports
    /// <see cref="ValidationCodes.Custom"/>, honouring a <c>Code</c> or <c>Message</c> declared
    /// through <c>ValidationConstraintAttribute</c>; override it to report your own code, message,
    /// severity, or more than one error.
    /// </summary>
    /// <param name="context">The pass to report into.</param>
    /// <param name="value">The member's value. Never null.</param>
    /// <param name="field">The wire field name errors report under.</param>
    ValidationFlow Validate(ref ValidationContext context, T value, string field) {
        if (IsValid(value)) {
            return ValidationFlow.Continue;
        }

        // The declaration's Code and Message arrive as ordinary properties on this instance when
        // the attribute derives from the constraint base; honouring them here is what makes the
        // knobs behave identically across every custom shape. The substitution happens now rather
        // than at generation time because the instance is shared across every field it is declared
        // on - and only a failing value pays for it.
        return this is Constraints.ValidationConstraintAttribute declared
            ? declared.Message is { } message
                ? context.Report(
                    field, declared.Code ?? ValidationCodes.Custom, message.Replace("{field}", field))
                : context.ReportCustom(field, code: declared.Code)
            : context.ReportCustom(field);
    }
}
