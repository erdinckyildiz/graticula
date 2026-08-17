using System;

namespace Graticula.Platform.Schema;

/// <summary>Why a component may or may not run against a platform store.</summary>
public enum SchemaCompatibilityOutcome
{
    /// <summary>The component may run.</summary>
    Compatible = 0,

    /// <summary>The store has never been migrated.</summary>
    NotInitialised = 1,

    /// <summary>
    /// A contract migration has removed something this component still uses.
    /// <b>The rollback window has closed.</b>
    /// </summary>
    ComponentTooOld = 2,

    /// <summary>
    /// The component expects a schema level that has not been applied yet. The
    /// migration has not been run.
    /// </summary>
    ComponentTooNew = 3,
}

/// <summary>The result of the startup handshake, with the message to show.</summary>
/// <param name="Outcome">What was decided.</param>
/// <param name="Explanation">
/// A complete sentence for an operator, naming both versions and the direction.
/// </param>
public readonly record struct SchemaCompatibilityResult(
    SchemaCompatibilityOutcome Outcome,
    string Explanation)
{
    /// <summary><see langword="true"/> when the component may start.</summary>
    public bool IsCompatible => Outcome == SchemaCompatibilityOutcome.Compatible;
}

/// <summary>
/// The startup handshake. Two questions, asked of two numbers.
/// </summary>
/// <remarks>
/// <para>
/// <b>This refuses; it does not migrate</b> (ADR-016 §4b). Auto-migration on
/// startup is how an old container started by accident — a stale tag, a
/// rollback, a stray <c>docker run</c> — silently rewrites a newer schema, and
/// the result is unrecoverable and looks like corruption rather than a mistake.
/// </para>
/// <para>
/// Pure by design. ADR-016 §10 condition 1 requires the refusal to be tested by
/// deliberately starting a stale component, and a decision about two integers
/// should not need a database to verify.
/// </para>
/// </remarks>
public static class SchemaCompatibility
{
    /// <summary>Decides whether a component may operate against a store.</summary>
    /// <param name="componentName">
    /// Which component is asking — <c>server</c>, <c>job-worker</c>. Named in the
    /// message so an operator reading a log knows which of the three refused.
    /// </param>
    /// <param name="componentSchemaVersion">The schema level the component was built against.</param>
    /// <param name="stamp">The store's stamp, or <see langword="null"/> if never migrated.</param>
    public static SchemaCompatibilityResult Check(
        string componentName,
        SchemaVersion componentSchemaVersion,
        SchemaStamp? stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);

        if (componentSchemaVersion.IsNone)
        {
            throw new ArgumentException(
                "A component must declare the schema level it was built against.",
                nameof(componentSchemaVersion));
        }

        if (stamp is null)
        {
            return new SchemaCompatibilityResult(
                SchemaCompatibilityOutcome.NotInitialised,
                $"The platform store has no schema. {componentName} expects schema "
                + $"{componentSchemaVersion}. Run the migration to create it — it is an "
                + "explicit operation and will report what it intends to do before doing it.");
        }

        if (componentSchemaVersion < stamp.MinimumReader)
        {
            return new SchemaCompatibilityResult(
                SchemaCompatibilityOutcome.ComponentTooOld,
                $"{componentName} is built for schema {componentSchemaVersion}, and the "
                + $"platform store requires at least {stamp.MinimumReader} (it is at "
                + $"{stamp.Applied}). A contract migration has removed something this version "
                + "still uses, so it cannot be rolled back to. "
                + "Recovery is restore-from-backup — which discards every write made since "
                + "that backup was taken.");
        }

        if (componentSchemaVersion > stamp.Applied)
        {
            return new SchemaCompatibilityResult(
                SchemaCompatibilityOutcome.ComponentTooNew,
                $"{componentName} is built for schema {componentSchemaVersion}, and the "
                + $"platform store is at {stamp.Applied}. The migration has not been run. "
                + "Run it, or start the version of this component that matches the store.");
        }

        return new SchemaCompatibilityResult(
            SchemaCompatibilityOutcome.Compatible,
            $"{componentName} at schema {componentSchemaVersion} is compatible with the "
            + $"platform store ({stamp}).");
    }
}
