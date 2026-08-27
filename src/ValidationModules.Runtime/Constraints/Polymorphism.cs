namespace ValidationModules.Constraints;

/// <summary>
/// How a nested descent treats a value whose runtime type is more derived than the property's
/// declared type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always named, never inferred.</b> Dispatching automatically over whatever subtypes the
/// generator happens to see would make coverage depend on physical assembly layout: write
/// <c>Payment</c>, <c>Card</c> and <c>Bank</c> together and dispatch works; extract <c>Card</c> to
/// a package, or let a consumer add <c>Crypto : Payment</c>, and coverage silently shrinks with no
/// code change, no warning and no failing test. Unearned confidence is worse than no feature, so
/// polymorphic behaviour is always something the author asked for by name.
/// </para>
/// <para>
/// For the same reason the diagnostic that prompts for a mode (VM0031) keys on whether the target
/// is sealed - a local, layout-independent fact - and never on which subtypes are visible from
/// here. A diagnostic keyed on subtype visibility would appear and disappear across assembly
/// boundaries, reintroducing the exact problem it exists to prevent.
/// </para>
/// </remarks>
public enum Polymorphism {

    /// <summary>
    /// The declared type's rules and nothing else. Emits no switch and costs nothing.
    /// </summary>
    /// <remarks>
    /// The default, and today's behaviour - chosen deliberately rather than fallen into. A sealed
    /// target needs nothing else, because it can have no subtypes.
    /// </remarks>
    DeclaredOnly,

    /// <summary>
    /// A type-test chain over the subtypes visible at build time, most-derived first.
    /// </summary>
    /// <remarks>
    /// Exactly one branch runs, nothing allocates, and no container is involved. The declared
    /// type's own validator is the fallback arm rather than an extra call, because each subtype's
    /// validator already checks everything it inherits - running both would report the base's
    /// failures twice.
    /// </remarks>
    CompileTime,

    // Runtime - resolve a validator for the value's runtime type from the container - lands with
    // the machinery behind it rather than ahead of it. Adding an enum member is source- and
    // binary-compatible, so there is nothing to gain by publishing one now whose only behaviour
    // would be a build error, and that is a failure mode IMPLEMENTATION-PLAN.md §17 calls out by
    // name. See docs/design/CONDITIONS-AND-POLYMORPHISM.md, phase 6.
}
