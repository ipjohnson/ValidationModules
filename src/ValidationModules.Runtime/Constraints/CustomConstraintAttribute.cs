namespace ValidationModules.Constraints;

/// <summary>
/// The base for a constraint attribute of your own, whose check compiles into the validator as a
/// direct static call. Emits code <c>custom</c>.
/// </summary>
/// <remarks>
/// <para>
/// Derive from this and declare a <c>public static bool IsValid</c> whose first parameter takes
/// the member type the attribute applies to. Constructor arguments flow into the check: each extra
/// <c>IsValid</c> parameter is matched positionally against the constructor's parameters, and the
/// generator passes the constant the declaration supplied - so one attribute class expresses a
/// family of parameterized checks, and everything is resolved before the program runs.
/// </para>
/// <para>
/// <b>This is the high-performance shape of a custom DataAnnotations attribute.</b> A
/// <c>ValidationAttribute</c> subclass is constructed and invoked through
/// <c>GetValidationResult</c>, paying DataAnnotations' costs - a context per check, a box for
/// value-type members. This compiles to the branch you would have written by hand: no instance, no
/// context, no boxing, nothing allocated on a passing value. The trade is that the check must be a
/// static method over statically renderable arguments, which is also what makes it verifiable at
/// build time - a mistake in the shape is a build error (VM1601) naming what to fix.
/// </para>
/// <para>
/// A null member value passes, as it does for every constraint except <c>[Required]</c> - the
/// generated guard skips the check rather than handing your method a null. Declare
/// <c>[Required]</c> beside it when absence should fail. <see cref="ValidationConstraintAttribute.Code"/>,
/// <see cref="ValidationConstraintAttribute.Message"/>, <see cref="ValidationConstraintAttribute.When"/>
/// and <see cref="ValidationConstraintAttribute.Unless"/> work here exactly as they do on the
/// built-in constraints - and the class may bake its own defaults as constants named
/// <c>DefaultMessage</c> and <c>DefaultCode</c> (on itself or a base), which every use site gets
/// unless it overrides. Constants rather than constructor assignments, because nothing ever
/// constructs the attribute: the generator reads it. Anything else the check needs must arrive
/// through the constructor: a custom init-only property has no path into a static method, and
/// setting one is a build error rather than an argument that silently never arrives.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class SkuAttribute : CustomConstraintAttribute {
///     public const string DefaultMessage = "sku must look like SKU-XXXXXXXX";
///
///     public SkuAttribute(int length) { }
///
///     public static bool IsValid(string value, int length) =>
///         value.Length == length &amp;&amp; value.StartsWith("SKU-", StringComparison.Ordinal);
/// }
///
/// public record Product {
///     [Required]
///     [Sku(12)]
///     public string? Sku { get; init; }
/// }
/// </code>
/// </example>
public abstract class CustomConstraintAttribute : ValidationConstraintAttribute;
