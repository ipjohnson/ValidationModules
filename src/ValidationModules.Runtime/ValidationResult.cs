using System.Collections.ObjectModel;

namespace ValidationModules;

/// <summary>
/// The immutable outcome of a validation pass.
/// </summary>
/// <remarks>
/// <para>
/// Immutability is the point. Hardened's equivalent exposes a mutable <c>Success</c> singleton and
/// an <c>AddError</c> method, so the natural way to write a custom validator poisons a
/// process-wide instance that every other caller shares. Here there is no mutator, which is what
/// makes the shared <see cref="Valid"/> instance safe.
/// </para>
/// </remarks>
public sealed class ValidationResult {
    private static readonly IReadOnlyList<ValidationError> NoErrors = new ReadOnlyCollection<ValidationError>([]);

    private ValidationResult(IReadOnlyList<ValidationError> errors) {
        Errors = errors;
    }

    /// <summary>
    /// The shared result for a pass that found nothing. Safe to share because nothing can mutate it.
    /// </summary>
    public static ValidationResult Valid { get; } = new(NoErrors);

    /// <summary>
    /// Every failure recorded, in declaration order.
    /// </summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>
    /// Whether the value is acceptable: true when no failure carries
    /// <see cref="ValidationSeverity.Error"/>. Warnings and information do not invalidate.
    /// </summary>
    public bool IsValid {
        get {
            for (var i = 0; i < Errors.Count; i++) {
                if (Errors[i].Severity == ValidationSeverity.Error) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Whether anything at all was recorded, at any severity. A result can have errors and still
    /// be <see cref="IsValid"/> if all of them are warnings.
    /// </summary>
    public bool HasErrors => Errors.Count > 0;

    /// <summary>
    /// Creates a result from a sequence of failures. Returns <see cref="Valid"/> when empty.
    /// </summary>
    public static ValidationResult FromErrors(IEnumerable<ValidationError> errors) {
        ArgumentNullException.ThrowIfNull(errors);

        // Copied rather than wrapped: the caller may be a pooled collector that is about to Reset.
        var copy = new List<ValidationError>(errors);

        return copy.Count == 0 ? Valid : new ValidationResult(new ReadOnlyCollection<ValidationError>(copy));
    }

    /// <summary>
    /// Creates a result that takes ownership of <paramref name="errors"/> rather than copying it.
    /// </summary>
    /// <remarks>
    /// The array must not be touched afterwards - it becomes the result's storage. Internal because
    /// that contract is only safe to offer callers in this assembly;
    /// <see cref="ValidationErrorCollector.ToResult"/> builds an exactly-sized array it never
    /// retains, and the public <see cref="FromErrors"/> overload keeps copying because it cannot
    /// know what its caller will do next. An array implements
    /// <see cref="IReadOnlyList{T}"/> directly, so this is one allocation where the copying path is
    /// three: a list, its backing array, and a wrapper.
    /// </remarks>
    internal static ValidationResult FromOwnedArray(ValidationError[] errors) =>
        errors.Length == 0 ? Valid : new ValidationResult(errors);

    /// <summary>
    /// Combines this result with another, preserving order - this result's failures first.
    /// </summary>
    /// <remarks>
    /// Returns a new instance; neither operand is modified. Merging rather than replacing by
    /// precedence is deliberate: structural constraints must not silently disappear because
    /// someone registered a business rule.
    /// </remarks>
    public ValidationResult Merge(ValidationResult other) {
        ArgumentNullException.ThrowIfNull(other);

        if (!other.HasErrors) {
            return this;
        }

        if (!HasErrors) {
            return other;
        }

        var merged = new List<ValidationError>(Errors.Count + other.Errors.Count);
        merged.AddRange(Errors);
        merged.AddRange(other.Errors);

        return new ValidationResult(new ReadOnlyCollection<ValidationError>(merged));
    }
}
