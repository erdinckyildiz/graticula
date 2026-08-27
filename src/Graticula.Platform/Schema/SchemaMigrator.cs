using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Schema;

/// <summary>
/// What a migration would do, produced before anything runs.
/// </summary>
/// <param name="From">The store's current stamp, or <see langword="null"/> if un-migrated.</param>
/// <param name="To">The stamp the store would hold afterwards.</param>
/// <param name="Pending">The migrations that would run, in order.</param>
/// <param name="ClosesRollbackWindow">
/// <see langword="true"/> when this raises the minimum reader version, after
/// which the previous version can no longer start.
/// </param>
public readonly record struct MigrationReport(
    SchemaStamp? From,
    SchemaStamp To,
    IReadOnlyList<Migration> Pending,
    bool ClosesRollbackWindow)
{
    /// <summary><see langword="true"/> when the store is already current.</summary>
    public bool IsUpToDate => Pending.Count == 0;

    /// <summary>
    /// The report an operator reads before agreeing to run this.
    /// </summary>
    /// <remarks>
    /// ADR-016 §4b requires migration to report what it will do before doing it.
    /// The rollback warning is the part that matters — it is the one consequence
    /// that cannot be undone.
    /// </remarks>
    public string Describe()
    {
        if (IsUpToDate)
        {
            return $"The platform store is up to date ({From}).";
        }

        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"{Pending.Count} migration(s) to apply, ");
        text.Append(From is null
            ? "creating a new platform store"
            : $"taking the platform store from {From}");
        text.Append(CultureInfo.InvariantCulture, $" to {To}:");

        foreach (Migration migration in Pending)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n  {migration}");
        }

        // <b>What the pending migrations do to rows that already exist -- ADR-018
        // condition 4.</b> The lines above are descriptions of the *schema*, and a schema
        // description can be complete and still leave an operator unaware that everything
        // they have published is about to become invisible. Collected rather than printed
        // inline so that they are read as consequences rather than as more of the list, and
        // only when there are any: a caution beside every step is noise, and noise is how
        // the one that matters gets skipped.
        // <b>Only on an upgrade, never on a creation.</b> A new store has no rows to change
        // the meaning of, so *every layer that already exists becomes private* is true and
        // vacuous there -- and a warning that fires when it does not apply is one an operator
        // learns to scroll past before the day it does.
        Migration[] cautions = From is null
            ? []
            : Pending.Where(m => !string.IsNullOrWhiteSpace(m.Caution)).ToArray();

        if (cautions.Length > 0)
        {
            text.Append("\n\nWhat this does to data that is already there:");

            foreach (Migration migration in cautions)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"\n  {migration.Version}: {migration.Caution}");
            }
        }

        if (ClosesRollbackWindow)
        {
            text.Append(
                "\n\nWARNING: this closes the rollback window. After it runs, any component "
                + $"older than version {To.MinimumReader} will refuse to start, and recovery "
                + "to such a version becomes restore-from-backup — which discards every write "
                + "made since that backup was taken.");
        }

        return text.ToString();
    }
}

/// <summary>
/// Runs pending migrations, one at a time, in order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never invoked at startup.</b> ADR-016 §4b: migration is an explicit
/// operation, because auto-migration is how an old container started by accident
/// silently rewrites a newer schema. Startup calls
/// <see cref="SchemaCompatibility.Check"/> and refuses; an operator calls this.
/// </para>
/// </remarks>
public sealed class SchemaMigrator
{
    private readonly IPlatformSchemaStore _store;
    private readonly MigrationSet _migrations;

    /// <summary>Creates a migrator.</summary>
    public SchemaMigrator(IPlatformSchemaStore store, MigrationSet migrations)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(migrations);

        _store = store;
        _migrations = migrations;
    }

    /// <summary>
    /// Works out what would happen, without changing anything.
    /// </summary>
    public async Task<MigrationReport> PlanAsync(CancellationToken cancellationToken = default)
    {
        SchemaStamp? current = await _store.ReadStampAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Migration> pending = _migrations.Pending(current);

        if (pending.Count == 0)
        {
            SchemaStamp unchanged = current ?? throw new InvalidOperationException(
                "An un-migrated store with no pending migrations is not a reachable state.");

            return new MigrationReport(current, unchanged, pending, ClosesRollbackWindow: false);
        }

        return new MigrationReport(
            current,
            _migrations.Project(current),
            pending,
            _migrations.ClosesRollbackWindow(current));
    }

    /// <summary>
    /// Applies every pending migration and returns what was done.
    /// </summary>
    /// <remarks>
    /// Each migration is applied with its resulting stamp as one atomic unit —
    /// see <see cref="IPlatformSchemaStore.ApplyAsync"/>. If one fails, earlier
    /// ones stay applied and the stamp reflects exactly how far it got, so
    /// re-running resumes rather than repeating.
    /// </remarks>
    public async Task<MigrationReport> ApplyAsync(CancellationToken cancellationToken = default)
    {
        MigrationReport plan = await PlanAsync(cancellationToken).ConfigureAwait(false);

        if (plan.IsUpToDate)
        {
            return plan;
        }

        SchemaStamp? stamp = plan.From;
        List<Migration> applied = [];

        foreach (Migration migration in plan.Pending)
        {
            stamp = stamp is null
                ? SchemaStamp.Initial(migration)
                : stamp.AfterApplying(migration);

            await _store.ApplyAsync(migration, stamp, cancellationToken).ConfigureAwait(false);
            applied.Add(migration);
        }

        return new MigrationReport(
            plan.From,
            stamp!,
            new ReadOnlyCollection<Migration>(applied),
            plan.ClosesRollbackWindow);
    }
}
