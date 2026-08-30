namespace ValidationModules;

/// <summary>
/// One helper per code in <see cref="ValidationCodes"/>, reporting the structured form of the
/// standard failure - code, attempted value, and the <see cref="ValidationMessageInfo"/> the
/// default message renders from. Each returns the <see cref="ValidationFlow"/> its collector
/// answered with, so a rule site can stop on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Emitting <c>"postalCode must be at most 100 characters."</c> at each
/// site was the single largest contributor to generated code size - measured at 107 of the 313
/// native bytes per constraint, because every message is unique and so nothing deduplicates in the
/// string heap. The text moved here first, composed on the failure path; it has now moved once
/// more, into <see cref="ValidationMessageTemplates"/>, and stopped being composed at all -
/// <see cref="ValidationError.Message"/> renders when something reads it, which is what lets one
/// result render per reader.
/// </para>
/// <para>
/// <b>What it costs.</b> Nothing on the success path, as ever. On the failure path the bounded
/// helpers allocate one <see cref="ValidationMessageInfo"/> and box their arguments - a hand-written
/// call's price. Generated sites do not pay it: their bounds are compile-time constants, so the
/// emitter hoists the info into a <c>static readonly</c> and calls
/// <c>ctx.Report(field, code, value, info)</c> directly, and the parameterless constraints share
/// the singletons on <see cref="ValidationMessageInfo"/> from every site.
/// </para>
/// <para>
/// <b>Why the receiver is a generic constrained to <see cref="IValidationContextReporter"/>.</b>
/// One home for the helpers, in sync forever across generated validators, hand-written validators,
/// <c>Apply</c> methods and rules-class bodies reporting through <c>rules.Context</c> - a new
/// helper lights up everywhere the day it is written. The constrained generic keeps the call a
/// non-boxing constrained call on the context struct, and call sites are textually what they were
/// (<c>ValidationContextExtensions.ReportRequired(ctx, "name")</c> infers
/// <c>TReporter = ValidationContext</c>). The receiver stays by value because an <c>in</c> or
/// <c>ref</c> receiver would refuse <c>context.Push("home").ReportRequired(...)</c> - the result
/// of a call is not addressable.
/// </para>
/// <para>
/// <b><c>value</c> is optional everywhere, and last.</b> Passing it is capture, not rendering -
/// the default render never includes it; only an installed
/// <see cref="ValidationMessageFormatter"/> can echo it, and
/// <c>ValidationModules_CaptureValues=false</c> stops generated sites passing it at all. It sits
/// after <c>severity</c> and <c>code</c> so every existing call keeps its spelling.
/// </para>
/// <para>
/// A constraint carrying an explicit <c>Message</c> bypasses all of this - the generator emits
/// <see cref="IValidationContextReporter.Report(string,string,string,ValidationSeverity)"/> with
/// the literal (its <c>{field}</c> substituted at generation time), because at that point the text
/// is one the author chose rather than one this library owns.
/// </para>
/// <para>
/// <b>Why each takes a code.</b> A <c>Code</c> without a <c>Message</c> beside it is the common
/// shape - errors.md calls the code a wire contract and the message prose - so overriding one must
/// not require overriding the other.
/// </para>
/// </remarks>
public static class ValidationContextExtensions {

    /// <summary>Records that a required value was missing.</summary>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportRequired<TReporter>(
        this TReporter context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(field, code ?? ValidationCodes.Required, value, ValidationMessageInfo.Required, severity);

    /// <summary>
    /// Records that a string fell outside its length bounds.
    /// </summary>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="min">The lower bound; zero means unbounded below.</param>
    /// <param name="max">The upper bound; <see cref="int.MaxValue"/> means unbounded above.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportStringLength<TReporter>(
        this TReporter context,
        string field,
        int min = 0,
        int max = int.MaxValue,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(
            field,
            code ?? ValidationCodes.StringLength,
            value,
            BoundedInfo(
                min, max,
                ValidationMessageTemplates.StringLengthBetween, ValidationMessageTemplates.StringLengthBetweenSingular,
                ValidationMessageTemplates.StringLengthAtMost, ValidationMessageTemplates.StringLengthAtMostSingular,
                ValidationMessageTemplates.StringLengthAtLeast, ValidationMessageTemplates.StringLengthAtLeastSingular),
            severity);

    /// <summary>
    /// Records that a collection fell outside its element-count bounds.
    /// </summary>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="min">The lower bound; zero means unbounded below.</param>
    /// <param name="max">The upper bound; <see cref="int.MaxValue"/> means unbounded above.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportItemCount<TReporter>(
        this TReporter context,
        string field,
        int min = 0,
        int max = int.MaxValue,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(
            field,
            code ?? ValidationCodes.ArrayBounds,
            value,
            BoundedInfo(
                min, max,
                ValidationMessageTemplates.ItemCountBetween, ValidationMessageTemplates.ItemCountBetweenSingular,
                ValidationMessageTemplates.ItemCountAtMost, ValidationMessageTemplates.ItemCountAtMostSingular,
                ValidationMessageTemplates.ItemCountAtLeast, ValidationMessageTemplates.ItemCountAtLeastSingular),
            severity);

    /// <summary>
    /// Records that a value was not an exact multiple of its divisor.
    /// </summary>
    /// <remarks>
    /// The divisor is a <c>decimal</c> whatever the member's type, because that is the domain the
    /// check is decided in and a message quoting a different number from the one the check used
    /// would be worse than no message.
    /// </remarks>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="divisor">The divisor the value had to be a multiple of.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportMultipleOf<TReporter>(
        this TReporter context,
        string field,
        decimal divisor,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(
            field,
            code ?? ValidationCodes.MultipleOf,
            value,
            new ValidationMessageInfo(ValidationMessageTemplates.MultipleOf, divisor),
            severity);

    /// <summary>
    /// Records that a collection contained the same element twice.
    /// </summary>
    /// <remarks>
    /// The offending element is deliberately not an argument. Finding it costs a second pass, and
    /// echoing a value the caller sent back into an error response is the habit
    /// <see cref="ReportPattern"/> avoids for the same reason.
    /// </remarks>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportUniqueItems<TReporter>(
        this TReporter context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(field, code ?? ValidationCodes.UniqueItems, value, ValidationMessageInfo.UniqueItems, severity);

    /// <summary>
    /// Records that a value fell outside its range.
    /// </summary>
    /// <remarks>
    /// Generic over the bound type rather than overloaded per numeric type, so an <c>int</c> or
    /// <c>DateTime</c> bound arrives typed and boxes only when a failure is actually recorded.
    /// The exclusivity flags pick the template that says what the check did -
    /// <c>ExclusiveMin</c>/<c>ExclusiveMax</c> were honoured in the comparison long before the
    /// message admitted it.
    /// </remarks>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="min">The lower bound.</param>
    /// <param name="max">The upper bound.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    /// <param name="exclusiveMin">Whether the lower bound itself is outside the permitted range.</param>
    /// <param name="exclusiveMax">Whether the upper bound itself is outside the permitted range.</param>
    public static ValidationFlow ReportRange<TReporter, T>(
        this TReporter context,
        string field,
        T min,
        T max,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null,
        bool exclusiveMin = false,
        bool exclusiveMax = false)
        where TReporter : IValidationContextReporter
        where T : IFormattable =>
        context.Report(
            field,
            code ?? ValidationCodes.Range,
            value,
            new ValidationMessageInfo(
                (exclusiveMin, exclusiveMax) switch {
                    (false, false) => ValidationMessageTemplates.RangeBetween,
                    (true, false) => ValidationMessageTemplates.RangeGreaterAndAtMost,
                    (false, true) => ValidationMessageTemplates.RangeAtLeastAndLess,
                    (true, true) => ValidationMessageTemplates.RangeGreaterAndLess,
                },
                min, max),
            severity);

    /// <summary>
    /// Records that a value fell below its lower bound, where no upper bound was declared.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ReportRange{TReporter, T}"/> rather than passing the type's extreme
    /// as the absent bound. The extreme is not a bound anyone wrote, and rendering it quotes it
    /// back to the caller - a specification setting only <c>minimum</c> produced "must be between
    /// 1 and 7.9228162514264338E+28" in a 400 body. Same code, because the failure is the same one
    /// and a client switching on it should not have to learn a second.
    /// </remarks>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="min">The lower bound.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    /// <param name="exclusive">Whether the bound itself is outside the permitted range.</param>
    public static ValidationFlow ReportRangeAtLeast<TReporter, T>(
        this TReporter context,
        string field,
        T min,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null,
        bool exclusive = false)
        where TReporter : IValidationContextReporter
        where T : IFormattable =>
        context.Report(
            field,
            code ?? ValidationCodes.Range,
            value,
            new ValidationMessageInfo(
                exclusive ? ValidationMessageTemplates.RangeGreaterThan : ValidationMessageTemplates.RangeAtLeast,
                min),
            severity);

    /// <summary>
    /// Records that a value rose above its upper bound, where no lower bound was declared.
    /// </summary>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="max">The upper bound.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    /// <param name="exclusive">Whether the bound itself is outside the permitted range.</param>
    public static ValidationFlow ReportRangeAtMost<TReporter, T>(
        this TReporter context,
        string field,
        T max,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null,
        bool exclusive = false)
        where TReporter : IValidationContextReporter
        where T : IFormattable =>
        context.Report(
            field,
            code ?? ValidationCodes.Range,
            value,
            new ValidationMessageInfo(
                exclusive ? ValidationMessageTemplates.RangeLessThan : ValidationMessageTemplates.RangeAtMost,
                max),
            severity);

    /// <summary>
    /// Records that a string did not match its pattern.
    /// </summary>
    /// <remarks>
    /// The pattern itself is deliberately not an argument. It is an implementation detail of the
    /// contract rather than something a caller can act on, and echoing it back leaks the shape of
    /// the validation to anyone probing the endpoint.
    /// </remarks>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportPattern<TReporter>(
        this TReporter context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(field, code ?? ValidationCodes.Pattern, value, ValidationMessageInfo.Pattern, severity);

    /// <summary>
    /// Records that a value was not one of the permitted set.
    /// </summary>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="allowedValues">
    /// The permitted values, already joined - <c>"available, pending, sold"</c>. The set is a
    /// compile-time constant, so the generator emits the joined form once rather than joining an
    /// array on every failure.
    /// </param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportAllowedValues<TReporter>(
        this TReporter context,
        string field,
        string allowedValues,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(
            field,
            code ?? ValidationCodes.Enum,
            value,
            new ValidationMessageInfo(ValidationMessageTemplates.AllowedValues, allowedValues),
            severity);

    /// <summary>
    /// Records that a value was one of the forbidden set.
    /// </summary>
    /// <remarks>
    /// Its own helper rather than <see cref="ReportAllowedValues"/> negated at the check and reused
    /// for the message: that reuse told the caller to enter one of the very values they must not,
    /// which was a bug wearing a message's clothes. The code stays
    /// <see cref="ValidationCodes.Enum"/> - the wire does not distinguish the two shapes, and a
    /// client already switching on it must not have to learn a second code for cosmetics.
    /// </remarks>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="deniedValues">The forbidden values, already joined - <c>"admin, root"</c>.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportDeniedValues<TReporter>(
        this TReporter context,
        string field,
        string deniedValues,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(
            field,
            code ?? ValidationCodes.Enum,
            value,
            new ValidationMessageInfo(ValidationMessageTemplates.DeniedValues, deniedValues),
            severity);

    /// <summary>
    /// Records that a value was not an email address.
    /// </summary>
    /// <remarks>
    /// The format family's messages state what the value was not, like <see cref="ReportPattern"/>,
    /// and never render the value itself - the same probing-and-logging argument made there. The
    /// <paramref name="value"/> parameter captures without rendering, as everywhere.
    /// </remarks>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportEmail<TReporter>(
        this TReporter context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(field, code ?? ValidationCodes.Email, value, ValidationMessageInfo.Email, severity);

    /// <summary>
    /// Records that a value was not a phone number.
    /// </summary>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportPhone<TReporter>(
        this TReporter context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(field, code ?? ValidationCodes.Phone, value, ValidationMessageInfo.Phone, severity);

    /// <summary>
    /// Records that a value was not an http, https or ftp URL.
    /// </summary>
    /// <remarks>
    /// The message names the accepted schemes because they are the whole check - a caller sent
    /// something like <c>www.example.com</c> often enough that "not a valid URL" alone reads as
    /// wrong to them.
    /// </remarks>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportUrl<TReporter>(
        this TReporter context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(field, code ?? ValidationCodes.Url, value, ValidationMessageInfo.Url, severity);

    /// <summary>
    /// Records that a value failed the credit card checksum.
    /// </summary>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportCreditCard<TReporter>(
        this TReporter context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(field, code ?? ValidationCodes.CreditCard, value, ValidationMessageInfo.CreditCard, severity);

    /// <summary>
    /// Records that a value was not well-formed Base64.
    /// </summary>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportBase64<TReporter>(
        this TReporter context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(field, code ?? ValidationCodes.Base64, value, ValidationMessageInfo.Base64, severity);

    /// <summary>
    /// Records that a file name's extension was not in the permitted set.
    /// </summary>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="extensions">
    /// The permitted extensions, already joined - <c>".png, .jpg"</c>. A compile-time constant,
    /// so the generator emits the joined form once rather than joining an array on every failure -
    /// the same arrangement as <see cref="ReportAllowedValues"/>.
    /// </param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportFileExtension<TReporter>(
        this TReporter context,
        string field,
        string extensions,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(
            field,
            code ?? ValidationCodes.FileExtension,
            value,
            new ValidationMessageInfo(ValidationMessageTemplates.FileExtension, extensions),
            severity);

    /// <summary>
    /// Records that a custom constraint's check failed.
    /// </summary>
    /// <remarks>
    /// The default message is deliberately terse - the check is yours, so only you can say what
    /// "valid" meant. A <c>DefaultMessage</c> constant baked on the attribute class, or a
    /// <c>Message</c> at the use site, replaces it - declaring one of them is the recommendation,
    /// not an edge case.
    /// </remarks>
    /// <param name="context">The reporter to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="code">Overrides the default code for this check. Null keeps it.</param>
    /// <param name="value">The attempted value, captured for readers that opt in. Null captures nothing.</param>
    public static ValidationFlow ReportCustom<TReporter>(
        this TReporter context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? code = null,
        object? value = null)
        where TReporter : IValidationContextReporter =>
        context.Report(field, code ?? ValidationCodes.Custom, value, ValidationMessageInfo.Custom, severity);

    /// <summary>
    /// Picks the between / at-most / at-least template - and its singular form when the deciding
    /// bound is one - matching the shape logic the composed <c>BoundsMessage</c> always had, so
    /// the rendered default is byte-identical to what used to be composed.
    /// </summary>
    private static ValidationMessageInfo BoundedInfo(
        int min, int max,
        string between, string betweenSingular,
        string atMost, string atMostSingular,
        string atLeast, string atLeastSingular) {
        var bounded = max != int.MaxValue;

        if (min > 0 && bounded) {
            return new ValidationMessageInfo(max == 1 ? betweenSingular : between, min, max);
        }

        if (bounded) {
            return new ValidationMessageInfo(max == 1 ? atMostSingular : atMost, max);
        }

        return new ValidationMessageInfo(min == 1 ? atLeastSingular : atLeast, min);
    }
}
