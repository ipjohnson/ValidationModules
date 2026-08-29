using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

// The library's own ValidationResult wins name lookup inside this namespace, so the
// DataAnnotations one - the only result type this file deals in - is aliased explicitly.
using DataAnnotationsResult = System.ComponentModel.DataAnnotations.ValidationResult;
using ValidationModules.Naming;

namespace ValidationModules;

/// <summary>
/// The bridge generated validators call to run the DataAnnotations surfaces that carry user code:
/// custom <see cref="ValidationAttribute"/> subclasses, <c>[CustomValidation]</c> methods, and
/// <see cref="IValidatableObject"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invoked, not reproduced.</b> The built-in DataAnnotations attributes compile to straight-line
/// checks because their semantics are closed and known; an arbitrary subclass's <c>IsValid</c> is
/// user code, and the only faithful thing to do with user code is run it. Nothing here reflects:
/// the attribute instance is constructed by generated code from its compile-time-constant
/// arguments, held in a static field, and called through its ordinary virtual surface - the same
/// calls <c>Validator.TryValidateObject</c> would make, minus the discovery.
/// </para>
/// <para>
/// <b>What it costs, and where.</b> Every check goes through
/// <see cref="ValidationAttribute.GetValidationResult"/> with a real context, allocating one per
/// call - a passing value included - plus the box the object-typed API forces on a value-type
/// member. A skip-the-context fast path was written and removed: an attribute that overrides only
/// the protected, context-taking <c>IsValid</c> without also overriding
/// <see cref="ValidationAttribute.RequiresValidationContext"/> works under
/// <c>Validator.TryValidateObject</c> - which always supplies a context - and would have received
/// null from the fast path, inside user code. So this is the DataAnnotations cost model, faithfully,
/// applied to the one property that asked for it; the rest of the pass keeps the zero-allocation
/// promise.
/// </para>
/// <para>
/// <b>Errors carry the <see cref="ValidationCodes.Custom"/> code</b>, one code for the whole
/// family for the reason <c>Ensure</c> predicates share <see cref="ValidationCodes.Predicate"/>:
/// the message is the attribute's own, human-facing and free to change, while the code is a wire
/// contract that must not vary with it.
/// </para>
/// </remarks>
public static class DataAnnotationsSupport {

    /// <summary>
    /// Runs one attribute against one member value, reporting its failure with the attribute's own
    /// formatted message.
    /// </summary>
    /// <param name="context">The pass to report into.</param>
    /// <param name="attribute">The attribute instance, constructed once by generated code.</param>
    /// <param name="instance">The object being validated - what the attribute's context exposes.</param>
    /// <param name="value">The member's value.</param>
    /// <param name="field">The wire field name errors report under.</param>
    /// <param name="memberName">The CLR member name, for the attribute's context.</param>
    /// <param name="displayName">
    /// What the attribute's message templates see as <c>{0}</c>: the member's
    /// <c>[Display(Name = …)]</c> when present, otherwise the CLR name - the same resolution
    /// DataAnnotations performs reflectively, done at build time instead.
    /// </param>
    public static ValidationFlow Validate(
        ref ValidationContext context,
        ValidationAttribute attribute,
        object instance,
        object? value,
        string field,
        string memberName,
        string displayName) {

        var result = attribute.GetValidationResult(
            value, CreateContext(context.Services, instance, memberName, displayName));

        return result is null
            ? ValidationFlow.Continue
            : context.Report(field, ValidationCodes.Custom, Message(result, field));
    }

    /// <summary>
    /// The boolean-path form of <see cref="Validate"/>: the verdict, no report.
    /// </summary>
    /// <remarks>
    /// A boolean pass carries no collector and so no services; an attribute that reads its
    /// context's <c>GetService</c> gets null, which is what <c>Validator.TryValidateObject</c>
    /// hands attributes when no provider was supplied.
    /// </remarks>
    public static bool IsValid(
        ValidationAttribute attribute, object instance, object? value, string memberName, string displayName) =>
        attribute.GetValidationResult(
            value, CreateContext(null, instance, memberName, displayName)) is null;

    /// <summary>
    /// Maps one <see cref="ValidationResult"/> - from a <c>[CustomValidation]</c> method or an
    /// <see cref="IValidatableObject"/> - into the collector.
    /// </summary>
    /// <param name="context">The pass to report into.</param>
    /// <param name="result">The result; null is DataAnnotations' spelling of success.</param>
    /// <param name="field">
    /// Where the error lands when the result names no members: the declaring property's field, or
    /// null for a type-level result, which reports against the object itself.
    /// </param>
    /// <param name="namer">
    /// Converts the CLR names in
    /// <see cref="System.ComponentModel.DataAnnotations.ValidationResult.MemberNames"/> to wire
    /// field names.
    /// Generated code passes the same policy its own literals were baked with, so a member named
    /// at run time lands on the same path a compiled constraint would have used.
    /// </param>
    public static ValidationFlow Apply(
        ref ValidationContext context, DataAnnotationsResult? result, string? field, IValidationFieldNamer namer) {

        if (result is null) {
            return ValidationFlow.Continue;
        }

        var message = Message(result, field);
        var named = false;

        foreach (var member in result.MemberNames) {
            if (string.IsNullOrEmpty(member)) {
                continue;
            }

            named = true;

            var flow = context.Report(namer.ToFieldName(member), ValidationCodes.Custom, message);

            if (flow.ShouldStop) {
                return flow;
            }
        }

        if (named) {
            return ValidationFlow.Continue;
        }

        return field is null
            ? context.ReportHere(ValidationCodes.Custom, message)
            : context.Report(field, ValidationCodes.Custom, message);
    }

    /// <summary>
    /// Runs <see cref="IValidatableObject.Validate"/> and maps every result it yields.
    /// </summary>
    /// <remarks>
    /// The caller gates this on nothing else having failed, which is
    /// <c>Validator.TryValidateObject</c>'s sequencing: object-level validation runs only when
    /// every attribute passed. The gate lives in generated code rather than here because the
    /// generated validator owns its ordering.
    /// </remarks>
    public static ValidationFlow ValidateObject(
        ref ValidationContext context, IValidatableObject value, IValidationFieldNamer namer) {

        var objectContext = CreateContext(context.Services, value, null, value.GetType().Name);

        foreach (var result in value.Validate(objectContext)) {
            var flow = Apply(ref context, result, null, namer);

            if (flow.ShouldStop) {
                return flow;
            }
        }

        return ValidationFlow.Continue;
    }

    /// <summary>
    /// A DataAnnotations context with everything resolvable at build time already resolved.
    /// </summary>
    /// <remarks>
    /// Public because generated code builds one to hand a context-taking
    /// <c>[CustomValidation]</c> method, which receives the context directly rather than through
    /// an attribute.
    /// </remarks>
    /// <param name="services">The pass's provider, so <c>GetService</c> works; null on a boolean pass.</param>
    /// <param name="instance">The object being validated.</param>
    /// <param name="memberName">The CLR member name, or null for a type-level context.</param>
    /// <param name="displayName">The display name, resolved at build time.</param>
    public static System.ComponentModel.DataAnnotations.ValidationContext CreateContext(
        IServiceProvider? services, object instance, string? memberName, string displayName) =>
#if NET10_0_OR_GREATER
        // The one constructor without [RequiresUnreferencedCode]: taking the display name as a
        // parameter is what removes the reflective resolution the others are annotated for.
        new(instance, displayName, services, items: null) { MemberName = memberName };
#else
        Net8Context(services, instance, memberName, displayName);

    // net8.0 predates the displayName constructor, so this calls an annotated one. The annotation
    // guards the lazy DisplayName resolution - a reflective walk over the instance type's members
    // looking for [Display] - and setting DisplayName (and MemberName) explicitly below means that
    // path is never entered: the property getter returns the assigned value without resolving.
    // The same resolution has already happened, correctly, at build time - the generator read
    // [Display] off the symbol and passed the answer in. A private helper rather than a branch in
    // CreateContext, so the suppression stays off the public surface and the API snapshot reads
    // the same on both target frameworks.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "DisplayName and MemberName are assigned explicitly, so the reflective " +
            "display-name resolution the constructor is annotated for is never reached.")]
    private static System.ComponentModel.DataAnnotations.ValidationContext Net8Context(
        IServiceProvider? services, object instance, string? memberName, string displayName) =>
        new(instance, services, items: null) { MemberName = memberName, DisplayName = displayName };
#endif

    /// <summary>
    /// The result's message, or a composed fallback for the rare result carrying none - a shape
    /// DataAnnotations itself never produces through an attribute, but a hand-written
    /// <c>[CustomValidation]</c> method can.
    /// </summary>
    private static string Message(DataAnnotationsResult result, string? field) =>
        result.ErrorMessage is { Length: > 0 } message
            ? message
            : field is null ? "validation failed." : string.Concat(field, " is invalid.");
}
