using Microsoft.Extensions.DependencyInjection;

namespace ValidationModules;

/// <summary>
/// Runs validators registered for a nested type alongside the generated one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hole this closes.</b> A generated validator descends into a nested object by calling the
/// nested type's generated validator through its static <c>Instance</c> field. That is what keeps
/// generated validators parameterless, and it means a hand-written
/// <c>IValidatorFor&lt;Address&gt;</c> registered in the container was invisible from the nested
/// path: validating an <c>Address</c> directly ran it, and validating the same <c>Address</c> as a
/// property of a <c>Pet</c> did not. Composition worked at the top level and silently did not one
/// level down, which is the worst shape for an inconsistency to take.
/// </para>
/// <para>
/// <b>Why this is AOT-safe.</b> The lookup is <c>GetServices&lt;IValidatorFor&lt;Address&gt;&gt;()</c>
/// - a <i>closed</i> generic, written by the generator, which knows the nested type at build time.
/// The reflective spelling would be
/// <c>MakeGenericType(typeof(IValidatorFor&lt;&gt;), addressType)</c>, and that is the thing this
/// library exists to avoid. Nothing here reflects.
/// </para>
/// <para>
/// <b>What it costs.</b> Nothing at all when no container took part: <see cref="ValidationContext.Services"/>
/// is null, this returns immediately, and the clean pass keeps its zero-allocation promise. When a
/// provider <i>is</i> present the resolve allocates an enumerable per nested node. That is the price
/// of composition and it is opt-in - you pay it by having started the pass from a scope.
/// </para>
/// </remarks>
public static class NestedValidation {

    /// <summary>
    /// Runs every registered <see cref="IValidatorFor{T}"/> for <paramref name="value"/> except the
    /// generated one, which the caller has already run.
    /// </summary>
    /// <remarks>
    /// <paramref name="generated"/> is excluded by reference rather than by type. The generated
    /// validator is registered in the container as well as being reachable statically, so without
    /// the exclusion every nested error would appear twice - and comparing by reference is exact,
    /// because registration hands the container that very singleton.
    /// </remarks>
    /// <typeparam name="T">The nested type. Always closed by the generator.</typeparam>
    /// <param name="context">The context already pushed to the nested path.</param>
    /// <param name="value">The nested value.</param>
    /// <param name="generated">The generated validator the caller ran, or null if there was none.</param>
    public static void ValidateRegistered<T>(
        this ValidationContext context, T value, IValidatorFor<T>? generated = null) {

        if (context.Services is not { } services) {
            return;
        }

        foreach (var validator in services.GetServices<IValidatorFor<T>>()) {
            if (ReferenceEquals(validator, generated)) {
                continue;
            }

            var nested = context;

            validator.Validate(ref nested, value);
        }
    }
}
