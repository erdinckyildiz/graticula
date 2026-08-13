using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GisServer.Platform.Schema;

/// <summary>
/// The ordered, validated set of migrations a component ships with.
/// </summary>
/// <remarks>
/// Validated at construction rather than at run time, so a malformed sequence
/// fails in a unit test on a developer's machine instead of half way through an
/// upgrade at 2 AM.
/// </remarks>
public sealed class MigrationSet
{
    /// <summary>Creates a migration set, validating the sequence.</summary>
    /// <exception cref="ArgumentException">The sequence is not a valid history.</exception>
    public MigrationSet(IReadOnlyList<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        if (migrations.Count == 0)
        {
            throw new ArgumentException("A migration set needs at least one migration.", nameof(migrations));
        }

        Migration[] ordered = migrations.ToArray();

        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i] is null)
            {
                throw new ArgumentException($"Migration {i} is null.", nameof(migrations));
            }

            if (i > 0 && ordered[i].Version <= ordered[i - 1].Version)
            {
                throw new ArgumentException(
                    $"Migrations must ascend without gaps in ordering: {ordered[i].Version} follows "
                    + $"{ordered[i - 1].Version}. A history that can be reordered is a history that "
                    + "produces different schemas on different machines.",
                    nameof(migrations));
            }
        }

        if (ordered[0].Phase == MigrationPhase.Contract)
        {
            throw new ArgumentException(
                "The first migration cannot be a contract: there is nothing to remove.",
                nameof(migrations));
        }

        All = new ReadOnlyCollection<Migration>(ordered);
        Latest = ordered[^1].Version;
    }

    /// <summary>Every migration, in order.</summary>
    public IReadOnlyList<Migration> All { get; }

    /// <summary>The schema level reached by applying all of them.</summary>
    public SchemaVersion Latest { get; }

    /// <summary>
    /// The migrations not yet applied to a store in state
    /// <paramref name="stamp"/>, in order. Empty when the store is current.
    /// </summary>
    public IReadOnlyList<Migration> Pending(SchemaStamp? stamp)
    {
        SchemaVersion applied = stamp?.Applied ?? SchemaVersion.None;

        List<Migration> pending = [];
        foreach (Migration migration in All)
        {
            if (migration.Version > applied)
            {
                pending.Add(migration);
            }
        }

        return pending;
    }

    /// <summary>
    /// The stamp that would result from applying every pending migration.
    /// </summary>
    /// <remarks>
    /// Computed <em>before</em> anything runs, so the migration report can tell
    /// an operator not only what will change but <b>whether it closes the
    /// rollback door</b> — which is the one consequence they cannot undo.
    /// </remarks>
    public SchemaStamp Project(SchemaStamp? stamp)
    {
        IReadOnlyList<Migration> pending = Pending(stamp);

        if (pending.Count == 0)
        {
            return stamp ?? throw new InvalidOperationException(
                "An un-migrated store with no pending migrations is not a reachable state.");
        }

        SchemaStamp result = stamp ?? SchemaStamp.Initial(pending[0]);
        int start = stamp is null ? 1 : 0;

        for (int i = start; i < pending.Count; i++)
        {
            result = result.AfterApplying(pending[i]);
        }

        return result;
    }

    /// <summary>
    /// <see langword="true"/> when applying the pending migrations would raise
    /// the minimum reader version, ending the ability to roll back.
    /// </summary>
    public bool ClosesRollbackWindow(SchemaStamp? stamp) =>
        stamp is not null && Project(stamp).MinimumReader > stamp.MinimumReader;
}
