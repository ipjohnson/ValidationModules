namespace ValidationModules;

/// <summary>
/// Structural validation for <typeparamref name="T"/>: the constraints declared on the type,
/// flattened into straight-line code by the source generator.
/// </summary>
/// <remarks>
/// <para>
/// Generated implementations are stateless, hold no dependencies, and are registered as
/// singletons. A pass that finds nothing allocates nothing.
/// </para>
/// <para>
/// The name is <c>IValidatorFor&lt;T&gt;</c> rather than <c>IValidator&lt;T&gt;</c> because
/// FluentValidation owns the latter, and any codebase using the adapter will have both namespaces
/// imported.
/// </para>
/// </remarks>
/// <typeparam name="T">The type being validated.</typeparam>
public interface IValidatorFor<in T> {

    /// <summary>
    /// Validates <paramref name="value"/>, reporting any failures to <paramref name="context"/>,
    /// and answers whether the pass carries on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Errors are reported in declaration order. Under
    /// <see cref="ValidationStopMode.CollectAll"/> - the default - every constraint is evaluated
    /// and the answer is always <see cref="ValidationFlow.Continue"/>; the one exception is
    /// <c>[Required]</c>, which suppresses the remaining constraints on the same field. Under
    /// <see cref="ValidationStopMode.StopOnFirstError"/> the first blocking failure answers
    /// <see cref="ValidationFlow.Stop"/> and nothing after it is evaluated.
    /// </para>
    /// <para>
    /// A caller composing validators must propagate the answer: returning
    /// <see cref="ValidationFlow.Continue"/> after a nested validator asked to stop would carry on
    /// walking a graph the pass has already finished with.
    /// </para>
    /// </remarks>
    /// <param name="context">Accumulates failures and carries the current field path.</param>
    /// <param name="value">The value to validate.</param>
    ValidationFlow Validate(ref ValidationContext context, T value);

    /// <summary>
    /// Whether <paramref name="value"/> passes, without building the reasons it did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default walks into a throwaway collector and asks whether anything blocking landed,
    /// which is correct for any implementation and costs what <see cref="Validate"/> costs. A
    /// generated validator overrides it with straight-line tests that return at the first failure,
    /// building no path, no message and no error record - so a caller who only wants a boolean is
    /// not charged for the report they threw away.
    /// </para>
    /// <para>
    /// Warnings do not make a value invalid, here or anywhere else in the error model.
    /// </para>
    /// </remarks>
    bool IsValid(T value) {
        var collector = new ValidationErrorCollector();
        var path = System.Buffers.ArrayPool<PathSegment>.Shared.Rent(
            ValidationErrorCollector.DefaultDepthLimit);

        try {
            var context = new ValidationContext(collector, path);

            Validate(ref context, value);

            return !collector.HasBlockingErrors;
        }
        finally {
            System.Buffers.ArrayPool<PathSegment>.Shared.Return(path);
        }
    }
}
