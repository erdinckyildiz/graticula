using System;
using System.Collections.Generic;
using System.Linq;

namespace Graticula.Platform.Admin;

/// <summary>
/// What an enterprise geodatabase does to a table that a plain read or write of it would get wrong — ADR-217, the
/// ArcGIS veteran's second review, N6.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the table's own columns and the geodatabase's table registry, both publicly documented</b> (Esri's
/// *"Geodatabase system tables"* and *"Branch versioning"* pages): a branch-versioned class carries
/// <c>GDB_BRANCH_ID</c>; a class archived without versioning carries <c>GDB_FROM_DATE</c> and <c>GDB_TO_DATE</c>; a class
/// registered as traditionally versioned has delta tables named <c>A</c> and <c>D</c> followed by its registration id.
/// Nothing here reads a geodatabase's internals beyond those names — ADR-013 §3, and §5 of the project's rules.
/// </para>
/// <para>
/// <b>Refused at publish, not served wrongly.</b> A branch-versioned or archived class holds every past state of every
/// row in the one table, so a plain read answers deleted and superseded rows as current; a traditionally versioned
/// class keeps its edits in the delta tables, so a plain read misses them and a plain write bypasses them. A class that
/// is only registered, neither versioned nor archived, is a table ArcGIS itself lets SQL edit, and is served as one.
/// </para>
/// </remarks>
public static class GeodatabaseClasses
{
    /// <summary>The columns that mark a branch-versioned or archived class, as ArcGIS names them.</summary>
    public static IReadOnlyList<string> MarkerColumns { get; } = ["GDB_BRANCH_ID", "GDB_FROM_DATE", "GDB_TO_DATE", "GDB_ARCHIVE_OID"];

    /// <summary>
    /// Why a table cannot be published as it is, or null when nothing here marks it.
    /// </summary>
    /// <param name="table">The table, as the publisher named it.</param>
    /// <param name="columns">Its column names, in any case.</param>
    /// <param name="versionedRegistration">
    /// The registration id when the geodatabase's registry has the table and its delta tables exist — traditionally
    /// versioned — or null.
    /// </param>
    /// <returns>The sentence, or null.</returns>
    public static string? Refusal(string table, IEnumerable<string> columns, long? versionedRegistration)
    {
        ArgumentNullException.ThrowIfNull(columns);
        HashSet<string> named = new(columns, StringComparer.OrdinalIgnoreCase);

        if (named.Contains("GDB_BRANCH_ID"))
        {
            return $"'{table}' is branch versioned in an enterprise geodatabase: its rows hold every version's edits "
                + "(GDB_BRANCH_ID), deleted and superseded ones included, and this server does not read a version, so it "
                + "would answer them all as current. Publish a view that selects the version you want, or serve the class "
                + "from ArcGIS Enterprise (ADR-217).";
        }

        if (named.Contains("GDB_FROM_DATE") && named.Contains("GDB_TO_DATE"))
        {
            return $"'{table}' is archived in an enterprise geodatabase: its rows include every past state "
                + "(GDB_FROM_DATE, GDB_TO_DATE), so a plain read would answer history as current. Publish a view of its "
                + "current rows — those whose GDB_TO_DATE is the archive's open end (ADR-217).";
        }

        if (versionedRegistration is { } id)
        {
            return $"'{table}' is versioned in an enterprise geodatabase (registration {id}): its edits are kept in delta "
                + $"tables beside it (A{id}, D{id}), so reading the table misses them and writing it would bypass them. "
                + $"Publish its versioned view, '{table}_EVW', which shows the default version (ADR-217).";
        }

        return null;
    }
}
