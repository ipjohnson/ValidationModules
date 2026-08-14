namespace ValidationModules;

/// <summary>
/// The version of the surface generated code binds against.
/// </summary>
/// <remarks>
/// <para>
/// A framework author compiles <c>ValidationModules.SourceGenerator.Impl</c> into their own
/// generator (plan §7.4). Nothing then ties the Impl they built against to the
/// <c>ValidationModules.Runtime</c> the *application* references, so a newer emitter can emit calls
/// a older runtime does not have - and the error surfaces inside generated code, which is the worst
/// possible place for it. Plan §7.5.
/// </para>
/// <para>
/// This is deliberately not the package version. Package versions move for reasons that have
/// nothing to do with the emitted surface - a bug fix, a dependency bump - and gating on them would
/// fail builds that are perfectly compatible. This number changes only when the emitter starts
/// depending on something a previous runtime cannot supply, which is what "keep the emitted surface
/// small and additive-only" means in practice.
/// </para>
/// <para>
/// Bump it when, and only when, an emitter change requires a runtime member that did not exist
/// before. Removing or changing a member is not covered by a bump - the surface is additive-only.
/// </para>
/// </remarks>
public static class RuntimeContract {

    // 1 -> 2: the emitter began calling NestedValidation.ValidateRegistered after every nested
    // descent, so that a validator registered for the nested type composes the same way one
    // registered for the top-level type always has. A runtime at contract 1 has no such method, and
    // the failure would land inside generated code - which is what VM0040 exists to prevent.

    // 2 -> 3: [MultipleOf] and [UniqueItems] arrived, and neither compiles to a comparison the way
    // every constraint before them did. Both call into ConstraintChecks, and both report through an
    // AddMultipleOf/AddUniqueItems that a contract-2 runtime does not have. The same bump covers
    // AddRangeAtLeast/AddRangeAtMost, which partially-bounded [Range] needs.

    /// <summary>
    /// The contract this runtime implements. Compared against
    /// <c>EmitterContract.RequiredRuntimeContract</c> by the generator, and against
    /// <c>$(ValidationModulesRuntimeContract)</c> by build tasks driving the emitter.
    /// </summary>
    public const int Version = 3;
}
