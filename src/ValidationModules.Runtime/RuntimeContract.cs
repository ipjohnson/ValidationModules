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

    /// <summary>
    /// The contract this runtime implements. Compared against
    /// <c>EmitterContract.RequiredRuntimeContract</c> by the generator, and against
    /// <c>$(ValidationModulesRuntimeContract)</c> by build tasks driving the emitter.
    /// </summary>
    public const int Version = 1;
}
