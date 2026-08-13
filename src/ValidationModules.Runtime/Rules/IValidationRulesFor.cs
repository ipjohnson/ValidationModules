namespace ValidationModules;

/// <summary>
/// Declares validation rules for <typeparamref name="T"/> from outside it, in a method body.
/// </summary>
/// <remarks>
/// <para>
/// The declaration form for a type you do not own, and the only one that can express a rule
/// spanning two properties. See API-SURFACE.md §19.
/// </para>
/// <para>
/// <b><see cref="Describe"/> has two consumers, and that is the whole design.</b> The source
/// generator <i>reads</i> the body at build time and flattens it into straight-line code;
/// <see cref="DescribedValidator{T}"/> <i>runs</i> it once and walks the rules it recorded. The
/// first costs a branch per rule and needs our generator, the second costs a delegate call and needs
/// nothing at all - which is what lets another source generator emit a rules class, register it, and
/// have validation work with none of this package's build-time machinery present.
/// </para>
/// <para>
/// Either way the rule set is built once. A generated validator has no rule set at all, and
/// <see cref="DescribedValidator{T}"/> is a singleton that calls <see cref="Describe"/> from its
/// constructor, so plan §2's "rule graphs are built once, never per validation call" holds on both
/// paths by construction.
/// </para>
/// <para>
/// <b>The body is a whitelisted DSL, not general C#.</b> A local, a loop, a condition, or a call to
/// anything that is not on the builder is a build error (VM0070) rather than something quietly
/// dropped. A runnable body looks like ordinary code, which is exactly why the part that cannot be
/// compiled has to break the build instead of behaving differently on the two paths.
/// </para>
/// </remarks>
/// <typeparam name="T">The type these rules apply to.</typeparam>
public interface IValidationRulesFor<T> {

    /// <summary>
    /// Declares the rules. Never called by user code, and called at most once per process by the
    /// runtime engine.
    /// </summary>
    /// <param name="rules">Accumulates the declarations.</param>
    void Describe(ValidationRules<T> rules);
}

/// <summary>
/// A hand-written rule applied through <see cref="ValidationRules{T}.Apply"/>.
/// </summary>
/// <remarks>
/// Taken as a method group rather than as a <c>(Type, string)</c> pair. That pair is how
/// <c>[Pattern(typeof(Patterns), "Sku")]</c> has to spell a member reference, because an attribute
/// cannot hold a method group - a method body can, so the constraint that forced it does not apply
/// and taking the group directly gets compile-time checking, go-to-definition and rename for free.
/// </remarks>
/// <param name="context">Accumulates failures and carries the current field path.</param>
/// <param name="value">The value being validated.</param>
public delegate void RuleAction<in T>(ref ValidationContext context, T value);
