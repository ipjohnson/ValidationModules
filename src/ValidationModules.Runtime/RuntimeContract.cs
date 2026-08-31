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
    // the failure would land inside generated code - which is what VM5001 exists to prevent.
    //
    //   Superseded, and NestedValidation is gone. The emitter now takes the nested type's
    //   validators as an IEnumerable<IValidatorFor<Nested>> constructor parameter and holds them as
    //   an array, which composes the same set without a per-descent container lookup. Nothing has
    //   emitted a ValidateRegistered call since, so no generated code referenced the method when it
    //   was removed, and ValidationContext.Services went with it - it had no other reader.
    //
    //   This is the one removal the additive-only rule below does not cover, and it is deliberate:
    //   a framework author who compiled Impl at contract 2 would emit calls to a method a current
    //   runtime no longer has. No stable Impl has shipped, so nobody is in that position - but the
    //   number cannot express a removal, which is why this is written down rather than encoded.

    // 2 -> 3: [MultipleOf] and [UniqueItems] arrived, and neither compiles to a comparison the way
    // every constraint before them did. Both call into ConstraintChecks, and both report through an
    // AddMultipleOf/AddUniqueItems that a contract-2 runtime does not have. The same bump covers
    // AddRangeAtLeast/AddRangeAtMost, which partially-bounded [Range] needs.

    // 3 -> 4: ValidationContext.Services arrived, and with it Polymorphism.Runtime - a descent that
    // resolves a validator for the value's runtime type through the provider on the collector. The
    // emitter now writes calls to DynamicValidation, which a contract-3 runtime does not have, and
    // reads ctx.Services, which it also does not have. Both would fail inside generated code, which
    // is what VM5001 exists to prevent.

    // 4 -> 5: IValidatorFor<T>.Validate returns ValidationFlow instead of void, and the report
    // helpers are Report* rather than Add* and return it too. The emitter now writes
    // `if (test && ctx.ReportX(...).ShouldStop) return ValidationFlow.Stop;` and returns a flow from
    // every generated Validate, none of which a contract-4 runtime can supply or accept.
    //
    // Unlike every bump before it this is not additive - the old members are gone rather than
    // joined. The additive-only rule below is what makes a bump sufficient for a framework author
    // compiling Impl themselves, and it does not cover this; the version gate turns what would be
    // an error inside generated code into VM5001 at the call site, which is the best available
    // outcome. Taken before 1.0.0 pins the surface, which is the only time it is cheap.

    // 5 -> 6: the DataAnnotations compatibility surface compiles instead of being diagnosed away.
    // The format validators emit calls to ConstraintChecks.IsEmail/IsPhone/IsUrl/IsCreditCard/
    // IsBase64/HasFileExtension and report through ReportEmail and friends; custom
    // ValidationAttribute subclasses, [CustomValidation] methods and IValidatableObject emit calls
    // into DataAnnotationsSupport and report under ValidationCodes.Custom. None of it exists in a
    // contract-5 runtime. One bump for both, because no release shipped between them; additive, as
    // the rule below requires.

    // 6 -> 7: IConstraintFor<T> arrived - the instance shape of a custom constraint. The emitter
    // hoists the constructed attribute into a static field (or constructs it per check under
    // [PerValidationInstance]) and weaves calls to its IsValid and Validate members, neither of
    // which a contract-6 runtime declares. Additive, as the rule below requires.

    // 7 -> 8: the rules surface became read-only and the runtime engine left.
    // IValidationRulesFor<T>.Describe is static abstract with a symbolic subject parameter;
    // ValidationRules<T> and PropertyRules<T,TValue> are inert and value-based; and
    // DescribedValidator<T>, AddDescribedValidator, the ICompiledRule machinery,
    // ConditionalRules<T> and IValidatorProvider are gone. Additive for the emitter:
    // IValidationContextReporter, and the Report* helpers moved to a receiver constrained to it -
    // emitted call sites are textually unchanged, so nothing older than this is required of them.
    //
    // Like 4 -> 5 this is not additive overall - the rules-engine members are removed, which the
    // number cannot express, so it is written down here instead. Taken while the rules surface had
    // zero consumers outside this repo (verified against ~/Hardened), which is what priced the
    // break at a snapshot re-verify and this bump.

    // 8 -> 9: the message became data. ValidationError carries Value
    // and MessageInfo and renders Message on read; IValidationContextReporter gains the structured
    // Report(field, code, value, info) overload; ValidationMessageInfo, its singletons,
    // ValidationMessageTemplates and ReportDeniedValues arrive, and the Report* helpers grow
    // value/exclusivity parameters. The emitter now writes structured Report calls, hoists
    // per-site static infos, and references the template fields - none of which a contract-8
    // runtime declares.
    //
    // Not additive overall, twice. ValidationError stops being a positional record - the
    // three-argument constructor and Deconstruct survive as declared members, so call sites
    // compile unchanged, but the positional-with expression over Message does not. And the
    // reporter interface gains a member outright rather than as a default implementation, because
    // a DIM invoked through the constrained generic boxes the context struct. Both taken in the
    // rc window, which is the only time they are cheap; the number cannot express either, so they
    // are written down here, per the rule above.

    // 9 -> 10: language packs. Generated pack classes implement
    // IValidationLanguagePack, and the registration extension registers them and TryAdds a
    // LanguagePackFormatter over the set - none of which a contract-9 runtime declares. The shape
    // keys arrive with them: ValidationMessageTemplates.KeyOf/TemplatesByKey/KnownKeys, and the
    // renderer's override-template entry points. Additive, as the rule below requires.

    // 10 -> 11: authored messages and collection bodies. The emitter writes
    // ctx.ReportAuthored(...) for a constraint carrying Message = … and for an Ensure's explicit
    // message:, so the author's text survives a language pack with a bare key for the code; and
    // the registration extension calls AddCollectionValidatorsFor<T> per validated type, which
    // registers CollectionValidatorFor<T>/CollectionAsyncValidatorFor<T> for List<T> and T[].
    // None of it exists in a contract-10 runtime. Additive, as the rule below requires.

    /// <summary>
    /// The contract this runtime implements. Compared against
    /// <c>EmitterContract.RequiredRuntimeContract</c> by the generator, and against
    /// <c>$(ValidationModulesRuntimeContract)</c> by build tasks driving the emitter.
    /// </summary>
    public const int Version = 11;
}
