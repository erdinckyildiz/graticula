using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Graticula.Platform.Schema;

/// <summary>Which half of expand-and-contract a migration is.</summary>
/// <remarks>
/// ADR-016 §5. A migration may only <b>add</b> in the version that introduces
/// it; removal happens later, once no old instance can still be running. N9
/// called this standard discipline, unstated, and <em>impossible to retrofit
/// after the first migration that breaks it</em> — which is why the model
/// carries it rather than a convention document.
/// </remarks>
public enum MigrationPhase
{
    /// <summary>
    /// Adds only: new columns, tables and indexes, all nullable or defaulted.
    /// Old code ignores them, so the previous version keeps running — which is
    /// what makes rollback possible.
    /// </summary>
    Expand = 1,

    /// <summary>
    /// Removes the old shape. <b>This is the operation that closes the rollback
    /// door</b>, by raising the minimum reader version (ADR-016 §4a).
    /// </summary>
    Contract = 2,
}

/// <summary>
/// One migration step: a version, a phase, and the statements to run.
/// </summary>
public sealed class Migration
{
    private Migration(
        SchemaVersion version,
        MigrationPhase phase,
        string description,
        IReadOnlyList<string> statements,
        SchemaVersion raisesMinimumReaderTo)
    {
        Version = version;
        Phase = phase;
        Description = description;
        Statements = statements;
        RaisesMinimumReaderTo = raisesMinimumReaderTo;
    }

    /// <summary>The level this migration brings the store to.</summary>
    public SchemaVersion Version { get; }

    /// <summary>Expand or contract.</summary>
    public MigrationPhase Phase { get; }

    /// <summary>What it does, in a sentence, for the migration report.</summary>
    public string Description { get; }

    /// <summary>The statements, in order.</summary>
    public IReadOnlyList<string> Statements { get; }

    /// <summary>
    /// For a contract migration, the oldest component version that can still
    /// operate once it has run. <see cref="SchemaVersion.None"/> for an expand.
    /// </summary>
    /// <remarks>
    /// <b>Not simply <see cref="Version"/>.</b> If expand at 5 adds a column and
    /// contract at 6 drops the old one, then after contract the oldest usable
    /// component is the one that stopped needing the old column — version 5, not
    /// 6. Getting this wrong closes the rollback door one version too early and
    /// nobody notices until they need it.
    /// </remarks>
    public SchemaVersion RaisesMinimumReaderTo { get; }

    /// <summary>Creates an expand migration.</summary>
    /// <param name="version">The level this migration brings the store to.</param>
    /// <param name="description">What it does, for the migration report.</param>
    /// <param name="statements">The statements, in order.</param>
    public static Migration Expand(
        SchemaVersion version, string description, params string[] statements) =>
        new(version, MigrationPhase.Expand, Require(description, nameof(description)),
            Validated(statements), SchemaVersion.None);

    /// <summary>
    /// Creates a contract migration.
    /// </summary>
    /// <param name="version">The level this migration brings the store to.</param>
    /// <param name="raisesMinimumReaderTo">
    /// The oldest component version that may run after this. Must be at most
    /// <paramref name="version"/> — a contract cannot require a reader newer
    /// than the schema it produces.
    /// </param>
    /// <param name="description">What it does, for the migration report.</param>
    /// <param name="statements">The statements, in order.</param>
    public static Migration Contract(
        SchemaVersion version,
        SchemaVersion raisesMinimumReaderTo,
        string description,
        params string[] statements)
    {
        if (raisesMinimumReaderTo.IsNone)
        {
            throw new ArgumentException(
                "A contract migration must state the oldest component version that may run "
                + "after it. That number is what closes the rollback window (ADR-016 §4a), and "
                + "leaving it unstated would close the window silently.",
                nameof(raisesMinimumReaderTo));
        }

        if (raisesMinimumReaderTo > version)
        {
            throw new ArgumentException(
                $"A contract at schema {version} cannot require a reader of version "
                + $"{raisesMinimumReaderTo}: no such component can exist yet.",
                nameof(raisesMinimumReaderTo));
        }

        return new Migration(
            version, MigrationPhase.Contract, Require(description, nameof(description)),
            Validated(statements), raisesMinimumReaderTo);
    }

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                "A migration needs a description: it appears in the report the operator reads "
                + "before agreeing to run it (ADR-016 §4b).", parameter)
            : value;

    private static ReadOnlyCollection<string> Validated(string[] statements)
    {
        ArgumentNullException.ThrowIfNull(statements);

        if (statements.Length == 0)
        {
            throw new ArgumentException("A migration must do something.", nameof(statements));
        }

        foreach (string statement in statements)
        {
            if (string.IsNullOrWhiteSpace(statement))
            {
                throw new ArgumentException("A migration statement is empty.", nameof(statements));
            }
        }

        return new ReadOnlyCollection<string>((string[])statements.Clone());
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Version} {Phase}: {Description}";
}
