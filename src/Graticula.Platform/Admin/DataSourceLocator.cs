using System;

namespace Graticula.Platform.Admin;

/// <summary>The kinds of data source a registration may name.</summary>
public static class DataSourceKinds
{
    /// <summary>A PostgreSQL database with PostGIS, reached by a connection string.</summary>
    public const string PostGis = "postgis";

    /// <summary>A folder of GeoParquet files read in place by DuckDB — ADR-066.</summary>
    public const string GeoParquet = "geoparquet";

    /// <summary>
    /// GeoParquet files in an S3 bucket prefix, or one file over https, read by DuckDB — ADR-067 §5.2.
    /// </summary>
    public const string GeoParquetRemote = "geoparquet-remote";

    /// <summary>A DuckDB database file under the GeoParquet root, its tables read as layers — ADR-067 §5.3.</summary>
    public const string DuckDb = "duckdb";

    /// <summary>A MotherDuck database, its tables read as layers — ADR-067 §5.4.</summary>
    public const string MotherDuck = "motherduck";
}

/// <summary>
/// How a GeoParquet folder is written where a connection string would be.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stored locator names its engine — ADR-066 §4.</b> A registered source is held as one
/// sealed string, and that string reaches a dozen places that have only the string: the pool
/// register, the quiesce register, the preview, the layer catalogue, the probe. Threading a kind
/// beside it through each of them would be a dozen chances to forget one, and the place that
/// forgot would treat a folder as a PostgreSQL connection string and fail with a parse error
/// about semicolons. A locator that says <c>geoparquet:</c> at its front is routed correctly by
/// anything that looks, and refused by Npgsql's parser by anything that does not.
/// </para>
/// <para>
/// <b>No PostgreSQL connection string can begin this way</b> — they are
/// <c>key=value</c> pairs — so the prefix is unambiguous rather than a convention.
/// <c>data_source.kind</c> says the same thing in the catalogue, and registration writes the
/// two together.
/// </para>
/// </remarks>
public static class GeoParquetLocator
{
    /// <summary>The prefix.</summary>
    public const string Scheme = "geoparquet:";

    /// <summary>The prefix of a remote location — ADR-067 §5.2 — followed by its JSON description.</summary>
    /// <remarks>
    /// <b>Not a prefix of <see cref="Scheme"/> and not prefixed by it</b>, so a check for one never
    /// matches the other. The JSON carries the location and any S3 credentials, and the whole
    /// locator is sealed like a connection string with a password in it.
    /// </remarks>
    public const string RemoteScheme = "geoparquet-remote:";

    /// <summary>The prefix of a DuckDB database file — ADR-067 §5.3 — followed by its JSON description.</summary>
    public const string DuckDbScheme = "duckdb:";

    /// <summary>The prefix of a MotherDuck database — ADR-067 §5.4 — followed by its JSON description, token included.</summary>
    public const string MotherDuckScheme = "motherduck:";

    /// <summary>Whether a stored locator is served by DuckDB: a local folder or a remote location.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns>Whether it names files rather than a PostgreSQL database.</returns>
    /// <remarks>
    /// <b>Both kinds, because every caller of this asks the same question</b> — is this read-only and
    /// served by DuckDB rather than PostgreSQL — and a remote location answers it the same way a
    /// folder does. Where the two differ, <see cref="IsRemote"/> says which.
    /// </remarks>
    public static bool Is(string? stored) =>
        stored is not null
        && (stored.StartsWith(Scheme, StringComparison.Ordinal)
            || stored.StartsWith(RemoteScheme, StringComparison.Ordinal)
            || stored.StartsWith(DuckDbScheme, StringComparison.Ordinal)
            || stored.StartsWith(MotherDuckScheme, StringComparison.Ordinal));

    /// <summary>Whether a stored locator names a DuckDB database — a file or MotherDuck — rather than files.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns>Whether its tables, not its files, are the layers.</returns>
    public static bool IsAttached(string? stored) =>
        stored is not null
        && (stored.StartsWith(DuckDbScheme, StringComparison.Ordinal)
            || stored.StartsWith(MotherDuckScheme, StringComparison.Ordinal));

    /// <summary>The locator for a DuckDB database file or a MotherDuck database.</summary>
    /// <param name="motherDuck">Whether it is MotherDuck.</param>
    /// <param name="json">Its JSON description.</param>
    /// <returns>The string to seal and store.</returns>
    public static string ForAttached(bool motherDuck, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return (motherDuck ? MotherDuckScheme : DuckDbScheme) + json;
    }

    /// <summary>The JSON description an attached-database locator carries.</summary>
    /// <param name="stored">A locator for which <see cref="IsAttached"/> is true.</param>
    /// <returns>The JSON, token included — never to be shown.</returns>
    public static string AttachedOf(string stored)
    {
        if (stored is null || !IsAttached(stored))
        {
            throw new ArgumentException("That locator does not name a DuckDB database.", nameof(stored));
        }

        return stored[(stored.StartsWith(DuckDbScheme, StringComparison.Ordinal) ? DuckDbScheme : MotherDuckScheme).Length..];
    }

    /// <summary>Whether a stored locator is a remote location rather than a local folder.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns>Whether it begins with <see cref="RemoteScheme"/>.</returns>
    public static bool IsRemote(string? stored) =>
        stored is not null && stored.StartsWith(RemoteScheme, StringComparison.Ordinal);

    /// <summary>The locator for a remote location.</summary>
    /// <param name="json">The location's JSON description, credentials included.</param>
    /// <returns>The string to seal and store.</returns>
    public static string ForRemote(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return RemoteScheme + json;
    }

    /// <summary>The JSON description a remote locator carries.</summary>
    /// <param name="stored">A locator for which <see cref="IsRemote"/> is true.</param>
    /// <returns>The JSON, credentials included — never to be shown.</returns>
    public static string RemoteOf(string stored)
    {
        if (!IsRemote(stored))
        {
            throw new ArgumentException("That locator does not name a remote GeoParquet location.", nameof(stored));
        }

        return stored[RemoteScheme.Length..];
    }

    /// <summary>The locator for a folder.</summary>
    /// <param name="folder">The absolute folder path.</param>
    /// <returns>The string to seal and store.</returns>
    public static string For(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return Scheme + folder;
    }

    /// <summary>The folder a locator names.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The folder path.</returns>
    public static string FolderOf(string stored)
    {
        if (stored is null || !stored.StartsWith(Scheme, StringComparison.Ordinal))
        {
            throw new ArgumentException("That locator does not name a GeoParquet folder.", nameof(stored));
        }

        return stored[Scheme.Length..];
    }

    /// <summary>The kind a stored locator belongs to.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns>The kind, <see cref="DataSourceKinds.PostGis"/> for anything that is not files.</returns>
    public static string KindOf(string? stored) =>
        IsRemote(stored) ? DataSourceKinds.GeoParquetRemote
        : stored is not null && stored.StartsWith(DuckDbScheme, StringComparison.Ordinal) ? DataSourceKinds.DuckDb
        : stored is not null && stored.StartsWith(MotherDuckScheme, StringComparison.Ordinal) ? DataSourceKinds.MotherDuck
        : Is(stored) ? DataSourceKinds.GeoParquet
        : DataSourceKinds.PostGis;
}
