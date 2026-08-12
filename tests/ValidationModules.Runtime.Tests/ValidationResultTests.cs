using Xunit;

namespace ValidationModules.Runtime.Tests;

public class ValidationResultTests {

    [Fact]
    public void Valid_HasNoErrorsAndIsValid() {
        Assert.True(ValidationResult.Valid.IsValid);
        Assert.False(ValidationResult.Valid.HasErrors);
        Assert.Empty(ValidationResult.Valid.Errors);
    }

    [Fact]
    public void Valid_ExposesNoMutator() {
        // Hardened's equivalent exposes AddError on a process-wide static, so the natural way to
        // write a custom validator poisons every other caller's success result. Nothing on this
        // type can mutate it, which is what makes sharing the instance safe.
        var mutators = typeof(ValidationResult)
            .GetMethods()
            .Where(method => method.Name is "AddError" or "Add" or "Clear")
            .ToArray();

        Assert.Empty(mutators);
    }

    [Fact]
    public void IsValid_OnlyErrorSeverityInvalidates() {
        var warnings = ValidationResult.FromErrors([
            new ValidationError("name", "deprecated", "x") { Severity = ValidationSeverity.Warning },
            new ValidationError("tag", "note", "x") { Severity = ValidationSeverity.Info },
        ]);

        Assert.True(warnings.IsValid);
        Assert.True(warnings.HasErrors);
    }

    [Fact]
    public void Severity_DefaultsToError() {
        var error = new ValidationError("name", "required", "x");

        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.False(ValidationResult.FromErrors([error]).IsValid);
    }

    [Fact]
    public void FromErrors_Empty_ReturnsTheSharedValidInstance() {
        Assert.Same(ValidationResult.Valid, ValidationResult.FromErrors([]));
    }

    [Fact]
    public void Merge_PreservesOrderWithThisResultFirst() {
        var first = ValidationResult.FromErrors([new ValidationError("a", "required", "x")]);
        var second = ValidationResult.FromErrors([new ValidationError("b", "required", "x")]);

        Assert.Equal(["a", "b"], first.Merge(second).Errors.Select(error => error.Field));
    }

    [Fact]
    public void Merge_DoesNotMutateEitherOperand() {
        var first = ValidationResult.FromErrors([new ValidationError("a", "required", "x")]);
        var second = ValidationResult.FromErrors([new ValidationError("b", "required", "x")]);

        first.Merge(second);

        Assert.Single(first.Errors);
        Assert.Single(second.Errors);
    }

    [Fact]
    public void Merge_WithValid_ReturnsTheOtherOperandUnchanged() {
        var errors = ValidationResult.FromErrors([new ValidationError("a", "required", "x")]);

        Assert.Same(errors, errors.Merge(ValidationResult.Valid));
        Assert.Same(errors, ValidationResult.Valid.Merge(errors));
    }

    [Fact]
    public void ValidationException_CarriesTheResult() {
        var result = ValidationResult.FromErrors([new ValidationError("name", "required", "x")]);

        var exception = new ValidationException(result);

        Assert.Same(result, exception.Result);
        Assert.Contains("name", exception.Message);
    }
}
