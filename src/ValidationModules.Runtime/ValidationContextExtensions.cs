using System.Globalization;

namespace ValidationModules;

/// <summary>
/// One helper per code in <see cref="ValidationCodes"/>, composing the standard message so that
/// the generator does not have to emit it as a literal at every constraint site.
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
/// their own <c>AddSomething</c> in the same shape and it reads identically to the built-ins, which
/// would not be true of instance methods. They take the context by value because an <c>in</c> or
/// <c>ref</c> receiver would refuse <c>context.Push("home").AddRequired(...)</c> - the result of a
/// call is not addressable. The copy is wider than it looks (the context is seven words, not the
/// two it was when it held a node index), but every one of these runs on the failure path, where a
/// register shuffle is lost against composing the message that follows it.
/// </para>
/// <para>
/// A constraint carrying an explicit <c>Message</c> bypasses all of this - the generator emits
/// <see cref="ValidationContext.Add"/> with the literal, because at that point the text is one the
/// author chose rather than one this file owns.
/// </para>
/// </remarks>
public static class ValidationContextExtensions {

    /// <summary>Records that a required value was missing.</summary>
    public static void AddRequired(
        this ValidationContext context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Add(field, ValidationCodes.Required, string.Concat(field, " is required."), severity);

    /// <summary>
    /// Records that a string fell outside its length bounds.
    /// </summary>
    /// <param name="context">The context to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="min">The lower bound; zero means unbounded below.</param>
    /// <param name="max">The upper bound; <see cref="int.MaxValue"/> means unbounded above.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public static void AddStringLength(
        this ValidationContext context,
        string field,
        int min = 0,
        int max = int.MaxValue,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Add(field, ValidationCodes.StringLength, BoundsMessage(field, min, max, "character"), severity);

    /// <summary>
    /// Records that a collection fell outside its element-count bounds.
    /// </summary>
    /// <param name="context">The context to record against.</param>
    /// <param name="field">The field name.</param>
    /// <param name="min">The lower bound; zero means unbounded below.</param>
    /// <param name="max">The upper bound; <see cref="int.MaxValue"/> means unbounded above.</param>
    /// <param name="severity">Defaults to <see cref="ValidationSeverity.Error"/>.</param>
    public static void AddItemCount(
        this ValidationContext context,
        string field,
        int min = 0,
        int max = int.MaxValue,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Add(field, ValidationCodes.ArrayBounds, BoundsMessage(field, min, max, "item"), severity);

    /// <summary>
    /// Records that a value fell outside its range.
    /// </summary>
    /// <remarks>
    /// Generic over the bound type rather than overloaded per numeric type: the
    /// <see cref="IFormattable"/> constraint means the <c>ToString</c> is a constrained call, so an
    /// <c>int</c> or <c>DateTime</c> bound formats without boxing. Invariant culture, because an
    /// error code's message is a wire format rather than prose.
    /// </remarks>
    public static void AddRange<T>(
        this ValidationContext context,
        string field,
        T min,
        T max,
        ValidationSeverity severity = ValidationSeverity.Error)
        where T : IFormattable =>
        context.Add(
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
    /// Records that a string did not match its pattern.
    /// </summary>
    /// <remarks>
    /// The pattern itself is deliberately not in the message. It is an implementation detail of the
    /// contract rather than something a caller can act on, and echoing it back leaks the shape of
    /// the validation to anyone probing the endpoint.
    /// </remarks>
    public static void AddPattern(
        this ValidationContext context,
        string field,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Add(
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
    public static void AddAllowedValues(
        this ValidationContext context,
        string field,
        string allowedValues,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        context.Add(
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
