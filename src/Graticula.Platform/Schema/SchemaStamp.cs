using System;

namespace Graticula.Platform.Schema;

/// <summary>
/// What the platform store records about its own schema: the level applied, and
/// the oldest component version that may operate against it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two numbers, not one</b>, and that is the fix for independent review
/// finding O1 (ADR-016 §4a). As first written, ADR-016 §4 demanded exact version
/// agreement while §5 and §6 required the previous version to keep running
/// against the expanded schema. Both cannot be true, and between them they
/// deleted rolling upgrade <em>and</em> the rollback the ADR exists to provide.
/// </para>
/// <para>
/// The resolution is that a released component cannot declare forward
/// compatibility with a schema that did not exist when it shipped — so the
/// <b>schema</b> declares backward compatibility instead. The information lives
/// where it is known.
/// </para>
/// </remarks>
public sealed class SchemaStamp
{
    /// <summary>Creates a stamp.</summary>
    /// <exception cref="ArgumentException">
    /// The minimum reader is newer than the applied level, which would mean no
    /// component could ever run.
    /// </exception>
    public SchemaStamp(SchemaVersion applied, SchemaVersion minimumReader)
    {
        if (applied.IsNone)
        {
            throw new ArgumentException(
                "A stamp exists only after a migration has run; use null to mean an "
                + "un-migrated store.", nameof(applied));
        }

        if (minimumReader.IsNone)
        {
            throw new ArgumentException(
                "A stamp must state a minimum reader version.", nameof(minimumReader));
        }

        if (minimumReader > applied)
        {
            throw new ArgumentException(
                $"Minimum reader {minimumReader} is newer than the applied schema {applied}: "
                + "no component could satisfy both.", nameof(minimumReader));
        }

        Applied = applied;
        MinimumReader = minimumReader;
    }

    /// <summary>The migration level actually applied.</summary>
    public SchemaVersion Applied { get; }

    /// <summary>
    /// The oldest component version that may safely operate against this schema.
    /// Raised only by a contract migration.
    /// </summary>
    public SchemaVersion MinimumReader { get; }

    /// <summary>
    /// The stamp after applying <paramref name="migration"/>.
    /// </summary>
    /// <remarks>
    /// <b>Expand leaves <see cref="MinimumReader"/> alone. Contract raises it.</b>
    /// That single asymmetry is what makes ADR-016 §6's rollback window a
    /// mechanical property rather than a documented promise — the door is shut by
    /// arithmetic, at a known moment, rather than by an operator remembering a
    /// rule.
    /// </remarks>
    public SchemaStamp AfterApplying(Migration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);

        if (migration.Version <= Applied)
        {
            throw new ArgumentException(
                $"Migration {migration.Version} is not ahead of the applied schema {Applied}.",
                nameof(migration));
        }

        return migration.Phase == MigrationPhase.Contract
            ? new SchemaStamp(migration.Version, Max(MinimumReader, migration.RaisesMinimumReaderTo))
            : new SchemaStamp(migration.Version, MinimumReader);
    }

    /// <summary>The stamp produced by applying <paramref name="migration"/> to an empty store.</summary>
    public static SchemaStamp Initial(Migration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);

        if (migration.Phase == MigrationPhase.Contract)
        {
            throw new ArgumentException(
                "The first migration cannot be a contract: there is nothing to remove.",
                nameof(migration));
        }

        // A fresh store is readable by the component that created it and nothing
        // older, because nothing older ever knew this schema.
        return new SchemaStamp(migration.Version, migration.Version);
    }

    private static SchemaVersion Max(SchemaVersion left, SchemaVersion right) =>
        left >= right ? left : right;

    /// <inheritdoc/>
    public override string ToString() => $"schema {Applied}, minimum reader {MinimumReader}";
}
