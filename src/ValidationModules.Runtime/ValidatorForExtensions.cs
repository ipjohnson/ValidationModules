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
    /// <param name="profile">The profile to validate under, or <see langword="null"/> for the default.</param>
    public static ValidationResult Validate<T>(this IValidatorFor<T> validator, T value, Type? profile = null) {
        ArgumentNullException.ThrowIfNull(validator);

        var collector = new ValidationErrorCollector(profile);
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
    public static bool IsValid<T>(this IValidatorFor<T> validator, T value, Type? profile = null) {
        ArgumentNullException.ThrowIfNull(validator);

        var collector = new ValidationErrorCollector(profile);
        var context = new ValidationContext(collector);

        validator.Validate(ref context, value);

        return !collector.HasErrors;
    }

    /// <summary>
    /// Runs the validator and throws <see cref="ValidationException"/> if the value is invalid.
    /// </summary>
    /// <exception cref="ValidationException">The value failed validation.</exception>
    public static void ValidateAndThrow<T>(this IValidatorFor<T> validator, T value, Type? profile = null) {
        var result = validator.Validate(value, profile);

        if (!result.IsValid) {
            throw new ValidationException(result);
        }
    }

    /// <summary>
    /// Runs the validator into a collector the caller owns, so the caller can pool it. This is the
    /// overload a per-request pipeline should use.
    /// </summary>
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
