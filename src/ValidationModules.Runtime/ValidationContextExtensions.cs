using System.Globalization;

namespace ValidationModules;

/// <summary>
/// One helper per code in <see cref="ValidationCodes"/>, composing the standard message so that
/// the generator does not have to emit it as a literal at every constraint site. Each returns the
/// <see cref="ValidationFlow"/> its collector answered with, so a rule site can stop on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Emitting <c>"postalCode must be at most 100 characters."</c> at each
/// site is the single largest contributor to generated code size - measured at 107 of the 313
/// native bytes per constraint, because every message is unique and so nothing deduplicates in the
/// string heap. Moving the text here removes it from metadata entirely.
/// </para>
/// <para>
/// <b>What it costs.</b> Composition allocates the message on the failure path - around 56 bytes
/// per error, against zero for a literal. Nothing on the success path, which is where production
/// traffic spends its time, and it lands immediately before a 400 response whose serialization
/// allocates considerably more.
/// </para>
/// <para>
/// <b>Why extensions rather than methods on the context.</b> A consumer with a custom code can add
/// their own <c>ReportSomething</c> in the same shape and it reads identically to the built-ins, which
/// would not be true of instance methods. They take the context by value because an <c>in</c> or
/// <c>ref</c> receiver would refuse <c>context.Push("home").ReportRequired(...)</c> - the result of a
/// call is not addressable. The copy is wider than it looks (the context is seven words, not the
/// two it was when it held a node index), but every one of these runs on the failure path, where a
/// register shuffle is lost against composing the message that follows it.
/// </para>
/// <para>
/// A constraint carrying an explicit <c>Message</c> bypasses all of this - the generator emits
/// <see cref="ValidationContext.Report(string,string,string,ValidationSeverity)"/> with the literal, because at that point the text is one the
/// author chose rather than one this file owns.
/// </para>
/// </remarks>
public static class ValidationContextExtensions {

    /// <summary>Records that a required value was missing.</summary>
    public static ValidationFlow ReportRequired(
        this ValidationContext context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Report(field, ValidationCodes.Required, string.Concat(field, " is required."), severity);

    /// <summary>
    /// Records that a string fell outside its length bounds.
    /// </summary>
    /// <param name="context">The context to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="min">The lower bound; zero means unbounded below.</param>
    /// <param name="max">The upper bound; <see cref="int.MaxValue"/> means unbounded above.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public static ValidationFlow ReportStringLength(
        this ValidationContext context,
        string field,
        int min = 0,
        int max = int.MaxValue,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Report(field, ValidationCodes.StringLength, BoundsMessage(field, min, max, "character"), severity);

    /// <summary>
    /// Records that a collection fell outside its element-count bounds.
    /// </summary>
    /// <param name="context">The context to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="min">The lower bound; zero means unbounded below.</param>
    /// <param name="max">The upper bound; <see cref="int.MaxValue"/> means unbounded above.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public static ValidationFlow ReportItemCount(
        this ValidationContext context,
        string field,
        int min = 0,
        int max = int.MaxValue,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Report(field, ValidationCodes.ArrayBounds, BoundsMessage(field, min, max, "item"), severity);

    /// <summary>
    /// Records that a value was not an exact multiple of its divisor.
    /// </summary>
    /// <remarks>
    /// The divisor is a <c>decimal</c> whatever the member's type, because that is the domain the
    /// check is decided in and a message quoting a different number from the one the check used
    /// would be worse than no message. Invariant culture, as everywhere else here.
    /// </remarks>
    /// <param name="context">The context to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="divisor">The divisor the value had to be a multiple of.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public static ValidationFlow ReportMultipleOf(
        this ValidationContext context,
        string field,
        decimal divisor,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Report(
            field,
            ValidationCodes.MultipleOf,
            string.Concat(
                field,
                " must be a multiple of ",
                divisor.ToString(CultureInfo.InvariantCulture),
                "."),
            severity);

    /// <summary>
    /// Records that a collection contained the same element twice.
    /// </summary>
    /// <remarks>
    /// The offending element is deliberately not in the message. Finding it costs a second pass, and
    /// echoing a value the caller sent back into an error response is the habit
    /// <see cref="ReportPattern"/> avoids for the same reason.
    /// </remarks>
    public static ValidationFlow ReportUniqueItems(
        this ValidationContext context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Report(
            field,
            ValidationCodes.UniqueItems,
            string.Concat(field, " must not contain duplicate items."),
            severity);

    /// <summary>
    /// Records that a value fell outside its range.
    /// </summary>
    /// <remarks>
    /// Generic over the bound type rather than overloaded per numeric type: the
    /// <see cref="IFormattable"/> constraint means the <c>ToString</c> is a constrained call, so an
    /// <c>int</c> or <c>DateTime</c> bound formats without boxing. Invariant culture, because an
    /// error code's message is a wire format rather than prose.
    /// </remarks>
    public static ValidationFlow ReportRange<T>(
        this ValidationContext context,
        string field,
        T min,
        T max,
        ValidationSeverity severity = ValidationSeverity.Error)
        where T : IFormattable =>
        context.Report(
            field,
            ValidationCodes.Range,
            string.Concat(
                field,
                " must be between ",
                min.ToString(null, CultureInfo.InvariantCulture),
                " and ",
                max.ToString(null, CultureInfo.InvariantCulture),
                "."),
            severity);

    /// <summary>
    /// Records that a value fell below its lower bound, where no upper bound was declared.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ReportRange{T}"/> rather than passing the type's extreme as the
    /// absent bound. The extreme is not a bound anyone wrote, and composing it into the message
    /// quotes it back to the caller - a specification setting only <c>minimum</c> produced
    /// "must be between 1 and 7.9228162514264338E+28" in a 400 body. Same code, because the failure
    /// is the same one and a client switching on it should not have to learn a second.
    /// </remarks>
    public static ValidationFlow ReportRangeAtLeast<T>(
        this ValidationContext context,
        string field,
        T min,
        ValidationSeverity severity = ValidationSeverity.Error)
        where T : IFormattable =>
        context.Report(
            field,
            ValidationCodes.Range,
            string.Concat(field, " must be at least ", min.ToString(null, CultureInfo.InvariantCulture), "."),
            severity);

    /// <summary>
    /// Records that a value rose above its upper bound, where no lower bound was declared.
    /// </summary>
    public static ValidationFlow ReportRangeAtMost<T>(
        this ValidationContext context,
        string field,
        T max,
        ValidationSeverity severity = ValidationSeverity.Error)
        where T : IFormattable =>
        context.Report(
            field,
            ValidationCodes.Range,
            string.Concat(field, " must be at most ", max.ToString(null, CultureInfo.InvariantCulture), "."),
            severity);

    /// <summary>
    /// Records that a string did not match its pattern.
    /// </summary>
    /// <remarks>
    /// The pattern itself is deliberately not in the message. It is an implementation detail of the
    /// contract rather than something a caller can act on, and echoing it back leaks the shape of
    /// the validation to anyone probing the endpoint.
    /// </remarks>
    public static ValidationFlow ReportPattern(
        this ValidationContext context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Report(
            field,
            ValidationCodes.Pattern,
            string.Concat(field, " is not in the required format."),
            severity);

    /// <summary>
    /// Records that a value was not one of the permitted set.
    /// </summary>
    /// <param name="context">The context to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="allowedValues">
    /// The permitted values, already joined - <c>"available, pending, sold"</c>. The set is a
    /// compile-time constant, so the generator emits the joined form once as a static field rather
    /// than joining an array on every failure.
    /// </param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public static ValidationFlow ReportAllowedValues(
        this ValidationContext context,
        string field,
        string allowedValues,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Report(
            field,
            ValidationCodes.Enum,
            string.Concat(field, " must be one of: ", allowedValues, "."),
            severity);

    /// <summary>
    /// Builds the at-least / at-most / between message shared by the two bounded constraints,
    /// including the singular-plural switch that reads wrong often enough to be worth centralizing.
    /// </summary>
    private static string BoundsMessage(string field, int min, int max, string noun) {
        var bounded = max != int.MaxValue;

        if (min > 0 && bounded) {
            return string.Concat(
                field, " must be between ", min.ToString(CultureInfo.InvariantCulture),
                " and ", max.ToString(CultureInfo.InvariantCulture), " ", Plural(max, noun), ".");
        }

        if (bounded) {
            return string.Concat(
                field, " must be at most ", max.ToString(CultureInfo.InvariantCulture),
                " ", Plural(max, noun), ".");
        }

        return string.Concat(
            field, " must be at least ", min.ToString(CultureInfo.InvariantCulture),
            " ", Plural(min, noun), ".");
    }

    private static string Plural(int count, string noun) => count == 1 ? noun : string.Concat(noun, "s");
}
