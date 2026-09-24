using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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

    /// <summary>
    /// The directory DuckDB extensions are loaded from by path — ADR-067 §5.1 — or null when none
    /// may be loaded, which switches remote locations off.
    /// </summary>
    public string? ExtensionDirectory { get; init; }

    /// <summary>How long a remote table's metadata is trusted before it is read again.</summary>
    public TimeSpan RemoteMetadataLifetime { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The MotherDuck extension's file, or null when MotherDuck is not enabled — ADR-067 §5.4. Not in
    /// <see cref="ExtensionDirectory"/>, because the image may not carry it: it is downloaded where
    /// the server can write.
    /// </summary>
    public string? MotherDuckExtension { get; init; }
}

/// <summary>A DuckDB database read as a set of tables — a file on this machine, or MotherDuck — ADR-067 §5.3–5.4.</summary>
/// <param name="File">The <c>.duckdb</c> file's full path, or null for MotherDuck.</param>
/// <param name="MotherDuckDatabase">The MotherDuck database name, or null for a file.</param>
/// <param name="Token">The MotherDuck access token.</param>
/// <param name="DeclaredSrid">
/// The EPSG code the registrant says the file's geometry is in, used for every geometry column whose
/// type carries no reference — which, measured on DuckDB 1.5.5, is every column in a file: a
/// <c>GEOMETRY('OGC:CRS84')</c> column comes back as <c>GEOMETRY</c> when the file is reopened.
/// </param>
public sealed record AttachedDuckDb(string? File, string? MotherDuckDatabase, string? Token, int? DeclaredSrid)
{
    /// <summary>Whether this is MotherDuck rather than a file.</summary>
    public bool IsMotherDuck => MotherDuckDatabase is not null;

    /// <summary>What a sentence may say about it: the file, or <c>md:database</c>.</summary>
    public string Location => IsMotherDuck ? "md:" + MotherDuckDatabase : File!;

    /// <summary>Never the token.</summary>
    /// <returns>The location.</returns>
    public override string ToString() => Location;
}

/// <summary>
/// GeoParquet somewhere other than this machine's disk — ADR-067 §5.2.
/// </summary>
/// <param name="Location">
/// An <c>s3://bucket/prefix/</c> whose <c>.parquet</c> files directly under it are the tables, or
/// one <c>https://…/name.parquet</c> file.
/// </param>
/// <param name="Region">The bucket's region, e.g. <c>us-west-2</c>.</param>
/// <param name="Endpoint">An S3-compatible endpoint host, or null for AWS.</param>
/// <param name="AccessKeyId">The access key id, or null to read anonymously.</param>
/// <param name="SecretAccessKey">The secret access key, with <paramref name="AccessKeyId"/>.</param>
/// <param name="UrlStyle"><c>vhost</c> or <c>path</c>, or null for DuckDB's default.</param>
/// <param name="UseSsl">False only for an S3-compatible endpoint that does not speak TLS.</param>
public sealed record RemoteGeoParquet(
    string Location,
    string? Region = null,
    string? Endpoint = null,
    string? AccessKeyId = null,
    string? SecretAccessKey = null,
    string? UrlStyle = null,
    bool UseSsl = true)
{
    /// <summary>Whether the location is an S3 prefix rather than one file.</summary>
    public bool IsPrefix => Location.EndsWith('/');

    /// <summary>Never the secret: a record's generated text would otherwise print it.</summary>
    /// <returns>The location and whether credentials are held.</returns>
    public override string ToString() =>
        $"{Location}{(AccessKeyId is null ? string.Empty : " (with credentials)")}";
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
/// <param name="Relation">
/// For a table in an attached database, the SQL naming it — <c>src."main"."places"</c> — which is
/// read instead of <paramref name="Path"/>; null for a Parquet file.
/// </param>
/// <param name="SridDeclared">Whether the reference came from the registration rather than the data.</param>
/// <param name="WideIdentityCandidates">
/// The candidates whose values do not fit in 32 bits — V-77: ArcGIS 10.x clients read an object id as a 32-bit
/// number. Offered all the same, by owner decision, and said.
/// </param>
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
    string? Problem,
    string? Relation = null,
    bool SridDeclared = false,
    IReadOnlyList<string>? WideIdentityCandidates = null);

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
    private readonly RemoteGeoParquet? _remote;
    private readonly TimeSpan _remoteLifetime;
    private readonly ConcurrentDictionary<string, (long ReadAt, GeoParquetTable Table)> _remoteTables =
        new(StringComparer.Ordinal);
    private RemoteFiles? _remoteListing;
    private readonly AttachedDuckDb? _attached;
    private AttachedTables? _attachedListing;
    private readonly ConcurrentDictionary<string, (string Version, Envelope? Extent)> _attachedExtents =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GeoParquetTable> _attachedDetails = new(StringComparer.Ordinal);
    private readonly object _attachedRefresh = new();
    private readonly object _attachedExtentGate = new();

    /// <summary>How long one statement reading an attached database's catalogue or extent may run.</summary>
    /// <remarks>
    /// The feature source's own deadline, applied to what it does not cover — a security review found the
    /// listing and the extent scan running unbounded on the request path.
    /// </remarks>
    public static readonly TimeSpan AttachedStatementDeadline = TimeSpan.FromSeconds(30);

    /// <summary>The most tables an attached database lists.</summary>
    public const int MostAttachedTables = 1000;
    private readonly object _remoteRefresh = new();
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
        _remoteLifetime = options.RemoteMetadataLifetime;

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

        (_root, EngineVersion) = OpenConfined(settings);
    }

    /// <summary>Opens a sandboxed DuckDB over a remote location — ADR-067 §5.2.</summary>
    /// <param name="remote">Where the files are, and any credentials.</param>
    /// <param name="options">Bounds, and the directory <c>httpfs</c> is loaded from.</param>
    public GeoParquetFolder(RemoteGeoParquet remote, GeoParquetOptions options)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ExtensionDirectory is null)
        {
            throw new InvalidOperationException(
                "Remote GeoParquet needs DuckDB's httpfs extension, and no extension directory is configured.");
        }

        _remote = remote;
        _remoteLifetime = options.RemoteMetadataLifetime;
        Folder = remote.Location;

        (_root, EngineVersion) = OpenConfined(RemoteSettings(remote, options));
    }

    /// <summary>Opens a sandboxed DuckDB over an attached database — ADR-067 §5.3–5.4.</summary>
    /// <param name="attached">The file or the MotherDuck database.</param>
    /// <param name="options">Bounds, and for MotherDuck the extension's file.</param>
    public GeoParquetFolder(AttachedDuckDb attached, GeoParquetOptions options)
    {
        ArgumentNullException.ThrowIfNull(attached);
        ArgumentNullException.ThrowIfNull(options);

        if (attached.IsMotherDuck && options.MotherDuckExtension is null)
        {
            throw new InvalidOperationException("MotherDuck is not enabled on this server.");
        }

        if (attached.IsMotherDuck && string.IsNullOrEmpty(attached.Token))
        {
            // <b>Refused before DuckDB is asked</b>: with no token the extension opens a browser to sign in
            // and waits a minute for it, measured (ADR-067 §4) — a request thread held for nothing.
            throw new InvalidOperationException("A MotherDuck database needs an access token.");
        }

        _attached = attached;
        _remoteLifetime = options.RemoteMetadataLifetime;
        Folder = attached.Location;

        (_root, EngineVersion) = OpenConfined(AttachedSettings(attached, options));

        try
        {
            using DuckDBCommand attach = _root.CreateCommand();
            attach.CommandText = attached.IsMotherDuck
                ? $"attach {Literal("md:" + attached.MotherDuckDatabase)} as src (read_only)"
                : $"attach {Literal(attached.File!)} as src (read_only)";
            attach.ExecuteNonQuery();
        }
        catch (DuckDBException failure)
        {
            _root.Dispose();

            // A design review: the driver's sentence is for support; the first one a person reads says what to do.
            string said = attached.IsMotherDuck && failure.Message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
                ? "MotherDuck refused the access token — check it was copied whole and has not been revoked. "
                : string.Empty;

            throw new InvalidOperationException($"{said}'{attached.Location}' could not be opened: {FirstLine(failure.Message)}");
        }
    }

    /// <summary>Every statement an attached database's DuckDB runs before it is locked, in order.</summary>
    /// <remarks>
    /// <para>
    /// <b>A file is allowed as two paths and nothing else</b>: the file and its write-ahead log, which
    /// DuckDB reads when it attaches. Its folder is not allowed, so a Parquet file beside it is not
    /// readable through this instance.
    /// </para>
    /// <para>
    /// <b>MotherDuck's token is written globally and before the lock</b>, like the S3 settings and for
    /// the same two reasons: query connections are duplicates and read the global value, and the
    /// extension would otherwise take <c>motherduck_token</c> from this process's environment.
    /// <b>What the lock does not govern is what MotherDuck runs on its own servers</b> — measured, an https
    /// read from a confined, authenticated connection returned content (ADR-067 §3). No caller's SQL
    /// reaches this instance, which is what that rests on.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> AttachedSettings(AttachedDuckDb attached, GeoParquetOptions options)
    {
        List<string> settings =
        [
            $"set memory_limit = {Literal(options.MemoryLimit)}",
            "set global TimeZone = 'UTC'",
            "set autoinstall_known_extensions = false",
            "set autoload_known_extensions = false",
            "set allow_community_extensions = false",
            "set allow_persistent_secrets = false",
        ];

        if (options.Threads is > 0 and var threads)
        {
            settings.Insert(1, $"set threads = {threads.ToString(CultureInfo.InvariantCulture)}");
        }

        if (attached.IsMotherDuck)
        {
            settings.Add($"load {Literal(Normalise(options.MotherDuckExtension!))}");
            settings.Add($"set global motherduck_token = {Literal(attached.Token!)}");
            settings.Add("set allowed_directories = ['md:']");
        }
        else
        {
            settings.Add($"set allowed_paths = [{Literal(attached.File!)}, {Literal(attached.File! + ".wal")}]");
        }

        settings.Add("set enable_external_access = false");
        settings.Add("set lock_configuration = true");

        return settings;
    }

    /// <summary>Whether this instance reads an attached database's tables.</summary>
    public bool IsAttached => _attached is not null;

    /// <summary>The platform directory DuckDB names its extension builds by, for this process.</summary>
    public static string ExtensionPlatform =>
        (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + "_"
        + (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64");

    /// <summary>Where <c>httpfs</c> is expected under an extension directory.</summary>
    /// <param name="directory">The extension directory.</param>
    /// <returns>The file path, with forward slashes.</returns>
    public static string HttpfsPath(string directory) =>
        Normalise(System.IO.Path.Combine(directory, ExtensionPlatform, "httpfs.duckdb_extension"));

    /// <summary>
    /// Every statement a remote location's DuckDB runs before it is locked, in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Credentials by the legacy settings, and never <c>CREATE SECRET</c> — ADR-067 §3.</b>
    /// Measured on 1.5.5, Windows x64 and linux-arm64: any DuckDB secret created before
    /// <c>allowed_directories</c> is set makes DuckDB send reads that are outside the list — to
    /// another bucket, to any https host, and to <c>http://127.0.0.1</c>. The <c>s3_*</c> settings
    /// in the same position leave the list intact. A test reads this list for the word.
    /// </para>
    /// <para>
    /// <b>The order is the sandbox.</b> The extension is loaded while loading is still allowed, the
    /// credentials are set while settings may still change, the allow-list is set before external
    /// access is switched off (or the location itself is refused, as with a folder), and the lock
    /// is last.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> RemoteSettings(RemoteGeoParquet remote, GeoParquetOptions options)
    {
        List<string> settings =
        [
            $"set memory_limit = {Literal(options.MemoryLimit)}",
            "set global TimeZone = 'UTC'",
            "set autoinstall_known_extensions = false",
            "set autoload_known_extensions = false",
            "set allow_community_extensions = false",
            "set allow_persistent_secrets = false",
            $"load {Literal(HttpfsPath(options.ExtensionDirectory!))}",
        ];

        if (options.Threads is > 0 and var threads)
        {
            settings.Insert(1, $"set threads = {threads.ToString(CultureInfo.InvariantCulture)}");
        }

        // <b>Global, because the connections that run queries are duplicates.</b> A plain `set` changes
        // the session that ran it, and every query here runs on a connection `Open` duplicates from the
        // root — which reads the global value. The first version set these per session: its test of the
        // environment below found the server's own AWS key still in force on the query connection, which
        // also meant a registration's own keys and region had never reached a query.
        //
        // <b>Every S3 setting, every time, including the ones this location does not use.</b> A
        // security review suspected, and a probe measured on 1.5.5: loading httpfs copies
        // AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_SESSION_TOKEN, AWS_REGION and DUCKDB_S3_ENDPOINT
        // from this process's environment into these settings. Left unset, an anonymous registration
        // would read with the server's own credentials, and an endpoint from the environment would send
        // it somewhere the address check never saw. So nothing is inherited: absent means empty.
        settings.Add($"set global s3_region = {Literal(remote.Region is { Length: > 0 } region ? region : "us-east-1")}");
        settings.Add($"set global s3_endpoint = {Literal(remote.Endpoint is { Length: > 0 } endpoint ? endpoint : "s3.amazonaws.com")}");
        settings.Add($"set global s3_url_style = {Literal(remote.UrlStyle is { Length: > 0 } style ? style : "vhost")}");
        settings.Add(remote.UseSsl ? "set global s3_use_ssl = true" : "set global s3_use_ssl = false");
        settings.Add($"set global s3_access_key_id = {Literal(remote.AccessKeyId ?? string.Empty)}");
        settings.Add($"set global s3_secret_access_key = {Literal(remote.AccessKeyId is { Length: > 0 } ? remote.SecretAccessKey ?? string.Empty : string.Empty)}");
        settings.Add("set global s3_session_token = ''");
        settings.Add("set global http_proxy = ''");

        // A listed key is never a pattern that walks the whole bucket (the review's P2).
        settings.Add("set global s3_allow_recursive_globbing = false");

        // <b>Bounded, because a slow server holds a request thread for as long as DuckDB waits</b>:
        // thirty seconds a try, in seconds as DuckDB counts them, and one retry rather than three.
        settings.Add("set global http_timeout = 30");
        settings.Add("set global http_retries = 1");

        // <b>A prefix is a directory and one file is a path</b>, and DuckDB keeps the two lists
        // apart: measured, a file URL in `allowed_directories` is refused as itself, and allowing its
        // directory instead would make every sibling readable.
        settings.Add(remote.IsPrefix
            ? $"set allowed_directories = [{Literal(remote.Location)}]"
            : $"set allowed_paths = [{Literal(remote.Location)}]");
        settings.Add("set enable_external_access = false");
        settings.Add("set lock_configuration = true");

        return settings;
    }

    private static (DuckDBConnection Root, string EngineVersion) OpenConfined(IReadOnlyList<string> settings)
    {
        DuckDBConnection root = new("DataSource=:memory:");
        root.Open();

        try
        {
            foreach (string setting in settings)
            {
                using DuckDBCommand command = root.CreateCommand();
                command.CommandText = setting;
                command.ExecuteNonQuery();
            }

            using DuckDBCommand version = root.CreateCommand();
            version.CommandText = "select version()";
            return (root, "DuckDB " + (string)version.ExecuteScalar()!);
        }
        catch (DuckDBException failure)
        {
            root.Dispose();

            // <b>Without the statement, which may hold a credential — and without the exception
            // either.</b> DuckDB's message names what failed on its first line and quotes the statement
            // on its next; a security review found the original kept as the inner exception, where any
            // logger that prints exceptions whole would print the secret.
            throw new InvalidOperationException(
                $"DuckDB could not be prepared for this source: {FirstLine(failure.Message)}");
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    /// <summary>Whether this instance reads a remote location rather than a folder.</summary>
    public bool IsRemote => _remote is not null;

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

        if (_attached is not null)
        {
            return [.. AttachedListing(refresh: true).Tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal)];
        }

        if (_remote is not null)
        {
            // <b>Four footers at a time</b>: each is two or three round trips at the bucket's latency and
            // they share nothing, so an eight-file prefix is read in the time of two. DuckDB's own model
            // is a connection per thread over one database, which is what `Find` already opens.
            IReadOnlyList<string> names = RemoteNames(refresh: true);
            GeoParquetTable[] read = new GeoParquetTable[names.Count];

            System.Threading.Tasks.Parallel.For(
                0, names.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 },
                i => read[i] = FindRemote(names[i], gated: false)
                    ?? Unreadable(names[i], PathOf(names[i]), "The file vanished while it was being read."));

            return read;
        }

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

        if (_attached is not null)
        {
            return AttachedListing(refresh: false).Tables.GetValueOrDefault(name);
        }

        if (_remote is not null)
        {
            return FindRemote(name);
        }

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
            table = Read(name, path, LocalVersion(file));
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

        if (_attached is not null)
        {
            return _attached.Location + " → main." + name;
        }

        if (_remote is not null)
        {
            // The listing knows the file a sanitised name stands for; a name it does not hold is
            // answered with the spelling it would have had, for the sentence that says it is missing.
            return RemoteListing(refresh: false).Files.TryGetValue(name, out string? url)
                ? url
                : _remote.IsPrefix ? _remote.Location + name + ".parquet" : _remote.Location;
        }

        return Folder + "/" + name + ".parquet";
    }

    /// <summary>
    /// The table name a remote file is published under: its name without <c>.parquet</c>, made into
    /// an identifier.
    /// </summary>
    /// <param name="stem">The file name without <c>.parquet</c>.</param>
    /// <returns>Letters, digits and underscores, at most 63, not starting with a digit.</returns>
    /// <remarks>
    /// <b>Sanitised for a remote file, and refused for a local one, because only one of them can be
    /// renamed by the person registering it.</b> A folder on this server is the operator's, and
    /// ADR-066 asks them to rename a file. A bucket usually is not: Overture's files are
    /// <c>part-00000-3d6dbc8d-…-c000.zstd.parquet</c>, and the first run of ADR-067's end-to-end check
    /// found every one of them silently dropped from the listing. So the name is made plain and the
    /// listing remembers which file it stands for.
    /// </remarks>
    public static string TableNameOf(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);

        StringBuilder name = new(Math.Min(stem.Length, 63) + 1);

        foreach (char c in stem)
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (name.Length == 0 || char.IsAsciiDigit(name[0]))
        {
            name.Insert(0, '_');
        }

        return name.Length > 63 ? name.ToString(0, 63) : name.ToString();
    }

    /// <summary>The table name a remote file is published under: its file name without <c>.parquet</c>.</summary>
    /// <param name="url">The file's URL.</param>
    /// <returns>The name, or null when the URL does not end in a <c>.parquet</c> file.</returns>
    public static string? NameOfUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        int query = url.IndexOfAny(['?', '#']);
        string path = query < 0 ? url : url[..query];
        int slash = path.LastIndexOf('/');
        string file = slash < 0 ? path : path[(slash + 1)..];

        return file.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase) && file.Length > ".parquet".Length
            ? file[..^".parquet".Length]
            : null;
    }

    private GeoParquetTable? FindRemote(string name, bool gated = true)
    {
        RemoteFiles listing = RemoteListing(refresh: false);

        if (listing.Problems.TryGetValue(name, out string? problem))
        {
            return Unreadable(name, _remote!.Location, problem);
        }

        if (!listing.Files.ContainsKey(name))
        {
            return null;
        }

        long lifetime = (long)_remoteLifetime.TotalMilliseconds;

        if (_remoteTables.TryGetValue(name, out var cached)
            && Environment.TickCount64 - cached.ReadAt < lifetime)
        {
            return cached.Table;
        }

        // <b>One refresh at a time, and the others keep what they had</b> — the review's M1. When the
        // lifetime ends under load every request would otherwise read the footer again at the bucket's
        // latency, all at once. A request that finds a refresh already running is answered from the
        // metadata that was current a minute ago; only one with nothing cached waits for it.
        //
        // <b>Not for the listing, which reads every file at once on purpose.</b> The first version gated
        // it too, and one gate for the whole location put the four parallel footers back in single
        // file: Overture's eight took 23.9 s instead of 6.3. A listing is a probe an administrator
        // asked for, not a stampede of map requests.
        if (!gated)
        {
            return ReadRemote(name);
        }

        bool entered = Monitor.TryEnter(_remoteRefresh);

        if (!entered)
        {
            if (_remoteTables.TryGetValue(name, out var stale))
            {
                return stale.Table;
            }

            Monitor.Enter(_remoteRefresh);
        }

        try
        {
            if (_remoteTables.TryGetValue(name, out var fresh)
                && Environment.TickCount64 - fresh.ReadAt < lifetime)
            {
                return fresh.Table;
            }

            return ReadRemote(name);
        }
        finally
        {
            Monitor.Exit(_remoteRefresh);
        }
    }

    private GeoParquetTable ReadRemote(string name)
    {
        string path = PathOf(name);
        GeoParquetTable table;

        try
        {
            table = Read(name, path, version: null);
        }
        catch (Exception failure) when (failure is DuckDBException or WkbFormatException
            or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            table = Unreadable(name, path, $"The file could not be read: {FirstLine(failure.Message)}");
        }

        _remoteTables[name] = (Environment.TickCount64, table);
        return table;
    }

    private IReadOnlyList<string> RemoteNames(bool refresh) => RemoteListing(refresh).Names;

    private sealed record AttachedTables(IReadOnlyDictionary<string, GeoParquetTable> Tables, long ReadAt, string? FileVersion);

    /// <summary>
    /// The tables in an attached database's <c>main</c> schema, as layers would be served from them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read again when a file changes, and at most once per metadata lifetime for MotherDuck</b>,
    /// whose tables have no modification time this server can ask for cheaply. A MotherDuck table's
    /// version is its row count and columns, which is what a rewrite nearly always changes.
    /// </para>
    /// <para>
    /// <b>One schema, <c>main</c></b>, because a layer names a schema and a table and the catalogue
    /// keeps them apart, and a first version that listed every schema would have to answer what two
    /// <c>places</c> tables in two schemas are called. Tables elsewhere are not listed (ADR-067 §5.3).
    /// </para>
    /// <para>
    /// <b>A reference from the column's type when it has one, else from the registration</b>, and a
    /// table with neither is listed with the reason. The two disagreeing is refused rather than
    /// resolved: the data says one thing and the registrant another, and only a person knows which.
    /// </para>
    /// </remarks>
    private AttachedTables AttachedListing(bool refresh)
    {
        string? fileVersion = _attached!.IsMotherDuck ? null : LocalVersion(new FileInfo(_attached.File!));
        long now = Environment.TickCount64;

        bool Current(AttachedTables listed) => _attached.IsMotherDuck
            ? now - listed.ReadAt < (long)_remoteLifetime.TotalMilliseconds
            : string.Equals(listed.FileVersion, fileVersion, StringComparison.Ordinal);

        if (!refresh && Volatile.Read(ref _attachedListing) is { } listed && Current(listed))
        {
            return listed;
        }

        // <b>One refresh at a time, and everybody else keeps the listing they had</b> — a security review's
        // finding: when MotherDuck's minute ended, every concurrent request relisted every table at once. A
        // request that arrives while a refresh runs is answered from the previous listing; only the first
        // request of all, with nothing to fall back on, waits.
        if (!Monitor.TryEnter(_attachedRefresh))
        {
            if (!refresh && Volatile.Read(ref _attachedListing) is { } stale)
            {
                return stale;
            }

            Monitor.Enter(_attachedRefresh);
        }

        try
        {
            if (!refresh && Volatile.Read(ref _attachedListing) is { } fresh && Current(fresh))
            {
                return fresh;
            }

            return RefreshAttached(fileVersion, now);
        }
        finally
        {
            Monitor.Exit(_attachedRefresh);
        }
    }

    private AttachedTables RefreshAttached(string? fileVersion, long now)
    {
        using DuckDBConnection connection = Open();

        // <b>MotherDuck's catalogue is asked to catch up first.</b> Found by the cities benchmark: a source opened
        // hours earlier did not list two tables created since, while a fresh connection did — and the same idle
        // source listed them on the next run, minutes later, before this line existed. MotherDuck stops refreshing
        // an attached catalogue in the background after five minutes idle
        // (`motherduck_background_catalog_refresh_inactivity_timeout`), so the first listing after a quiet spell
        // can be one refresh behind. `refresh databases` costs about 12 ms and asks for that refresh up front;
        // that it closes the gap is not measured, and a failure leaves the catalogue as it was.
        if (_attached!.IsMotherDuck)
        {
            try
            {
                using DuckDBCommand refresh = connection.CreateCommand();
                refresh.CommandText = "refresh databases";
                using CancellationTokenSource deadline = Deadline(refresh);
                refresh.ExecuteNonQuery();
            }
            catch (DuckDBException)
            {
            }
        }

        HashSet<string> baseTables = new(StringComparer.Ordinal);

        using (DuckDBCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "select table_name from duckdb_tables() where database_name = 'src' and schema_name = 'main' "
                + $"order by table_name limit {MostAttachedTables.ToString(CultureInfo.InvariantCulture)}";

            using CancellationTokenSource deadline = Deadline(command);
            using DuckDBDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                baseTables.Add(reader.GetString(0));
            }
        }

        Dictionary<string, List<GeoParquetColumn>> columnsOf = new(StringComparer.Ordinal);

        using (DuckDBCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "select table_name, column_name, data_type from information_schema.columns "
                + "where table_catalog = 'src' and table_schema = 'main' order by table_name, ordinal_position";

            using CancellationTokenSource deadline = Deadline(command);
            using DuckDBDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                string table = reader.GetString(0);

                if (!baseTables.Contains(table))
                {
                    continue;
                }

                if (!columnsOf.TryGetValue(table, out List<GeoParquetColumn>? columns))
                {
                    columnsOf[table] = columns = [];
                }

                columns.Add(new GeoParquetColumn(reader.GetString(1), reader.GetString(2)));
            }
        }

        Dictionary<string, GeoParquetTable> tables = new(StringComparer.Ordinal);

        foreach ((string name, List<GeoParquetColumn> all) in columnsOf.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            string location = _attached!.Location + " → main." + name;

            if (!PlainName().IsMatch(name) || name.Length > 63)
            {
                tables[name] = Unreadable(name, location,
                    "The table name is not a plain identifier of at most 63 letters, digits and underscores, "
                    + "and a layer's table name must be one.");
                continue;
            }

            try
            {
                tables[name] = ReadAttached(connection, name, location, all, fileVersion);
                _attachedDetails[name] = tables[name];
            }
            catch (Exception failure) when (failure is DuckDBException or WkbFormatException
                or ArgumentException or InvalidOperationException or FormatException or OverflowException)
            {
                tables[name] = Unreadable(name, location, $"The table could not be read: {FirstLine(failure.Message)}");
            }
        }

        // Tables gone from the database leave the details cache with it.
        foreach (string gone in _attachedDetails.Keys.Where(k => !tables.ContainsKey(k)))
        {
            _attachedDetails.TryRemove(gone, out _);
        }

        AttachedTables result = new(tables, now, fileVersion);
        Volatile.Write(ref _attachedListing, result);
        return result;
    }

    /// <summary>A cancellation that interrupts a command when the attached statement deadline passes.</summary>
    private static CancellationTokenSource Deadline(DuckDBCommand command)
    {
        CancellationTokenSource deadline = new(AttachedStatementDeadline);
        deadline.Token.Register(command.Cancel);
        return deadline;
    }

    private GeoParquetTable ReadAttached(
        DuckDBConnection connection, string name, string location, List<GeoParquetColumn> all, string? fileVersion)
    {
        string relation = "src.main." + Quote(name);
        List<GeoParquetColumn> geometries = [.. all.Where(c => c.Type.StartsWith("GEOMETRY", StringComparison.Ordinal))];
        List<GeoParquetColumn> columns = [.. all.Where(c => !c.Type.StartsWith("GEOMETRY", StringComparison.Ordinal))];

        long rows;

        using (DuckDBCommand count = connection.CreateCommand())
        {
            count.CommandText = $"select count(*) from {relation}";
            using CancellationTokenSource deadline = Deadline(count);
            rows = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        string version = fileVersion is not null
            ? Hash($"{fileVersion}:{name}")
            : Hash($"{rows}:{string.Join(",", all.Select(c => c.Name + " " + c.Type))}");

        // <b>Measured once per version, not once per listing</b> — the review's point that relisting MotherDuck
        // every minute ran a count-distinct over every table every minute, billed to the registrant.
        if (_attachedDetails.TryGetValue(name, out GeoParquetTable? known)
            && string.Equals(known.Version, version, StringComparison.Ordinal))
        {
            return known;
        }

        GeoParquetTable Refused(string problem, string column = "") =>
            new(name, location, rows, version, new GeoParquetMetadata(column, null, null, null, null, problem),
                columns, null, [], null, problem, relation);

        if (geometries.Count == 0)
        {
            return Refused(all.Any(c => string.Equals(c.Type, "BLOB", StringComparison.Ordinal))
                ? "The table has no GEOMETRY column. A BLOB of well-known binary is not read as one: cast it with st_geomfromwkb into a GEOMETRY column."
                : "The table has no GEOMETRY column.");
        }

        GeoParquetColumn geometry = geometries[0];

        if (!PlainName().IsMatch(geometry.Name))
        {
            return Refused($"The geometry column '{geometry.Name}' is not a plain identifier, and a layer's geometry column must be one.", geometry.Name);
        }

        int? typed = SridOfType(geometry.Type, out string? unreadable);

        if (unreadable is not null)
        {
            return Refused(unreadable, geometry.Name);
        }

        int? srid = typed ?? _attached!.DeclaredSrid;

        if (typed is not null && _attached!.DeclaredSrid is { } declared && declared != typed)
        {
            return Refused(
                $"The column '{geometry.Name}' says its reference is EPSG:{typed}, and the registration declares "
                + $"EPSG:{declared}. The data and the declaration disagree, and a person has to say which is right.",
                geometry.Name);
        }

        if (srid is null)
        {
            return Refused(
                $"The column '{geometry.Name}' carries no reference — DuckDB does not keep one in a database file — "
                + "and the registration declares none. Register the source with the EPSG code its geometry is in: "
                + "4326 for longitude and latitude, 3857 for web-Mercator metres, or the code of its national grid.",
                geometry.Name);
        }

        GeometryKind? kind = SampleKind(connection, relation, geometry.Name);
        (IReadOnlyList<string> candidates, string? preferred, IReadOnlyList<string> wide) = Identities(connection, relation, columns);

        // <b>The row number only where it is a row number</b> — two findings of a security review. A column the
        // table itself calls `rowid` hides DuckDB's pseudo-column, so the alias would name somebody's data; and on
        // MotherDuck a row's `rowid` is not promised to survive deletes and compaction. Either way the layer needs
        // an integer column of its own that is unique.
        if (_attached!.IsMotherDuck || all.Any(c => string.Equals(c.Name, "rowid", StringComparison.OrdinalIgnoreCase)))
        {
            candidates = [.. candidates.Where(c => !string.Equals(c, RowNumberColumn, StringComparison.Ordinal))];
            preferred = candidates.Count > 0 ? candidates[0] : null;

            if (candidates.Count == 0)
            {
                return Refused(
                    "The table has no integer column that is unique and never null, and a layer here needs one as its "
                    + (_attached.IsMotherDuck
                        ? "identity: MotherDuck does not promise that a row keeps its position."
                        : "identity: the table has a column named rowid, which hides DuckDB's own row number."),
                    geometry.Name);
            }
        }

        return new GeoParquetTable(
            name, location, rows, version,
            new GeoParquetMetadata(geometry.Name, srid, kind, null, null, null),
            columns, kind, candidates, preferred, null, relation, SridDeclared: typed is null, WideIdentityCandidates: wide);
    }

    /// <summary>The EPSG code a <c>GEOMETRY('…')</c> type names, or null when it names none.</summary>
    /// <param name="type">DuckDB's type text.</param>
    /// <param name="unreadable">Why a reference that is there cannot be used, or null.</param>
    /// <returns>The code, or null.</returns>
    internal static int? SridOfType(string type, out string? unreadable)
    {
        unreadable = null;

        if (!type.StartsWith("GEOMETRY('", StringComparison.Ordinal) || !type.EndsWith("')", StringComparison.Ordinal))
        {
            return null;
        }

        string crs = type["GEOMETRY('".Length..^2];

        if (crs is "OGC:CRS84" or "EPSG:4326")
        {
            return 4326;
        }

        if (crs.StartsWith("EPSG:", StringComparison.Ordinal)
            && int.TryParse(crs.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int code) && code > 0)
        {
            return code;
        }

        unreadable = $"The geometry column's reference is '{crs}', which is not an EPSG code this server can serve.";
        return null;
    }

    /// <summary>The bounding box of an attached table's geometry, computed once per version.</summary>
    /// <remarks>
    /// <b>Read here because core DuckDB cannot say it</b> — <c>st_extent_agg</c> and <c>st_xmin</c> are the
    /// spatial extension's — and a table, unlike a GeoParquet file, carries no bbox of its own. So every
    /// geometry is read once as WKB and its envelope folded in, and the answer is kept until the table's
    /// version changes. That is a full read of one column: seconds for a million rows on a local file,
    /// longer over MotherDuck, once.
    /// </remarks>
    public Envelope? AttachedExtent(GeoParquetTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table.Relation is null || table.Problem is not null)
        {
            return null;
        }

        if (_attachedExtents.TryGetValue(table.Name, out var cached)
            && string.Equals(cached.Version, table.Version, StringComparison.Ordinal))
        {
            return cached.Extent;
        }

        // <b>One scan at a time for the whole database, under the deadline, and a failure is an unknown extent</b>
        // — the review found concurrent first describes each scanning the column, nothing bounding it, and a
        // malformed geometry turning a layer's description into a 500.
        lock (_attachedExtentGate)
        {
            if (_attachedExtents.TryGetValue(table.Name, out cached)
                && string.Equals(cached.Version, table.Version, StringComparison.Ordinal))
            {
                return cached.Extent;
            }

            Envelope? answer;

            try
            {
                using DuckDBConnection connection = Open();
                using DuckDBCommand command = connection.CreateCommand();
                command.CommandText =
                    $"select st_aswkb({Quote(table.Geometry.Column)}) from {table.Relation} where {Quote(table.Geometry.Column)} is not null";

                using CancellationTokenSource deadline = Deadline(command);
                Envelope extent = Envelope.Empty;

                using (DuckDBDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        using Stream stream = reader.GetStream(0);
                        byte[] wkb = new byte[stream.Length];
                        stream.ReadExactly(wkb);
                        extent = extent.Union(WkbReader.Read(wkb).Envelope);
                    }
                }

                answer = extent.IsEmpty ? null : extent;
            }
            catch (Exception failure) when (failure is DuckDBException or WkbFormatException or ArgumentException
                or InvalidOperationException or OverflowException)
            {
                answer = null;
            }

            _attachedExtents[table.Name] = (table.Version, answer);
            return answer;
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>The most files a remote prefix lists; past it the listing says it stopped.</summary>
    /// <remarks>
    /// A security review's M2: a prefix of a hundred thousand objects made the probe read a hundred
    /// thousand footers, several round trips each, on threads the request had already let go of.
    /// </remarks>
    public const int MostRemoteFiles = 1000;

    /// <summary>Whether the last listing of this remote prefix stopped at <see cref="MostRemoteFiles"/>.</summary>
    public bool RemoteListingTruncated => _remoteListing?.Truncated ?? false;

    /// <summary>
    /// The files at a remote location and the table name each is published under, listed at most
    /// once per metadata lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two files whose names sanitise alike are refused, both of them, rather than numbered.</b>
    /// Numbering them in listing order was the first version, and a security review showed what it
    /// lets a bucket's writer do: add <c>a+b.parquet</c> beside a published <c>a-b.parquet</c>, and
    /// <c>+</c> sorts first, so the new file takes <c>a_b</c> and every layer on it serves the newcomer.
    /// </para>
    /// <para>
    /// <b>A key with a pattern character in it is listed and refused</b>: a store returns whatever
    /// names it likes, and DuckDB would read <c>*</c> or <c>?</c> in one as a pattern rather than a file.
    /// </para>
    /// <para>
    /// <b>Swapped whole</b>, so a reader on another thread sees the old listing or the new one and
    /// never half of each (the review's L5).
    /// </para>
    /// </remarks>
    private RemoteFiles RemoteListing(bool refresh)
    {
        long now = Environment.TickCount64;

        if (!refresh
            && Volatile.Read(ref _remoteListing) is { } listed
            && now - listed.ReadAt < (long)_remoteLifetime.TotalMilliseconds)
        {
            return listed;
        }

        List<string> urls = [];
        bool truncated = false;

        if (_remote!.IsPrefix)
        {
            using DuckDBConnection connection = Open();
            using DuckDBCommand command = connection.CreateCommand();

            // Directly under the prefix: `*` does not cross a `/`, so a prefix is one level, as a folder is.
            command.CommandText =
                $"select file from glob({Literal(_remote.Location + "*.parquet")}) order by file "
                + $"limit {(MostRemoteFiles + 1).ToString(CultureInfo.InvariantCulture)}";

            using DuckDBDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (urls.Count == MostRemoteFiles)
                {
                    truncated = true;
                    break;
                }

                urls.Add(reader.GetString(0));
            }
        }
        else
        {
            urls.Add(_remote.Location);
        }

        (IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> problems, IReadOnlyList<string> names) =
            MapRemoteNames(urls, _remote.Location);

        RemoteFiles result = new(files, problems, names, now, truncated);
        Volatile.Write(ref _remoteListing, result);
        return result;
    }

    /// <summary>The table name each listed file is published under, and the files that cannot be.</summary>
    /// <param name="urls">The listed URLs, in listing order.</param>
    /// <param name="location">The location they were listed under.</param>
    /// <returns>Name to URL for publishable files, name to reason for the rest, and every name in order.</returns>
    internal static (IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, string> Problems, IReadOnlyList<string> Names)
        MapRemoteNames(IReadOnlyList<string> urls, string location)
    {
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(location);

        Dictionary<string, string> files = new(StringComparer.Ordinal);
        Dictionary<string, string> problems = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> byName = new(StringComparer.Ordinal);
        List<string> names = [];

        foreach (string url in urls)
        {
            if (NameOfUrl(url) is not { } stem)
            {
                continue;
            }

            string name = TableNameOf(stem);

            if (!byName.TryGetValue(name, out List<string>? same))
            {
                byName[name] = same = [];
                names.Add(name);
            }

            same.Add(url);
        }

        foreach (string name in names)
        {
            List<string> same = byName[name];

            if (same.Count > 1)
            {
                problems[name] = $"{same.Count} files here would be published as '{name}' — "
                    + string.Join(", ", same.Select(u => "'" + u[(u.LastIndexOf('/') + 1)..] + "'"))
                    + " — and a layer must name one file. Rename all but one of them.";
            }
            else if (same[0].AsSpan(Math.Min(location.Length, same[0].Length)).IndexOfAny(PatternCharacters) >= 0)
            {
                problems[name] = "The file's name holds a character DuckDB reads as a pattern (* ? [ ] { }), "
                    + "so it cannot be read as one file. Rename it.";
            }
            else
            {
                files[name] = same[0];
            }
        }

        return (files, problems, names);
    }

    private static readonly System.Buffers.SearchValues<char> PatternCharacters =
        System.Buffers.SearchValues.Create("*?[]{}");

    private sealed record RemoteFiles(
        IReadOnlyDictionary<string, string> Files,
        IReadOnlyDictionary<string, string> Problems,
        IReadOnlyList<string> Names,
        long ReadAt,
        bool Truncated);

    // <b>Hashed, so an entity tag does not tell a stranger the file's size and when it was
    // written</b> — a security review's note. It still changes whenever either does.
    private static string LocalVersion(FileInfo file) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Create(
                CultureInfo.InvariantCulture, $"{file.Length}:{file.LastWriteTimeUtc.Ticks}"))))[..16]
            .ToLowerInvariant();

    /// <summary>The table expression for a file, with the row-number column when it is needed.</summary>
    /// <remarks>
    /// <b>A table in an attached database answers to the same name for its row number</b>: its
    /// <c>rowid</c>, aliased, so every statement the feature source writes reads a table exactly as it
    /// reads a file. Measured on MotherDuck and on a local file: the alias filters and counts.
    /// </remarks>
    internal static string TableExpression(GeoParquetTable table, bool rowNumbers) =>
        table.Relation is { } relation
            ? rowNumbers ? $"(select *, rowid as {RowNumberColumn} from {relation})" : relation
            : rowNumbers
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

    private GeoParquetTable Read(string name, string path, string? version)
    {
        using DuckDBConnection connection = Open();

        string? geo = Scalar(connection,
            $"select decode(value) from parquet_kv_metadata({Literal(path)}) where decode(key) = 'geo'")
            as string;

        long rows;

        using (DuckDBCommand footer = connection.CreateCommand())
        {
            footer.CommandText =
                $"select num_rows, num_row_groups, created_by from parquet_file_metadata({Literal(path)})";

            using DuckDBDataReader reader = footer.ExecuteReader();

            if (!reader.Read())
            {
                throw new InvalidOperationException("The file has no Parquet footer.");
            }

            rows = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);

            // <b>A remote file's version is its footer — ADR-067 §5.2.</b> There is no modification
            // time to read cheaply over https or S3, and a rewritten file almost always changes its
            // row count, its row groups or its writer; the `geo` key, which carries the bbox, covers
            // most of the rest. A rewrite that changes none of those keeps its old version, and
            // ADR-067 says so.
            version ??= Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
                    $"{rows}:{reader.GetValue(1)}:{(reader.IsDBNull(2) ? string.Empty : reader.GetString(2))}:{geo}"))))[..16]
                .ToLowerInvariant();
        }

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
            kind = SampleKind(connection, $"read_parquet({Literal(path)})", metadata.Column);
        }

        // <b>A remote file offers its row number and nothing it would have to scan to prove.</b>
        // Measuring a column unique reads the whole column, and over S3 that is the probe's dominant
        // cost: on Overture's division areas from the VPS, 3.7 s of a file's 6.6 s, for eight files, and
        // the first end-to-end run's test request was abandoned by its client at a minute (ADR-067).
        // A remote file is as immutable as a local one — its version is its footer — so the row number
        // is as stable, and a publisher who knows a column is unique says so on a local copy.
        (IReadOnlyList<string> candidates, string? preferred, IReadOnlyList<string> wide) = problem is not null
            ? ([], null, [])
            : _remote is not null
                ? ([RowNumberColumn], RowNumberColumn, [])
                : Identities(connection, $"read_parquet({Literal(path)})", columns);

        return new GeoParquetTable(
            name, path, rows, version, metadata, columns, kind, candidates, preferred, problem, WideIdentityCandidates: wide);
    }

    private static GeometryKind? SampleKind(DuckDBConnection connection, string relation, string column)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText =
            $"select st_aswkb({Quote(column)}) from {relation} "
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
    private static (IReadOnlyList<string> Candidates, string? Preferred, IReadOnlyList<string> Wide) Identities(
        DuckDBConnection connection, string relation, IReadOnlyList<GeoParquetColumn> columns)
    {
        List<string> integers = columns
            .Where(c => IsInteger(c.Type) && PlainName().IsMatch(c.Name))
            .Select(c => c.Name)
            .OrderBy(c => PreferenceOf(c))
            .Take(MostIdentityColumnsMeasured)
            .ToList();

        List<string> unique = [];
        List<string> wide = [];
        long rows = 0;

        if (integers.Count > 0)
        {
            StringBuilder sql = new("select count(*)");

            // <b>And each one's smallest and largest, in the same pass</b> — V-77: an object id past 32 bits is one
            // an ArcGIS 10.x client cannot hold, and `osm_id` reaches 14 billion.
            foreach (string column in integers)
            {
                sql.Append(", count(distinct ").Append(Quote(column)).Append("), count(")
                   .Append(Quote(column)).Append("), min(").Append(Quote(column)).Append(")::hugeint, max(")
                   .Append(Quote(column)).Append(")::hugeint");
            }

            sql.Append(" from ").Append(relation);

            using DuckDBCommand command = connection.CreateCommand();
            command.CommandText = sql.ToString();

            using DuckDBDataReader reader = command.ExecuteReader();

            if (reader.Read())
            {
                long total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                rows = total;

                for (int i = 0; i < integers.Count; i++)
                {
                    long distinct = Convert.ToInt64(reader.GetValue(1 + (4 * i)), CultureInfo.InvariantCulture);
                    long present = Convert.ToInt64(reader.GetValue(2 + (4 * i)), CultureInfo.InvariantCulture);

                    if (distinct == total && present == total)
                    {
                        unique.Add(integers[i]);

                        if (total > 0
                            && (!FitsIn32Bits(reader.GetValue(3 + (4 * i))) || !FitsIn32Bits(reader.GetValue(4 + (4 * i)))))
                        {
                            wide.Add(integers[i]);
                        }
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

        return (unique, preferred, wide);
    }

    /// <summary>Whether a measured value fits in a signed 32-bit integer, as an ArcGIS 10.x object id must.</summary>
    private static bool FitsIn32Bits(object value) =>
        value is DBNull
        || (System.Numerics.BigInteger.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out System.Numerics.BigInteger v)
            && v >= int.MinValue && v <= int.MaxValue);

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
