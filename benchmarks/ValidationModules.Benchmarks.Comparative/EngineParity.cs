using FluentValidation;
using ValidationModules.Benchmarks.Comparative.Engines;
using ValidationModules.Benchmarks.Comparative.Models;
using DataAnnotations = System.ComponentModel.DataAnnotations;

namespace ValidationModules.Benchmarks.Comparative;

/// <summary>
/// Checks, before anything is measured, that the engines are being asked the same question.
/// </summary>
/// <remarks>
/// <para>
/// A comparative benchmark's failure mode is not a wrong number, it is a number that is right about
/// the wrong thing. The rules here are declared three times - constraint attributes, a
/// <c>RuleFor</c> chain, DataAnnotations attributes - and nothing in the compiler relates them. One
/// edit to a bound in one of the three and the suite goes on producing tidy tables comparing two
/// different specifications.
/// </para>
/// <para>
/// So the run refuses to start unless the engines agree on how many failures each payload has. It
/// is a weaker check than comparing the failures themselves, and deliberately: the engines produce
/// different field-path casing and different message text by design, and the conformance suite in
/// §8 of the plan is where that belongs. Counting is enough to catch a rule that was changed in one
/// spelling and not the others, which is the mistake that actually happens.
/// </para>
/// <para>
/// DataAnnotations is checked on the flat payload only. It does not descend into nested objects, so
/// on a nested payload it is expected to disagree, and the benchmarks that use it there say so in
/// their names.
/// </para>
/// <para>
/// <b>Called from <c>[GlobalSetup]</c>, not only from <c>Main</c>, and that distinction is the
/// whole point.</b> Under Native AOT, BenchmarkDotNet compiles and runs each class in a separate
/// process; <c>Main</c>'s check runs in the JIT host and says nothing about what the measured
/// process does. It said nothing about this: <c>Validator.TryValidateObject</c> under Native AOT
/// returns <i>valid</i> with zero failures for an object violating five constraints, because the
/// trimmer removed the metadata it reflects over. The suite duly reported DataAnnotations as
/// <b>4.6x faster</b> on the failing payload than under the JIT - a tidy number for an engine that
/// had stopped validating. Exactly the failure this class was written to prevent, running in the
/// one place it could not see.
/// </para>
/// </remarks>
public static class EngineParity {

    // Hoisted: constructing per invocation would put an allocation on the measured path.
    private static readonly BasketValidator BasketValidatorShared = new();
    private static readonly CustomerValidator CustomerValidatorShared = new();
    private static readonly OrderValidator OrderValidatorShared = new();

    /// <summary>
    /// Throws if the engines disagree about any sample payload.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rule sets have drifted apart.</exception>
    public static void Verify() {
        var mismatches = new List<string>();

        Compare(mismatches, "flat, clean",
            VmCount(CustomerValidatorShared, SampleData.ValidCustomer()),
            FvCount(CustomerFluentValidator.Instance, SampleData.ValidCustomer()),
            null);

        Compare(mismatches, "flat, every rule violated",
            VmCount(CustomerValidatorShared, SampleData.InvalidCustomer()),
            FvCount(CustomerFluentValidator.Instance, SampleData.InvalidCustomer()),
            null);

        Compare(mismatches, "nested, clean",
            VmCount(OrderValidatorShared, SampleData.ValidOrder()),
            FvCount(OrderFluentValidator.Instance, SampleData.ValidOrder()),
            null);

        Compare(mismatches, "nested, one failure per level",
            VmCount(OrderValidatorShared, SampleData.InvalidOrder()),
            FvCount(OrderFluentValidator.Instance, SampleData.InvalidOrder()),
            null);

        Compare(mismatches, "basket of 100",
            VmCount(BasketValidatorShared, SampleData.BasketOf(100)),
            FvCount(BasketFluentValidator.Instance, SampleData.BasketOf(100)),
            null);

        if (mismatches.Count > 0) {
            throw new InvalidOperationException(
                "The engines no longer agree on what the rules are, so any comparison between them " +
                "would be meaningless. Reconcile the constraint attributes in Models/Models.cs, the " +
                "RuleFor chains in Engines/FluentEngine.cs and the attributes in " +
                "Models/AnnotatedModels.cs before running again." +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, mismatches));
        }
    }

    /// <summary>
    /// Why DataAnnotations cannot be compared on this runtime, or null when it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Verify"/>, and returned rather than thrown, because the two
    /// describe different faults. A disagreement between ValidationModules and FluentValidation is
    /// a rule that was edited in one spelling and not the others - a bug in this suite, and every
    /// number it produces is meaningless, so <see cref="Verify"/> throws.
    /// </para>
    /// <para>
    /// An engine that has stopped working is not that. Under Native AOT
    /// <c>Validator.TryValidateObject</c> returns <i>valid</i> with zero failures for an object
    /// violating five constraints, because the trimmer removed the metadata it reflects over - and
    /// the suite duly reported DataAnnotations as 4.6x <i>faster</i> than under the JIT on the
    /// failing payload. Aborting the whole class over it would throw away the
    /// ValidationModules-versus-FluentValidation numbers, which are measuring something real. So
    /// the DataAnnotations benchmarks fail on their own and the rest of the table survives.
    /// </para>
    /// </remarks>
    public static string? DataAnnotationsDivergence() {
        var clean = DaCount(SampleData.ValidAnnotatedCustomer());
        var failing = DaCount(SampleData.InvalidAnnotatedCustomer());
        var expected = VmCount(CustomerValidatorShared, SampleData.InvalidCustomer());

        if (clean != 0) {
            return $"DataAnnotations found {clean} failures on a payload that satisfies every rule.";
        }

        if (failing != expected) {
            return
                $"DataAnnotations found {failing} failures where the other engines find {expected}. " +
                "Under Native AOT this is normally zero: Validator.TryValidateObject reflects over " +
                "property metadata the trimmer has removed, reports the object valid, and is fast " +
                "because it is no longer validating. Publishing that as a timing would be a lie.";
        }

        return null;
    }

    private static void Compare(List<string> mismatches, string payload, int vm, int fv, int? da) {
        if (vm != fv || (da is { } annotations && annotations != vm)) {
            mismatches.Add(
                $"  {payload}: ValidationModules found {vm}, FluentValidation found {fv}" +
                (da is { } count ? $", DataAnnotations found {count}" : string.Empty));
        }
    }

    private static int VmCount<T>(IValidatorFor<T> validator, T value) => validator.Validate(value).Errors.Count;

    private static int FvCount<T>(IValidator<T> validator, T value) => validator.Validate(value).Errors.Count;

    private static int DaCount(object value) {
        var results = new List<DataAnnotations.ValidationResult>();

        DataAnnotationsEngine.TryValidate(value, results);

        return results.Count;
    }
}
