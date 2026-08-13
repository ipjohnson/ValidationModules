using DataAnnotations = System.ComponentModel.DataAnnotations;

namespace ValidationModules.Benchmarks.Comparative.Engines;

/// <summary>
/// The in-box engine, wrapped so the benchmarks call it the way an application would.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not recurse.</b> <c>Validator.TryValidateObject</c> walks the top-level properties of
/// the object it is given and stops there, even with <c>validateAllProperties</c> set - a nested
/// object's own <c>[Required]</c> is never evaluated, and neither is a collection element's. So a
/// nested reading for this engine is doing strictly less work than the other two and is labelled as
/// such wherever it appears.
/// </para>
/// <para>
/// It is included anyway because it is what a project gets for free, and because the reflection it
/// performs per call - property descriptors, attribute lookups, boxed values - is the cost the
/// generated approach exists to remove. That cost is real whether or not the engine descends.
/// </para>
/// </remarks>
public static class DataAnnotationsEngine {

    /// <summary>
    /// Validates one object, reusing the caller's results list so the comparison is not dominated by
    /// allocating a fresh one per call.
    /// </summary>
    /// <param name="instance">The object to validate.</param>
    /// <param name="results">Receives the failures. Cleared first.</param>
    public static bool TryValidate(object instance, List<DataAnnotations.ValidationResult> results) {
        results.Clear();

        var context = new DataAnnotations.ValidationContext(instance);

        return DataAnnotations.Validator.TryValidateObject(instance, context, results, validateAllProperties: true);
    }
}
