namespace ValidationModules;

/// <summary>
/// One validation failure: where it happened, what rule it was, what value arrived, and the
/// ingredients of what to tell a human. The message is data until something reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two shapes, told apart by <see cref="MessageInfo"/>.</b> A structured error - what every
/// generated constraint site and every <c>Report*</c> helper produces - carries the constraint's
/// <see cref="ValidationMessageInfo"/> and renders <see cref="Message"/> on read, so one result
/// can render per reader: default English in a log, a translation at the HTTP boundary, the code
/// and <see cref="ValidationMessageInfo.Args"/> verbatim for a client that renders its own text.
/// A finished-string error - the <see cref="ValidationError(string, string, string)"/> shape the
/// FluentValidation adapter, DataAnnotations' invoked user code and hand-written
/// <c>Report(field, code, message)</c> calls produce - carries only its text, visibly:
/// <see cref="MessageInfo"/> is null and no reader can re-render it.
/// </para>
/// <para>
/// <b>The three-argument constructor is the compatibility shape.</b> It matches the record
/// Hardened already constructs, so retargeting it onto this library is not a source change; only
/// the positional-record spelling went away, because a stored positional <c>Message</c> and a
/// rendered one cannot coexist.
/// </para>
/// <para>
/// <b><see cref="Value"/> is captured, never rendered here.</b> Not by <see cref="Message"/>, not
/// by <see cref="ToString"/>, not by the problem-details writer - only an installed
/// <see cref="ValidationMessageFormatter"/> can choose to echo it, which makes that choice an
/// explicit one at a named place. Builds that must not carry values at all set
/// <c>ValidationModules_CaptureValues=false</c>, and the generator emits no capture - absent from
/// the binary, which is a stronger guarantee than any runtime switch. Serializing this struct with
/// your own serializer is the one path the library cannot police; its own JSON context does not
/// carry <see cref="Value"/>.
/// </para>
/// <para>
/// <b>Equality is field equality, as it always was.</b> Two failures from the same constraint
/// site compare equal - same shared info reference, and a boxed primitive value compares by value
/// through <c>EqualityComparer&lt;object&gt;</c>. Failures of the same rule declared at two
/// different sites now differ by <see cref="MessageInfo"/> reference even when their rendered text
/// matches; they are different rule sites, and the previous text-based equality was the accident.
/// </para>
/// <para>
/// <see cref="Message"/> renders per read - a readonly struct has nowhere to cache - which the
/// read-once consumers (serialize, log, throw) never notice. A reader that fans one error into
/// many strings holds the result of one read.
/// </para>
/// </remarks>
public readonly record struct ValidationError {

    private readonly string? _message;

    /// <summary>
    /// Creates a finished-string error. The compatibility and adapter shape: the message is
    /// stored as given, and <see cref="MessageInfo"/> stays null.
    /// </summary>
    /// <param name="field">The dotted path to the field - <c>home.postalCode</c>, <c>toys[3].name</c>.</param>
    /// <param name="code">A stable machine-readable code - <c>required</c>, <c>string_length</c>.</param>
    /// <param name="message">The human-readable message, already composed.</param>
    public ValidationError(string field, string code, string message) {
        Field = field;
        Code = code;
        _message = message;
    }

    /// <summary>
    /// Creates a structured error. <see cref="Message"/> renders from
    /// <paramref name="messageInfo"/> when read.
    /// </summary>
    /// <param name="field">The dotted path to the field.</param>
    /// <param name="code">A stable machine-readable code.</param>
    /// <param name="value">The attempted value, or null when capture is off or nothing applies.</param>
    /// <param name="messageInfo">The constraint's template and arguments. Shared, not per-error.</param>
    public ValidationError(string field, string code, object? value, ValidationMessageInfo messageInfo) {
        Field = field;
        Code = code;
        Value = value;
        MessageInfo = messageInfo;
    }

    /// <summary>The dotted path to the field - <c>home.postalCode</c>, <c>toys[3].name</c>.</summary>
    public string Field { get; init; }

    /// <summary>A stable machine-readable code - <c>required</c>, <c>string_length</c>.</summary>
    public string Code { get; init; }

    /// <summary>
    /// The value that failed, or null - when the producing site captured nothing, when
    /// <c>ValidationModules_CaptureValues</c> turned capture off, or when the check has no single
    /// value to name. A reference to data the application already holds; see the class remarks for
    /// what is and is not allowed to render it.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// How badly this failed. Defaults to <see cref="ValidationSeverity.Error"/>, which is also
    /// <c>default</c>, so an uninitialized severity is never silently benign.
    /// </summary>
    public ValidationSeverity Severity { get; init; }

    /// <summary>
    /// The template and arguments this error renders from, or null for a finished-string error.
    /// Null is the visible mark of "this one cannot be re-rendered".
    /// </summary>
    public ValidationMessageInfo? MessageInfo { get; init; }

    /// <summary>
    /// Whether <see cref="Message"/> is the application's own text - a constraint's
    /// <c>Message = …</c>, an <c>Ensure</c>'s explicit <c>message:</c> - rather than text this
    /// library composed.
    /// </summary>
    /// <remarks>
    /// This is what <see cref="LanguagePackFormatter"/> reads to keep an authored message intact:
    /// before it, an override survived or died according to whether the active pack happened to
    /// carry a bare key for that code, which is a rule nobody can see. A finished-string error
    /// that is <i>not</i> authored - a hand-written <c>Report(field, code, message)</c> - still
    /// translates through a bare code-level pack key, which is the documented route for wording a
    /// custom code per culture.
    /// </remarks>
    public bool MessageIsAuthored { get; init; }

    /// <summary>
    /// The human-readable message: the stored text when this is a finished-string error, otherwise
    /// the default render - template holes filled, arguments formatted invariantly,
    /// <see cref="Value"/> never included.
    /// </summary>
    public string Message => _message ?? MessageInfo?.Render(in this) ?? string.Empty;

    /// <summary>
    /// The message as <paramref name="formatter"/> renders it. The read-side override point; the
    /// pass that produced the error never took part in this decision.
    /// </summary>
    /// <param name="formatter">The formatter whose answer is wanted.</param>
    public string ToMessage(ValidationMessageFormatter formatter) {
        ArgumentNullException.ThrowIfNull(formatter);

        return formatter.Format(in this);
    }

    /// <summary>
    /// The positional shape this type had as a positional record, kept so existing
    /// <c>var (field, code, message) = error</c> call sites survive the reshape.
    /// </summary>
    /// <param name="field">Receives <see cref="Field"/>.</param>
    /// <param name="code">Receives <see cref="Code"/>.</param>
    /// <param name="message">Receives <see cref="Message"/>, rendering it if needed.</param>
    public void Deconstruct(out string field, out string code, out string message) {
        field = Field;
        code = Code;
        message = Message;
    }

    /// <summary>
    /// <c>field: code - message</c>. Declared rather than synthesized because the synthesized
    /// record printer would include <see cref="Value"/>, and a struct that leaks what it was
    /// explicitly designed not to leak the moment someone interpolates it is a trap, not a
    /// formatter.
    /// </summary>
    public override string ToString() => string.Concat(Field, ": ", Code, " - ", Message);
}
