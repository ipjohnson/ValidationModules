namespace ValidationModules;

/// <summary>
/// Declares rules for a type you do not own, from outside it.
/// </summary>
/// <remarks>
/// <para>
/// The escape hatch, and deliberately more verbose than putting attributes on the model. An
/// earlier version of this design had overlays as the primary mechanism and it was significantly
/// worse to use.
/// </para>
/// <para>
/// The overlay class is a declaration site, never invoked. Its members mirror the target's
/// properties by name and type; the generator checks both and reports a diagnostic on a mismatch,
/// so renaming a property upstream breaks the build with a clear message rather than silently
/// dropping a rule. Overlay rules and in-type rules for the same property union.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [ValidationOverlayFor&lt;Pet&gt;]
/// public sealed partial class PetOverlay {
///     [Required]
///     public string? Tag { get; }
/// }
/// </code>
/// </example>
/// <typeparam name="TTarget">The type these rules apply to.</typeparam>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ValidationOverlayForAttribute<TTarget> : Attribute;
