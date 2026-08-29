namespace ValidationModules;

/// <summary>
/// Turns a <see cref="ValidationError"/> into text, in place of the default render. Installed by
/// readers - a problem-details writer, a logging boundary, a UI - never by the validation pass:
/// errors carry data, and which language or how much detail a message shows is decided where it is
/// read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Permissiveness is this type's decision.</b> The default render never includes
/// <see cref="ValidationError.Value"/> - not in <see cref="ValidationError.Message"/>, not in
/// <c>ToString</c>, not in the problem-details body. A formatter sees the whole error and may
/// choose to echo the value ("'SUMMER!!' is not in the required format"), which makes it the
/// explicit reopening point for that decision - the same posture the reporter tier takes for
/// values in hand-written messages. A diagnostics formatter registered only in Development is the
/// intended shape.
/// </para>
/// <para>
/// <b>Culture is ambient or yours - never stored.</b> An error does not remember a culture and a
/// formatter is consulted at read time, so <c>CultureInfo.CurrentUICulture</c> (which flows across
/// awaits and is set per request by localization middleware) or an explicit culture the formatter
/// holds are both correct. Storing culture on the error would freeze the very decision late
/// rendering exists to defer.
/// </para>
/// <para>
/// Errors whose <see cref="ValidationError.MessageInfo"/> is null carry only their finished
/// message - the FluentValidation adapter, DataAnnotations' invoked user code, hand-written
/// <c>Report(field, code, message)</c> calls. A formatter can still rewrite them by code, but has
/// no arguments to build from; falling back to <see cref="ValidationError.Message"/> is the
/// ordinary answer.
/// </para>
/// </remarks>
public abstract class ValidationMessageFormatter {

    /// <summary>
    /// The message for <paramref name="error"/>. Implementations answer for every error they are
    /// handed; <see cref="ValidationError.Message"/> is the fallback with nothing to override.
    /// </summary>
    /// <param name="error">The error to render.</param>
    public abstract string Format(in ValidationError error);
}
