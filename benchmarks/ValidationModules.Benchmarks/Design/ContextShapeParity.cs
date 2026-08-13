namespace ValidationModules.Benchmarks.Design;

/// <summary>
/// Checks that the prototype produces byte-identical error paths to the shipped shape, before
/// anything compares their speed.
/// </summary>
/// <remarks>
/// Two path builders written twice will drift, and a shape that is fast because it silently emits
/// <c>lines.3.sku</c> instead of <c>lines[3].sku</c> is not a candidate for anything. The shapes
/// are only comparable while they agree, so this runs from every benchmark class's setup and
/// throws rather than letting a wrong number look like a win.
/// </remarks>
public static class ContextShapeParity {

    /// <summary>
    /// Throws if the two shapes disagree about any path.
    /// </summary>
    /// <exception cref="InvalidOperationException">The prototype has drifted from the shipped shape.</exception>
    public static void Verify() {
        var mismatches = new List<string>();

        Compare(mismatches, "root field",
            Log(context => context.Add("name", "required", "m")),
            Chain(context => context.Add("name", "required", "m")));

        Compare(mismatches, "one nested object",
            Log(context => context.Push("home").Add("postalCode", "required", "m")),
            Chain(context => context.Push("home").Add("postalCode", "required", "m")));

        Compare(mismatches, "collection element",
            Log(context => context.PushIndex("toys", 3).Add("name", "required", "m")),
            Chain(context => context.PushIndex("toys", 3).Add("name", "required", "m")));

        Compare(mismatches, "dictionary entry",
            Log(context => context.PushKey("items", "sku-1").Add("name", "required", "m")),
            Chain(context => context.PushKey("items", "sku-1").Add("name", "required", "m")));

        Compare(mismatches, "four levels, mixed",
            Log(context => context.Push("order").PushIndex("lines", 2).Push("shipTo").PushKey("tags", "a")
                .Add("city", "required", "m")),
            Chain(context => context.Push("order").PushIndex("lines", 2).Push("shipTo").PushKey("tags", "a")
                .Add("city", "required", "m")));

        Compare(mismatches, "type-level failure at depth",
            Log(context => context.Push("home").AddHere("conflict", "m")),
            Chain(context => context.Push("home").AddHere("conflict", "m")));

        Compare(mismatches, "type-level failure at the root",
            Log(context => context.AddHere("conflict", "m")),
            Chain(context => context.AddHere("conflict", "m")));

        // Suppression is a property of the error model rather than of either shape, so it has to
        // survive the change. Two adds on one field, the first a failed Required: one error out.
        Compare(mismatches, "Required suppresses the rest of the field",
            LogAll(context => {
                context.Add("name", ValidationCodes.Required, "m");
                context.Add("name", ValidationCodes.StringLength, "m");
            }),
            ChainAll(context => {
                context.Add("name", ValidationCodes.Required, "m");
                context.Add("name", ValidationCodes.StringLength, "m");
            }));

        if (mismatches.Count > 0) {
            throw new InvalidOperationException(
                "The chain prototype no longer agrees with the shipped context, so comparing their " +
                "speed would be meaningless. Reconcile ContextShapePrototype.cs with " +
                "ValidationContext/ValidationErrorCollector before running again." +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, mismatches));
        }
    }

    private static void Compare(List<string> mismatches, string scenario, string log, string chain) {
        if (!string.Equals(log, chain, StringComparison.Ordinal)) {
            mismatches.Add($"  {scenario}: shipped produced '{log}', prototype produced '{chain}'");
        }
    }

    private static string Log(Action<ValidationContext> body) => LogAll(body);

    private static string LogAll(Action<ValidationContext> body) {
        var collector = new ValidationErrorCollector();

        body(new ValidationContext(collector));

        return Describe(collector.ToResult());
    }

    private static string Chain(Action<ChainContext> body) => ChainAll(body);

    private static string ChainAll(Action<ChainContext> body) {
        var collector = new ChainErrorCollector();

        body(new ChainContext(collector));

        return Describe(collector.ToResult());
    }

    /// <summary>
    /// Field and code for every error in order, which is what the two shapes have to agree on.
    /// Messages are the caller's literal here and carry no information.
    /// </summary>
    private static string Describe(ValidationResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Field}|{error.Code}"));
}
