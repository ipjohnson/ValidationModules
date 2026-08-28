namespace ValidationModules;

/// <summary>
/// A validator reached by the runtime type of a value rather than by a static type argument.
/// </summary>
/// <remarks>
/// <para>
/// One adapter is emitted per validated type and registered by that assembly's
/// <c>Add&lt;Assembly&gt;Validators()</c>. The registering assembly knows the type statically, so
/// there is no <c>MakeGenericType</c>, no <c>Activator</c> and no scanning anywhere in the path -
/// dispatch is a <c>GetType()</c> and a dictionary lookup, which is a method-table read and a handle
/// compare. AOT- and trim-clean, and the adapters are rooted by the generated registration.
/// </para>
/// <para>
/// Adapters exist for every validated type, including ones carrying no constraints of their own.
/// That is what makes a lookup miss unambiguous: it can only mean the declaring assembly's
/// registration was never called, never that the type had nothing to check - so the throw can be
/// unconditional and can say what to do about it.
/// </para>
/// </remarks>
public interface IDynamicValidator {

    /// <summary>The type this validates. The key it is registered under.</summary>
    Type ValidatedType { get; }

    /// <summary>
    /// Validates <paramref name="value"/>, which must be of <see cref="ValidatedType"/>, and
    /// answers whether the pass carries on.
    /// </summary>
    ValidationFlow Validate(ref ValidationContext context, object value);

    /// <summary>The boolean form, returning at the first failure.</summary>
    bool IsValid(object value);
}
