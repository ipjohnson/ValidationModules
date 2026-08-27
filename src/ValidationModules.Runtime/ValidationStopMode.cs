namespace ValidationModules;

/// <summary>When a validation pass stops.</summary>
public enum ValidationStopMode {

    /// <summary>
    /// Evaluate every rule and report everything that failed. The default, and what a caller
    /// rendering a form or a 400 body wants: one round trip listing all the problems.
    /// </summary>
    CollectAll = 0,

    /// <summary>
    /// Return at the first Error-severity failure, leaving the remaining rules unevaluated. Not
    /// "collect everything and show the first" - the work is genuinely skipped, including descent
    /// into nested objects and collection elements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Warnings do not stop a pass. A warning is advisory and does not make a value invalid, so
    /// stopping on one would hide the error that follows it.
    /// </para>
    /// <para>
    /// A rule declared through <c>rules.Apply</c> or an <see cref="IAsyncValidatorFor{T}"/> is
    /// hand-written code that may discard the <see cref="ValidationFlow"/> it is handed; such a
    /// rule keeps going. That is the same carve-out those two already have for
    /// <see cref="IValidatorFor{T}.IsValid"/>.
    /// </para>
    /// </remarks>
    StopOnFirstError = 1
}
