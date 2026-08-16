using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ValidationModules.AspNetCore;

/// <summary>
/// Turns a <see cref="ValidationResult"/> into the HTTP shape RFC 9457 describes.
/// </summary>
/// <remarks>
/// Public and static because every project that validates over HTTP writes this mapping, and
/// writing it by hand is how two endpoints in one service end up disagreeing about the response
/// shape. <see cref="ValidationEndpointFilter{T}"/> uses it; so can a controller, a middleware, or
/// anything else holding a result.
/// </remarks>
public static class ValidationProblem {

    /// <summary>
    /// Groups <paramref name="result"/>'s failures by field, in the shape ASP.NET Core's own
    /// model-binding failures use.
    /// </summary>
    /// <remarks>
    /// Order within a field is preserved, because <see cref="ValidationResult.Errors"/> is in
    /// declaration order and a caller reading "name is required" before "name must be at most 10
    /// characters" is reading them in the order the rules were written.
    /// </remarks>
    public static Dictionary<string, string[]> ToDictionary(
        ValidationResult result, ValidationProblemOptions? options = null) {
        ArgumentNullException.ThrowIfNull(result);

        options ??= new ValidationProblemOptions();

        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        for (var i = 0; i < result.Errors.Count; i++) {
            var error = result.Errors[i];

            if (!options.IncludeNonErrors && error.Severity != ValidationSeverity.Error) {
                continue;
            }

            if (!grouped.TryGetValue(error.Field, out var messages)) {
                grouped[error.Field] = messages = new List<string>(1);
            }

            messages.Add(error.Message);
        }

        var byField = new Dictionary<string, string[]>(grouped.Count, StringComparer.Ordinal);

        foreach (var pair in grouped) {
            byField[pair.Key] = pair.Value.ToArray();
        }

        return byField;
    }

    /// <summary>
    /// The same grouping over each failure's machine-readable code rather than its message.
    /// </summary>
    public static Dictionary<string, string[]> ToCodeDictionary(
        ValidationResult result, ValidationProblemOptions? options = null) {
        ArgumentNullException.ThrowIfNull(result);

        options ??= new ValidationProblemOptions();

        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        for (var i = 0; i < result.Errors.Count; i++) {
            var error = result.Errors[i];

            if (!options.IncludeNonErrors && error.Severity != ValidationSeverity.Error) {
                continue;
            }

            if (!grouped.TryGetValue(error.Field, out var codes)) {
                grouped[error.Field] = codes = new List<string>(1);
            }

            codes.Add(error.Code);
        }

        var byField = new Dictionary<string, string[]>(grouped.Count, StringComparer.Ordinal);

        foreach (var pair in grouped) {
            byField[pair.Key] = pair.Value.ToArray();
        }

        return byField;
    }

    /// <summary>
    /// Builds the <see cref="ValidationProblemDetails"/> body for a failed result.
    /// </summary>
    public static ValidationProblemDetails ToProblemDetails(
        ValidationResult result, ValidationProblemOptions? options = null) {
        ArgumentNullException.ThrowIfNull(result);

        options ??= new ValidationProblemOptions();

        var problem = new ValidationProblemDetails(ToDictionary(result, options)) {
            Title = options.Title,
            Type = options.Type,
            Status = options.StatusCode,
        };

        if (options.IncludeCodes) {
            problem.Extensions["validationCodes"] = ToCodeDictionary(result, options);
        }

        return problem;
    }

    /// <summary>
    /// The result to return from a handler or filter for a failed validation.
    /// </summary>
    /// <remarks>
    /// Neither <c>Results.Problem</c> nor <c>Results.ValidationProblem</c>: the first cannot be
    /// serialised in a Native AOT app whose JSON context knows only the consumer's own types, and
    /// the second drops the codes extension. See <see cref="ValidationProblemResult"/>.
    /// </remarks>
    public static IResult ToResult(ValidationResult result, ValidationProblemOptions? options = null) {
        options ??= new ValidationProblemOptions();

        return new ValidationProblemResult(ToProblemDetails(result, options), options.StatusCode);
    }
}
