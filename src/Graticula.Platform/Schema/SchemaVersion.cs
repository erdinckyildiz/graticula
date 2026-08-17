using System;
using System.Globalization;

namespace Graticula.Platform.Schema;

/// <summary>
/// A platform store schema level. Monotonic, starting at 1.
/// </summary>
/// <remarks>
/// Deliberately not semantic versioning. A schema level answers one question —
/// <em>which migrations have run</em> — and ordering is the only operation on
/// it. Semver would invite arguments about whether a change is major.
/// </remarks>
public readonly struct SchemaVersion : IEquatable<SchemaVersion>, IComparable<SchemaVersion>
{
    /// <summary>
    /// No schema. A database that has never been migrated, which is distinct
    /// from a database at level zero — there is no level zero.
    /// </summary>
    public static SchemaVersion None => default;

    /// <summary>The first real schema level.</summary>
    public static SchemaVersion First => new(1);

    /// <summary>Creates a schema version.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is negative. Zero is permitted and means
    /// <see cref="None"/>.
    /// </exception>
    public SchemaVersion(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    /// <summary>The level. Zero means <see cref="None"/>.</summary>
    public int Value { get; }

    /// <summary><see langword="true"/> when this is <see cref="None"/>.</summary>
    public bool IsNone => Value == 0;

    /// <summary>The next level up.</summary>
    public SchemaVersion Next() => new(Value + 1);

    /// <inheritdoc/>
    public int CompareTo(SchemaVersion other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    public bool Equals(SchemaVersion other) => Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SchemaVersion other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value;

    /// <summary>Equality.</summary>
    public static bool operator ==(SchemaVersion left, SchemaVersion right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(SchemaVersion left, SchemaVersion right) => !left.Equals(right);

    /// <summary>Ordering.</summary>
    public static bool operator <(SchemaVersion left, SchemaVersion right) => left.Value < right.Value;

    /// <summary>Ordering.</summary>
    public static bool operator >(SchemaVersion left, SchemaVersion right) => left.Value > right.Value;

    /// <summary>Ordering.</summary>
    public static bool operator <=(SchemaVersion left, SchemaVersion right) => left.Value <= right.Value;

    /// <summary>Ordering.</summary>
    public static bool operator >=(SchemaVersion left, SchemaVersion right) => left.Value >= right.Value;

    /// <inheritdoc/>
    public override string ToString() =>
        IsNone ? "none" : Value.ToString(CultureInfo.InvariantCulture);
}
