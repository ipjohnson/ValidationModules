namespace ValidationModules;

/// <summary>
/// Declares validation rules for <typeparamref name="T"/> from outside it, in a method body that is
/// read at build time and never run.
/// </summary>
/// <remarks>
/// <para>
/// The declaration form for a type you do not own, and the only one that can express a rule
/// spanning two properties.
/// </para>
/// <para>
/// <b><see cref="Describe"/> has one consumer: the source generator.</b> It transcribes the body
/// into the generated validator - vocabulary calls become check-and-report code, and every other
/// statement is copied through and runs at validation time inside that validator. Nothing
/// instantiates a rules class (<c>Describe</c> is static, so <c>this</c> cannot compile) and
/// nothing invokes it (the builder it takes cannot be constructed). Under trimming and Native AOT
/// the class disappears entirely.
/// </para>
/// <para>
/// <b>A breakpoint in <c>Describe</c> never hits.</b> The method is read, not run. Step through the
/// generated validator under <c>obj/…/generated</c> instead, which is straight-line code.
/// </para>
/// </remarks>
/// <typeparam name="T">The type these rules apply to.</typeparam>
public interface IValidationRulesFor<T> {

    /// <summary>
    /// Declares the rules. Read by the source generator, never called.
    /// </summary>
    /// <param name="rules">The vocabulary. Inert by construction; it exists to be read.</param>
    /// <param name="x">
    /// The subject, symbolically. It never holds a value; it exists so member access typechecks,
    /// renames propagate and go-to-definition works.
    /// </param>
    static abstract void Describe(ValidationRules<T> rules, T x);
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
/// <returns>
/// Whether the pass carries on. Return what <c>context.Report(...)</c> handed back to honour
/// <see cref="ValidationStopMode.StopOnFirstError"/>; returning
/// <see cref="ValidationFlow.Continue"/> unconditionally is legal and simply means this rule never
/// ends a pass early.
/// </returns>
public delegate ValidationFlow RuleAction<in T>(ref ValidationContext context, T value);
