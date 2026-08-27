namespace ValidationModules;

/// <summary>
/// The registered <see cref="IDynamicValidator"/> adapters, keyed by the type each validates.
/// </summary>
/// <remarks>
/// Built once when the container first resolves it, not per validation call - the same requirement
/// every rule graph in this library is held to. A second adapter for one type replaces the first
/// rather than composing: unlike <c>IValidatorFor&lt;T&gt;</c>, which a runner merges, this is a
/// lookup and a lookup has one answer.
/// </remarks>
public sealed class DynamicValidatorRegistry {
    private readonly Dictionary<Type, IDynamicValidator> _byType;

    /// <summary>Indexes the adapters every registered assembly contributed.</summary>
    public DynamicValidatorRegistry(IEnumerable<IDynamicValidator> validators) {
        ArgumentNullException.ThrowIfNull(validators);

        _byType = new Dictionary<Type, IDynamicValidator>();

        foreach (var validator in validators) {
            _byType[validator.ValidatedType] = validator;
        }
    }

    /// <summary>The adapter for <paramref name="type"/>, or null when none is registered.</summary>
    public IDynamicValidator? Find(Type type) => _byType.TryGetValue(type, out var found) ? found : null;
}
