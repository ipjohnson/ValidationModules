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
        var context = new ValidationContext(collector);

        validator.Validate(ref context, value);

        return collector.ToResult();
    }

    /// <summary>
    /// Whether the value passes, without materializing a result.
    /// </summary>
    /// <remarks>
    /// Still runs every constraint - there is no first-failure exit - but nothing is allocated
    /// when the value is clean.
    /// </remarks>
    public static bool IsValid<T>(this IValidatorFor<T> validator, T value) {
        ArgumentNullException.ThrowIfNull(validator);

        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        validator.Validate(ref context, value);

        return !collector.HasErrors;
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

        var context = new ValidationContext(collector);

        validator.Validate(ref context, value);
    }
}
