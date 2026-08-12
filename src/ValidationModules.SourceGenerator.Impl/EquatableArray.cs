using System.Collections;
using System.Collections.Immutable;

namespace ValidationModules.SourceGenerator.Impl;

/// <summary>
/// An immutable array with value equality, for use inside incremental-generator models.
/// </summary>
/// <remarks>
/// <see cref="ImmutableArray{T}"/> compares by reference, so a record holding one is never equal to
/// a structurally identical rebuild. That silently defeats every cache in the pipeline: the model
/// is recomputed, compares unequal, and every downstream stage re-runs on each keystroke. Wrapping
/// it restores the comparison the pipeline assumes it has.
/// </remarks>
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T> {

    private readonly ImmutableArray<T> _values;

    public EquatableArray(ImmutableArray<T> values) {
        _values = values;
    }

    public static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);

    public int Count => _values.IsDefault ? 0 : _values.Length;

    public T this[int index] => _values[index];

    public bool Equals(EquatableArray<T> other) {
        if (_values.IsDefault || other._values.IsDefault) {
            return _values.IsDefault && other._values.IsDefault;
        }

        if (_values.Length != other._values.Length) {
            return false;
        }

        for (var i = 0; i < _values.Length; i++) {
            if (!_values[i].Equals(other._values[i])) {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode() {
        if (_values.IsDefault) {
            return 0;
        }

        var hash = 17;
        for (var i = 0; i < _values.Length; i++) {
            hash = (hash * 31) + _values[i].GetHashCode();
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() =>
        (_values.IsDefault ? ImmutableArray<T>.Empty : _values).AsEnumerable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class EquatableArrayExtensions {
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> source) where T : IEquatable<T> =>
        new(source.ToImmutableArray());
}
