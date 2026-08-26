namespace ValidationModules;

/// <summary>
/// Identity for a position in an object graph, independent of how that position is rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Suppression used to key on the rendered field path. That path is
/// deliberately bounded - <see cref="ValidationContext"/> keeps the outermost and immediate parent
/// segment and elides the middle - so two genuinely different fields can render to the same string.
/// Keyed on that string, suppression treated them as one field and dropped real failures: three
/// invalid items in a collection reported one <c>required</c> error between them. Identity now
/// comes from the structural coordinates instead, which the elision does not touch.
/// </para>
/// <para>
/// <b>Structural, not referential.</b> The fold takes the member and the index, never the object.
/// Object addresses move under a compacting GC, so an identity built from them would change
/// mid-pass; and value types have no reference identity to take without boxing. Coordinates have
/// neither problem, and they make the identity reproducible: the same document shape yields the
/// same ids on every run, which is what any future baseline or suppression file would need.
/// </para>
/// <para>
/// <b>Mix rather than add.</b> Addition collides. <c>Add(id, 0)</c> is <c>id</c>, so
/// <c>items[0]</c> would share an identity with <c>items</c> itself, and an object's high field
/// ordinals reach into whatever the next base happens to be. The finalizer below avalanches even a
/// zero input, so sibling indices land far apart.
/// </para>
/// </remarks>
internal static class StructuralId {

    /// <summary>Root of every context-derived identity.</summary>
    internal const ulong Seed = 0xCBF29CE484222325UL;

    /// <summary>
    /// Root of identities for errors that arrive already pathed, through
    /// <see cref="ValidationErrorCollector.Add(in ValidationError)"/>. Adapters mapping another
    /// engine's failures have no coordinates to fold, only a finished path, so they get their own
    /// space rather than pretending to structural identity. The two never need to cross-suppress:
    /// an adapter produces every error for its model, and a walked context produces every error for
    /// its own.
    /// </summary>
    internal const ulong AdapterSeed = 0x9E3779B97F4A7C15UL;

    /// <summary>splitmix64's finalizer, folding <paramref name="value"/> into <paramref name="id"/>.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong id, ulong value) {
        id ^= value * 0x9E3779B97F4A7C15UL;
        id = (id ^ (id >> 30)) * 0xBF58476D1CE4E5B9UL;
        id = (id ^ (id >> 27)) * 0x94D049BB133111EBUL;
        return id ^ (id >> 31);
    }

    /// <summary>
    /// FNV-1a over the UTF-16 units. Deliberately not <see cref="string.GetHashCode()"/>, which is
    /// randomised per process and would give the same document a different identity on every run.
    /// </summary>
    internal static ulong Hash(string value) {
        var hash = Seed;

        for (var i = 0; i < value.Length; i++) {
            hash ^= value[i];
            hash *= 0x100000001B3UL;
        }

        return hash;
    }
}
