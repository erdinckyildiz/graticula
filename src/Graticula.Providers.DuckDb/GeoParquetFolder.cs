using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Graticula.Geometries;

namespace Graticula.Providers.DuckDb;

/// <summary>How much of the machine a folder's DuckDB may use.</summary>
/// <remarks>
/// <b>Bounded by default, because DuckDB's own default is not a server's.</b> DuckDB takes 80% of
/// physical memory and every core unless told otherwise — the right choice for an analyst's
/// notebook and the wrong one for a process that also answers every other request, on a machine
/// that is running PostgreSQL beside it.
/// </remarks>
public sealed record GeoParquetOptions
{
    /// <summary>DuckDB's <c>memory_limit</c> for one folder, in DuckDB's own spelling.</summary>
    public string MemoryLimit { get; init; } = "1GB";

    /// <summary>DuckDB's <c>threads</c> for one folder, or null for its default.</summary>
    public int? Threads { get; init; }
}

/// <summary>A column of a GeoParquet file, as DuckDB types it.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Type">DuckDB's type, e.g. <c>BIGINT</c> or <c>DECIMAL(10,2)</c>.</param>
public sealed record GeoParquetColumn(string Name, string Type);

/// <summary>One GeoParquet file in a registered folder, as a layer would be served from it.</summary>
/// <param name="Name">The file name without <c>.parquet</c> — the layer's table name.</param>
/// <param name="Path">The file's full path, with forward slashes.</param>
/// <param name="Rows">How many rows the file's footer says it holds.</param>
/// <param name="Version">Changes whenever the file does; the same for every row in it.</param>
/// <param name="Geometry">What the file's <c>geo</c> metadata says.</param>
/// <param name="Columns">Every column except the geometry and its covering box.</param>
/// <param name="Kind">The geometry kind, from the metadata or from the first row that has one.</param>
/// <param name="IdentityCandidates">
/// Columns that can be the layer's object id: integer columns measured unique and never null, and
/// <see cref="GeoParquetFolder.RowNumberColumn"/>.
/// </param>
/// <param name="CandidateObjectIdColumn">The candidate a publisher is offered first.</param>
/// <param name="Problem">Why a layer cannot be served from this file, or null.</param>
public sealed record GeoParquetTable(
    string Name,
    string Path,
    long Rows,
    string Version,
    GeoParquetMetadata Geometry,
    IReadOnlyList<GeoParquetColumn> Columns,
    GeometryKind? Kind,
    IReadOnlyList<string> IdentityCandidates,
    string? CandidateObjectIdColumn,
    string? Problem);

/// <summary>
/// A folder of GeoParquet files, and the sandboxed DuckDB that reads them — ADR-066 §2.
/// </summary>
/// <remarks>
/// <para>
/// <b>One in-memory DuckDB per registered folder, confined to that folder.</b> Before anything
/// else runs, <c>allowed_directories</c> is set to the folder, extension installing and loading
/// are switched off, external access is switched off, and the configuration is locked so that no
/// later statement can undo any of it. Measured 2026-09-13 on DuckDB 1.5.5: a read outside the
/// folder, <c>glob</c> outside it, <c>INSTALL</c>, <c>LOAD</c> and changing a setting all fail
/// with a permission error after this, on the root connection and on every duplicate.
/// </para>
/// <para>
/// <b>What the sandbox does not stop, measured the same day: a write inside the folder.</b>
/// <c>COPY … TO</c> and <c>ATTACH</c> into the allowed directory succeed. Nothing in this server
/// hands DuckDB a caller's SQL — the where clause is rebuilt from a parsed tree
/// (<see cref="Graticula.Features.PredicateSql"/>) and every identifier is a name this code
/// already had — so that is a property of the engine rather than a path to it, and the
/// deployment's answer is to mount the folder read-only. ADR-066 §3 carries it.
/// </para>
/// <para>
/// <b>Order matters and was found by getting it wrong</b>: <c>allowed_directories</c> must be set
/// before <c>enable_external_access</c> is switched off, or the folder itself is refused.
/// </para>
/// </remarks>
public sealed partial class GeoParquetFolder : IDisposable
{
    /// <summary>
    /// DuckDB's virtual column numbering a file's rows from zero — the identity of a file that has
    /// no unique integer column of its own.
    /// </summary>
    /// <remarks>
    /// <b>Stable for exactly as long as the file is.</b> A GeoParquet file is immutable, so row
    /// 41 is row 41 until somebody replaces the file — and then every id may move, which is why
    /// <see cref="GeoParquetTable.Version"/> changes with the file and a client holding ids is
    /// told the layer changed. A unique integer column in the file survives a rewrite and is
    /// offered first whenever there is one.
    /// </remarks>
    public const string RowNumberColumn = "file_row_number";

    private const int MostIdentityColumnsMeasured = 8;

    private static readonly string[] PreferredIdentities =
        ["objectid", "fid", "ogc_fid", "gid", "id", "oid"];

    private readonly DuckDBConnection _root;
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified, GeoParquetTable Table)> _tables =
        new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Opens a sandboxed DuckDB over one folder.</summary>
    /// <param name="folder">The folder, which must exist.</param>
    /// <param name="options">Memory and thread bounds.</param>
    public GeoParquetFolder(string folder, GeoParquetOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        options ??= new GeoParquetOptions();

        string full = System.IO.Path.GetFullPath(folder);

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The folder '{full}' does not exist.");
        }

        Folder = Normalise(full);

        _root = new DuckDBConnection("DataSource=:memory:");
        _root.Open();

        try
        {
            List<string> settings =
            [
                $"set memory_limit = {Literal(options.MemoryLimit)}",
                "set global TimeZone = 'UTC'",
                $"set allowed_directories = [{Literal(Folder + "/")}]",
                "set autoinstall_known_extensions = false",
                "set autoload_known_extensions = false",
                "set allow_community_extensions = false",
                "set enable_external_access = false",
            ];

            if (options.Threads is > 0 and var threads)
            {
                settings.Insert(1, $"set threads = {threads.ToString(CultureInfo.InvariantCulture)}");
            }

            // Last, so nothing after it — here or in any duplicate — can change the lines above.
            settings.Add("set lock_configuration = true");

            foreach (string setting in settings)
            {
                using DuckDBCommand command = _root.CreateCommand();
                command.CommandText = setting;
                command.ExecuteNonQuery();
            }

            using DuckDBCommand version = _root.CreateCommand();
            version.CommandText = "select version()";
            EngineVersion = "DuckDB " + (string)version.ExecuteScalar()!;
        }
        catch
        {
            _root.Dispose();
            throw;
        }
    }

    /// <summary>The folder, absolute, with forward slashes and no trailing one.</summary>
    public string Folder { get; }

    /// <summary>The engine and its version, e.g. <c>DuckDB v1.5.5</c>.</summary>
    public string EngineVersion { get; }

    /// <summary>A new connection to this folder's DuckDB, which the caller disposes.</summary>
    /// <returns>An open connection sharing the sandboxed database.</returns>
    /// <remarks>
    /// <b>Opened here because DuckDB.NET's duplicate is not</b> — measured, it arrives closed.
    /// A connection per statement is DuckDB's own model for concurrency: one database, any number
    /// of connections, each used by one thread at a time.
    /// </remarks>
    internal DuckDBConnection Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DuckDBConnection connection = _root.Duplicate();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        return connection;
    }

    /// <summary>Every GeoParquet file directly in the folder, publishable or not.</summary>
    /// <returns>One entry per <c>.parquet</c> file, each with its problem if it has one.</returns>
    public IReadOnlyList<GeoParquetTable> List()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<GeoParquetTable> tables = [];

        foreach (string file in Directory
            .EnumerateFiles(Folder, "*.parquet", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(file);

            if (!PlainName().IsMatch(name) || name.Length > 63)
            {
                tables.Add(Unreadable(name, Normalise(file),
                    "The file name is not a plain identifier of at most 63 letters, digits and "
                    + "underscores, and a layer's table name must be one. Renaming the file serves it."));
                continue;
            }

            tables.Add(Find(name) ?? Unreadable(name, Normalise(file), "The file vanished while it was being read."));
        }

        return tables;
    }

    /// <summary>The file a layer's table name refers to, or null when there is none.</summary>
    /// <param name="name">The file name without <c>.parquet</c>.</param>
    /// <returns>The file as a layer would be served from it, read again only when it has changed.</returns>
    public GeoParquetTable? Find(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string path = PathOf(name);
        FileInfo file = new(path);

        if (!file.Exists)
        {
            return null;
        }

        // <b>A link is refused rather than followed</b> — a security review's finding. The sandbox
        // compares paths as text, so a `parcels.parquet` that links to a file elsewhere passes every
        // check on its name and reads whatever it points at. A file in a registered folder is the
        // operator's to place, and a link is somewhere else being placed.
        if (file.LinkTarget is not null)
        {
            return Unreadable(name, path,
                "The file is a symbolic link, and a GeoParquet layer reads only files that are in its "
                + "folder. Copy or move the file into the folder instead.");
        }

        if (_tables.TryGetValue(name, out var cached)
            && cached.Length == file.Length
            && cached.Modified == file.LastWriteTimeUtc)
        {
            return cached.Table;
        }

        GeoParquetTable table;

        try
        {
            table = Read(name, path, file);
        }
        catch (Exception failure) when (failure is DuckDBException or WkbFormatException
            or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            // Remembered like any other answer: the same bytes will fail the same way, and a
            // listing that re-reads a broken file on every request pays for it every time.
            table = Unreadable(name, path, $"The file could not be read: {FirstLine(failure.Message)}");
        }

        _tables[name] = (file.Length, file.LastWriteTimeUtc, table);
        return table;
    }

    /// <summary>The path a table name refers to.</summary>
    /// <param name="name">The file name without <c>.parquet</c>.</param>
    /// <returns>The full path with forward slashes.</returns>
    public string PathOf(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>A name, never a path.</b> The table name is interpolated into a statement as part
        // of a string literal and joined to the folder, so a separator or a parent reference in
        // it would be a way out of the folder the sandbox would then have to catch.
        if (!PlainName().IsMatch(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a plain file name. A GeoParquet layer names its file without "
                + "the extension, in letters, digits and underscores.", nameof(name));
        }

        return Folder + "/" + name + ".parquet";
    }

    /// <summary>The table expression for a file, with the row-number column when it is needed.</summary>
    internal static string TableExpression(GeoParquetTable table, bool rowNumbers) =>
        rowNumbers
            ? $"read_parquet({Literal(table.Path)}, file_row_number = true)"
            : $"read_parquet({Literal(table.Path)})";

    /// <summary>A SQL string literal.</summary>
    internal static string Literal(string text) =>
        "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>A quoted SQL identifier.</summary>
    internal static string Quote(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _root.Dispose();
    }

    private static string Normalise(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static GeoParquetTable Unreadable(string name, string path, string problem) =>
        new(name, path, 0, string.Empty, GeoParquetMetadata.Parse(null), [], null, [], null, problem);

    private GeoParquetTable Read(string name, string path, FileInfo file)
    {
        // <b>Hashed, so an entity tag does not tell a stranger the file's size and when it was
        // written</b> — a security review's note. It still changes whenever either does.
        string version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Create(
                CultureInfo.InvariantCulture, $"{file.Length}:{file.LastWriteTimeUtc.Ticks}"))))[..16]
            .ToLowerInvariant();

        using DuckDBConnection connection = Open();

        string? geo = Scalar(connection,
            $"select decode(value) from parquet_kv_metadata({Literal(path)}) where decode(key) = 'geo'")
            as string;

        long rows = Convert.ToInt64(
            Scalar(connection, $"select num_rows from parquet_file_metadata({Literal(path)})"),
            CultureInfo.InvariantCulture);

        GeoParquetMetadata metadata = GeoParquetMetadata.Parse(geo);

        if (metadata.Problem is { } unusable)
        {
            // Said before DuckDB is asked for the columns, because a file whose metadata is wrong
            // is one DuckDB may refuse to describe at all, and its sentence is about its reader.
            return new GeoParquetTable(name, path, rows, version, metadata, [], null, [], null, unusable);
        }

        List<GeoParquetColumn> columns = [];
        bool geometryFound = false;

        using (DuckDBCommand describe = connection.CreateCommand())
        {
            describe.CommandText = $"describe select * from read_parquet({Literal(path)})";

            using DuckDBDataReader reader = describe.ExecuteReader();

            while (reader.Read())
            {
                string column = reader.GetString(0);
                string type = reader.GetString(1);

                if (string.Equals(column, metadata.Column, StringComparison.Ordinal))
                {
                    geometryFound = true;
                    continue;
                }

                if (string.Equals(column, metadata.Covering, StringComparison.Ordinal)
                    || type.StartsWith("GEOMETRY", StringComparison.Ordinal))
                {
                    continue;
                }

                columns.Add(new GeoParquetColumn(column, type));
            }
        }

        string? problem = null;

        if (!geometryFound)
        {
            problem = $"The 'geo' metadata names '{metadata.Column}' as the geometry and the file has no such column.";
        }

        if (problem is null && !PlainName().IsMatch(metadata.Column))
        {
            problem =
                $"The geometry column '{metadata.Column}' is not a plain identifier, and a layer's "
                + "geometry column must be one.";
        }

        GeometryKind? kind = metadata.Kind;

        if (problem is null && kind is null)
        {
            kind = SampleKind(connection, path, metadata.Column);
        }

        (IReadOnlyList<string> candidates, string? preferred) = problem is null
            ? Identities(connection, path, columns)
            : ([], null);

        return new GeoParquetTable(
            name, path, rows, version, metadata, columns, kind, candidates, preferred, problem);
    }

    private static GeometryKind? SampleKind(DuckDBConnection connection, string path, string column)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText =
            $"select st_aswkb({Quote(column)}) from read_parquet({Literal(path)}) "
            + $"where {Quote(column)} is not null limit 1";

        using DuckDBDataReader reader = command.ExecuteReader();

        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        using Stream stream = reader.GetStream(0);
        byte[] wkb = new byte[stream.Length];
        stream.ReadExactly(wkb);

        return WkbReader.Read(wkb).Kind;
    }

    /// <summary>Integer columns measured unique and never null, and the row number.</summary>
    /// <remarks>
    /// <b>Measured rather than taken from the name</b>, because a Parquet file has no primary key
    /// and a column called <c>id</c> is a hope. One statement counts every candidate at once —
    /// DuckDB answers <c>count(distinct …)</c> over a few million rows in a fraction of a second —
    /// and at most eight columns are measured, preferred names first, so that a wide file of
    /// integers does not make listing a folder expensive.
    /// </remarks>
    private static (IReadOnlyList<string> Candidates, string? Preferred) Identities(
        DuckDBConnection connection, string path, IReadOnlyList<GeoParquetColumn> columns)
    {
        List<string> integers = columns
            .Where(c => IsInteger(c.Type) && PlainName().IsMatch(c.Name))
            .Select(c => c.Name)
            .OrderBy(c => PreferenceOf(c))
            .Take(MostIdentityColumnsMeasured)
            .ToList();

        List<string> unique = [];

        if (integers.Count > 0)
        {
            StringBuilder sql = new("select count(*)");

            foreach (string column in integers)
            {
                sql.Append(", count(distinct ").Append(Quote(column)).Append("), count(")
                   .Append(Quote(column)).Append(')');
            }

            sql.Append(" from read_parquet(").Append(Literal(path)).Append(')');

            using DuckDBCommand command = connection.CreateCommand();
            command.CommandText = sql.ToString();

            using DuckDBDataReader reader = command.ExecuteReader();

            if (reader.Read())
            {
                long total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);

                for (int i = 0; i < integers.Count; i++)
                {
                    long distinct = Convert.ToInt64(reader.GetValue(1 + (2 * i)), CultureInfo.InvariantCulture);
                    long present = Convert.ToInt64(reader.GetValue(2 + (2 * i)), CultureInfo.InvariantCulture);

                    if (distinct == total && present == total)
                    {
                        unique.Add(integers[i]);
                    }
                }
            }
        }

        // <b>A column of the file's own before the row number, whatever it is called.</b> The two
        // are equally unique today and not equally stable: a rewrite that reorders the rows moves
        // every row number and leaves `osm_id` where it was. Measured on GDAL's conversion of
        // OpenStreetMap roads and places, where the first version of this offered the row number
        // over a unique `osm_id` because the name was not on the preferred list.
        string? preferred = unique.FirstOrDefault();

        if (!columns.Any(c => string.Equals(c.Name, RowNumberColumn, StringComparison.OrdinalIgnoreCase)))
        {
            unique.Add(RowNumberColumn);
            preferred ??= RowNumberColumn;
        }

        return (unique, preferred);
    }

    private static int PreferenceOf(string column)
    {
        int at = Array.FindIndex(
            PreferredIdentities, p => string.Equals(p, column, StringComparison.OrdinalIgnoreCase));

        return at < 0 ? PreferredIdentities.Length : at;
    }

    internal static bool IsInteger(string type) => BaseType(type) is
        "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT"
        or "UTINYINT" or "USMALLINT" or "UINTEGER";

    internal static string BaseType(string type)
    {
        int open = type.IndexOf('(', StringComparison.Ordinal);
        return (open < 0 ? type : type[..open]).Trim().ToUpperInvariant();
    }

    private static object? Scalar(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string FirstLine(string message)
    {
        int newline = message.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? message : message[..newline];
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();
}
