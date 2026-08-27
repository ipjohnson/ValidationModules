namespace ValidationModules.Constraints;

/// <summary>
/// Requires an enum-typed member to hold a value the enum actually declares.
/// </summary>
/// <remarks>
/// <para>
/// An enum is an integer with names on some of it. Nothing stops <c>(Status)99</c> existing, and a
/// deserialiser handed <c>99</c> from the wire will produce exactly that - so a handler switching on
/// the value falls through every case it was written for. This is the check that says the value came
/// from the set the type describes.
/// </para>
/// <para>
/// It costs nothing at run time: the members are known while the validator is being written, so the
/// emitted test is a comparison against them rather than a call to <see cref="System.Enum.IsDefined"/>,
/// which would box, search, and need the metadata kept alive under trimming.
/// </para>
/// <para>
/// On a <see cref="System.FlagsAttribute"/> enum a combination of declared members is a legitimate
/// value even though it is not itself declared, so the emitted test is that no bit outside the
/// declared ones is set.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class EnumDefinedAttribute : ValidationConstraintAttribute { }
