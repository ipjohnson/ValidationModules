using Microsoft.Extensions.Options;

namespace ValidationModules.Options;

/// <summary>
/// The bridge from <see cref="IValidatorFor{T}"/> to <see cref="IValidateOptions{TOptions}"/>:
/// every registered validator runs, results merge, and failures render one line per error as
/// <c>field [code] message</c>.
/// </summary>
/// <typeparam name="TOptions">The options type being validated.</typeparam>
internal sealed class ValidatorForValidateOptions<TOptions> : IValidateOptions<TOptions>
    where TOptions : class {

    private readonly string? _name;
    private readonly IValidatorFor<TOptions>[] _validators;

    public ValidatorForValidateOptions(string? name, IEnumerable<IValidatorFor<TOptions>> validators) {
        ArgumentNullException.ThrowIfNull(validators);

        _name = name;
        _validators = validators as IValidatorFor<TOptions>[]
            ?? System.Linq.Enumerable.ToArray(validators);
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, TOptions options) {
        // Named options flow every instance through every IValidateOptions; only the instance
        // this registration was made for is this bridge's to judge - the shape
        // ValidateDataAnnotations takes too.
        if (_name is not null && !string.Equals(name, _name, StringComparison.Ordinal)) {
            return ValidateOptionsResult.Skip;
        }

        // Failing beats skipping: AddValidatedOptions was called, so validated options were asked
        // for, and answering "valid" with nothing consulted would be a silent no-op. The usual
        // cause is the generated Add…Validators() not being called.
        if (_validators.Length == 0) {
            return ValidateOptionsResult.Fail(
                $"No IValidatorFor<{typeof(TOptions).Name}> is registered. Call the generated " +
                "Add<Assembly>Validators() before the host builds, or register one by hand.");
        }

        var collector = new ValidationErrorCollector();

        for (var i = 0; i < _validators.Length; i++) {
            _validators[i].ValidateInto(collector, options);
        }

        var result = collector.ToResult();

        if (result.IsValid) {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>(result.Errors.Count);

        foreach (var error in result.Errors) {
            if (error.Severity == ValidationSeverity.Error) {
                failures.Add($"{error.Field} [{error.Code}] {error.Message}");
            }
        }

        return ValidateOptionsResult.Fail(failures);
    }
}
