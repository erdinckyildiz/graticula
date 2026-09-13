using System;

namespace Graticula.Platform.Admin;

/// <summary>The kinds of data source a registration may name.</summary>
public static class DataSourceKinds
{
    /// <summary>A PostgreSQL database with PostGIS, reached by a connection string.</summary>
    public const string PostGis = "postgis";

    /// <summary>A folder of GeoParquet files read in place by DuckDB — ADR-066.</summary>
    public const string GeoParquet = "geoparquet";
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

    /// <summary>Whether a stored locator is a GeoParquet folder.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns>Whether it names a folder rather than a database.</returns>
    public static bool Is(string? stored) =>
        stored is not null && stored.StartsWith(Scheme, StringComparison.Ordinal);

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
        if (!Is(stored))
        {
            throw new ArgumentException("That locator does not name a GeoParquet folder.", nameof(stored));
        }

        return stored[Scheme.Length..];
    }

    /// <summary>The kind a stored locator belongs to.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns><see cref="DataSourceKinds.GeoParquet"/> or <see cref="DataSourceKinds.PostGis"/>.</returns>
    public static string KindOf(string? stored) =>
        Is(stored) ? DataSourceKinds.GeoParquet : DataSourceKinds.PostGis;
}
