using System.Buffers;

namespace ValidationModules;

/// <summary>
/// The ergonomic surface over <see cref="IValidatorFor{T}"/>.
/// </summary>
/// <remarks>
/// These are extensions rather than interface members so that hand-writing an
/// <see cref="IValidatorFor{T}"/> means implementing one method rather than five.
/// </remarks>
public static class ValidatorForExtensions {

    /// <summary>
    /// Runs the validator and returns an immutable result.
    /// </summary>
    /// <param name="validator">The validator to run.</param>
    /// <param name="value">The value to validate.</param>
    public static ValidationResult Validate<T>(this IValidatorFor<T> validator, T value) {
        ArgumentNullException.ThrowIfNull(validator);

        var collector = new ValidationErrorCollector();
        var path = ArrayPool<PathSegment>.Shared.Rent(ValidationErrorCollector.DefaultDepthLimit);

        try {
            var context = new ValidationContext(collector, path);

            validator.Validate(ref context, value);

            return collector.ToResult();
        }
        finally {
            // Not cleared: every slot is written before it is read and nothing at or above the
            // current depth is ever read, so stale contents cannot be observed.
            ArrayPool<PathSegment>.Shared.Return(path);
        }
    }


    /// <summary>
    /// Whether the value passes, without building the reasons it did not.
    /// </summary>
    /// <remarks>
    /// Forwards to <see cref="IValidatorFor{T}.IsValid"/>, which a generated validator overrides
    /// with straight-line tests that return at the first failure. This exists because a default
    /// interface member is only reachable through the interface: calling it on a hand-written
    /// validator's concrete type would otherwise not compile. Where the concrete type declares its
    /// own - every generated one does - that instance method wins and this is never reached.
    /// </remarks>
    public static bool IsValid<T>(this IValidatorFor<T> validator, T value) {
        ArgumentNullException.ThrowIfNull(validator);

        return validator.IsValid(value);
    }

    /// <summary>
    /// Runs the validator and throws <see cref="ValidationException"/> if the value is invalid.
    /// </summary>
    /// <exception cref="ValidationException">The value failed validation.</exception>
    public static void ValidateAndThrow<T>(this IValidatorFor<T> validator, T value) {
        var result = validator.Validate(value);

        if (!result.IsValid) {
            throw new ValidationException(result);
        }
    }

    /// <summary>
    /// Runs the validator into a collector the caller owns, for gathering several validations into
    /// one result.
    /// </summary>
    /// <remarks>
    /// This used to be the overload a per-request pipeline should reach for, because owning the
    /// collector let it be pooled and a fresh one cost 472 bytes. A fresh one now costs 48, and
    /// reusing one makes every failing pass allocate a node it would otherwise have recycled, so
    /// <c>Validate</c> is the better default and this is for callers who genuinely want several
    /// passes in one result.
    /// </remarks>
    /// <param name="validator">The validator to run.</param>
    /// <param name="collector">Receives the errors. Not reset first - reset it yourself between passes.</param>
    /// <param name="value">The value to validate.</param>
    public static void ValidateInto<T>(
        this IValidatorFor<T> validator,
        ValidationErrorCollector collector,
        T value) {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(collector);

        var path = ArrayPool<PathSegment>.Shared.Rent(ValidationErrorCollector.DefaultDepthLimit);

        try {
            var context = new ValidationContext(collector, path);

            validator.Validate(ref context, value);
        }
        finally {
            ArrayPool<PathSegment>.Shared.Return(path);
        }
    }
}
