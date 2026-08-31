namespace ValidationModules.Constraints;

/// <summary>
/// The collection's elements must all differ. Emits code <c>unique_items</c>.
/// </summary>
/// <remarks>
/// <para>
/// OpenAPI's <c>uniqueItems</c>. No arguments; presence is the constraint.
/// </para>
/// <para>
/// <b>The one constraint here that is not a comparison.</b> Every other rule compiles to a branch
/// over a value the validator already has. Uniqueness has to look at the elements against each
/// other, so it calls <see cref="ConstraintChecks.AllUnique{T}"/> - which stays off the heap for
/// the collection sizes a request body actually carries, and falls back to a set above them.
/// </para>
/// <para>
/// Elements are compared with <c>EqualityComparer&lt;T&gt;.Default</c>: value equality for records,
/// primitives and anything implementing <see cref="System.IEquatable{T}"/>, and reference equality
/// for a class that overrides none of it. That last case passes for the wrong reason, so the
/// generator reports VM1202 rather than letting it through quietly.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [UniqueItems]
/// public List&lt;string&gt; Tags { get; init; } = [];
/// </code>
/// </example>
public sealed class UniqueItemsAttribute : ValidationConstraintAttribute;
