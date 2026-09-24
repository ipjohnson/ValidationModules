namespace ValidationModules;

/// <summary>
/// A point in a validation pass, taken with <see cref="ValidationContext.Mark"/> and passed back
/// to <see cref="ValidationContext.HasBlockingErrorsSince"/>.
/// </summary>
/// <remarks>
/// <para>
/// Opaque, and a struct holding one reference, so taking a mark on every pass costs no allocation.
/// It stays valid for the rest of the pass it was taken in. <c>default</c> is the start of the
/// pass.
/// </para>
/// <para>
/// A mark from one collector means nothing to another. Asking a different pass about it treats
/// every failure that pass recorded as recorded after the mark.
/// </para>
/// </remarks>
public readonly struct ValidationMark
{
    internal ValidationMark(object? token) => Token = token;

    /// <summary>The collector's change token when the mark was taken.</summary>
    internal object? Token { get; }
}
