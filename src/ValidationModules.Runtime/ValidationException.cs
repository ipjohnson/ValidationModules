namespace ValidationModules;

/// <summary>
/// Thrown by <see cref="ValidatorForExtensions.ValidateAndThrow{T}"/>, and by nothing else in this
/// library.
/// </summary>
/// <remarks>
/// Validating never throws on failure - it accumulates and returns. This type exists for callers
/// who want the failure to unwind, and for frameworks that map it onto a response. Both routes end
/// at the same <see cref="ValidationResult"/>, so a framework needs one mapper rather than two
/// shapes that agree by duplication.
/// </remarks>
public sealed class ValidationException : Exception {

    /// <summary>
    /// Creates an exception carrying the failures that caused it.
    /// </summary>
    /// <param name="result">The failed result. Must not be valid.</param>
    public ValidationException(ValidationResult result)
        : base(BuildMessage(result)) {
        ArgumentNullException.ThrowIfNull(result);

        Result = result;
    }

    /// <summary>
    /// The failures. Never null, and never empty in practice.
    /// </summary>
    public ValidationResult Result { get; }

    private static string BuildMessage(ValidationResult result) {
        if (result is null || result.Errors.Count == 0) {
            return "Validation failed.";
        }

        var first = result.Errors[0];
        var remaining = result.Errors.Count - 1;

        return remaining == 0
            ? $"Validation failed: {first.Field} {first.Code}."
            : $"Validation failed: {first.Field} {first.Code}, and {remaining} more.";
    }
}
