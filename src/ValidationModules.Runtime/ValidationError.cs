namespace ValidationModules;

/// <summary>
/// One validation failure: where it happened, what rule it was, and what to tell a human.
/// </summary>
/// <remarks>
/// The positional shape matches the record Hardened already has, so retargeting it onto this
/// library in Stage 5 is not a wire change. <see cref="Severity"/> is an init-only property rather
/// than a fourth constructor parameter for the same reason, and because adding it later would be a
/// binary break on the primary constructor.
/// </remarks>
/// <param name="Field">The dotted path to the field - <c>home.postalCode</c>, <c>toys[3].name</c>.</param>
/// <param name="Code">A stable machine-readable code - <c>required</c>, <c>string_length</c>.</param>
/// <param name="Message">The human-readable message.</param>
public readonly record struct ValidationError(string Field, string Code, string Message) {

    /// <summary>
    /// How badly this failed. Defaults to <see cref="ValidationSeverity.Error"/>, which is also
    /// <c>default</c>, so an uninitialized severity is never silently benign.
    /// </summary>
    public ValidationSeverity Severity { get; init; }
}
