namespace ValidationModules.Constraints;

/// <summary>
/// Opts an <c>IConstraintFor&lt;T&gt;</c> attribute class out of the shared instance: the
/// generator constructs a fresh instance every time the constraint is checked, instead of once
/// into a static field.
/// </summary>
/// <remarks>
/// <para>
/// The shared instance is the default because it is free, and it carries a contract: the attribute
/// is called concurrently and must be immutable after construction. This marker is for the
/// implementation that cannot honour that - one keeping per-call state in fields, or wrapping
/// something genuinely not thread-safe. It buys isolation at the cost of an allocation on every
/// check of every value, paid whether the value passes or fails; the generator states that cost
/// with an Info (VM1603) at each use site, because it is the one constraint cost a clean pass
/// pays.
/// </para>
/// <para>
/// On the attribute class, not the declaration site: statefulness is a property of the
/// implementation, and a subclass inherits it. Meaningful only on a class implementing
/// <c>IConstraintFor&lt;T&gt;</c> - the static check of a <c>CustomConstraintAttribute</c> has no
/// instance to isolate, and a DataAnnotations attribute is constructed by the bridge's rules.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PerValidationInstanceAttribute : Attribute;
