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
        SchemaVersion raisesMinimumReaderTo,
        string? caution = null)
    {
        Version = version;
        Phase = phase;
        Description = description;
        Statements = statements;
        RaisesMinimumReaderTo = raisesMinimumReaderTo;
        Caution = caution;
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

    /// <summary>
    /// What this does to rows that already exist, or null when it does nothing to them.
    /// </summary>
    /// <remarks>
    /// <b>The description is about the schema; this is about the data.</b> ADR-018
    /// condition 4. An operator reading *Add ownership and sharing scope to layers* has been
    /// told the truth and has not been told that everything they published is about to become
    /// private.
    /// </remarks>
    public string? Caution { get; }

    /// <summary>Creates an expand migration.</summary>
    /// <param name="version">The level this migration brings the store to.</param>
    /// <param name="description">What it does, for the migration report.</param>
    /// <param name="statements">The statements, in order.</param>
    public static Migration Expand(
        SchemaVersion version, string description, params string[] statements) =>
        new(version, MigrationPhase.Expand, Require(description, nameof(description)),
            Validated(statements), SchemaVersion.None);

    /// <summary>
    /// The same migration, carrying a sentence about what it does to data that is already
    /// there.
    /// </summary>
    /// <param name="caution">
    /// What it does to rows that already exist, in the operator's terms rather than the
    /// schema's. Printed by the plan before anything runs.
    /// </param>
    /// <returns>A copy with the caution attached.</returns>
    /// <remarks>
    /// <para>
    /// <b>[ADR-018](../../../docs/adr/ADR-018-authorization-and-roles.md) condition 4</b>:
    /// *the upgrade is walked on a store that already has layers, and the operator is told
    /// that existing layers became private. Silently privatising somebody's published data is
    /// a worse regression than the closed default was.*
    /// </para>
    /// <para>
    /// <b>A description is about the schema and a caution is about the data, and until
    /// 2026-08-27 there was only the first.</b> `SharingV5` reads *Add ownership and sharing
    /// scope to layers*, which is true, complete as a description, and tells an operator
    /// nothing about the fact that every layer they have published is about to stop being
    /// visible. The rollback window already had a warning of this kind; nothing else did.
    /// </para>
    /// <para>
    /// <b>A method rather than an overload, and that is not a style choice.</b> It was written
    /// first as `Expand(version, description, caution, params string[] statements)`. Every
    /// existing call passes a description and then SQL, so overload resolution took each
    /// migration's **first statement** as its caution and dropped it from the statements —
    /// silently, for the whole history. It was caught only because one migration has exactly
    /// one statement and became a migration that does nothing, which throws at type
    /// initialisation. A signature that can reinterpret every existing call site is not one
    /// to have.
    /// </para>
    /// <para>
    /// <b>Optional, because most migrations have nothing to say.</b> Adding a nullable column
    /// changes no row's meaning. A caution on every step would be noise, and noise is how the
    /// one that matters gets skipped.
    /// </para>
    /// </remarks>
    public Migration Cautioning(string caution) =>
        new(Version, Phase, Description, Statements, RaisesMinimumReaderTo,
            Require(caution, nameof(caution)));

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
