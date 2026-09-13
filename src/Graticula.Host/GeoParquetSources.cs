using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Providers.DuckDb;

namespace Graticula.Host;

/// <summary>
/// The GeoParquet folders this server may read, and the one DuckDB each is read by — ADR-066.
/// </summary>
/// <remarks>
/// <para>
/// <b>A root, and nothing outside it.</b> DuckDB reads a registered folder inside this process, so
/// where an administrator may point it is the deployment's decision and not the API's:
/// <c>Graticula:GeoParquetRoot</c> names the directory, a folder must be inside it to be
/// registered, and it must still be inside it to be read. Unset, the feature is off and says how to
/// turn it on.
/// </para>
/// <para>
/// <b>One DuckDB per folder, opened on first use and kept.</b> Opening one costs a few milliseconds
/// and a few megabytes; a folder's metadata cache and DuckDB's own file cache are what make the
/// second query cheaper than the first, and both would be thrown away by opening per request.
/// </para>
/// </remarks>
internal sealed partial class GeoParquetSources : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<GeoParquetFolder>> _folders = new(PathComparer);

    /// <summary>How two folder paths are compared: as the file system compares them.</summary>
    /// <remarks>
    /// On Windows, <c>Sub</c> and <c>sub</c> are one folder, and a case-sensitive key opened a second
    /// DuckDB for each spelling — a security review's finding.
    /// </remarks>
    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Characters DuckDB reads as a file pattern in a path.</summary>
    private static readonly System.Buffers.SearchValues<char> PatternCharacters =
        System.Buffers.SearchValues.Create("*?[]{}");
    private readonly GeoParquetOptions _options;
    private readonly bool _allowPrivate;
    private readonly string? _motherDuckDirectory;
    private readonly object _motherDuckDownload = new();
    private Task<string>? _motherDuckInstall;

    /// <summary>The largest MotherDuck extension this server accepts — measured 18–28 MB; anything far larger is not it.</summary>
    private const long MostExtensionBytes = 256L * 1024 * 1024;
    private bool _disposed;

    public GeoParquetSources(
        string? root,
        string memoryLimit = "1GB",
        int? threads = null,
        string? extensionDirectory = null,
        bool allowPrivate = false,
        string? motherDuckDirectory = null)
    {
        Root = string.IsNullOrWhiteSpace(root) ? null : Normalise(Path.GetFullPath(root));
        _options = new GeoParquetOptions
        {
            MemoryLimit = memoryLimit,
            Threads = threads,
            ExtensionDirectory = string.IsNullOrWhiteSpace(extensionDirectory) ? null : Path.GetFullPath(extensionDirectory),
        };
        _allowPrivate = allowPrivate;
        _motherDuckDirectory = string.IsNullOrWhiteSpace(motherDuckDirectory) ? null : Path.GetFullPath(motherDuckDirectory);

    }

    /// <summary>Starts installing MotherDuck's extension in the background, when MotherDuck is on.</summary>
    /// <remarks>Called once at startup, so the first MotherDuck request finds it ready rather than starting it.</remarks>
    public void BeginMotherDuckInstall()
    {
        if (_motherDuckDirectory is null)
        {
            return;
        }

        lock (_motherDuckDownload)
        {
            _motherDuckInstall ??= Task.Run(InstallMotherDuck);
        }
    }

    /// <summary>Whether MotherDuck may be registered: the deployment switched it on — ADR-067 §5.4.</summary>
    public bool MotherDuckEnabled => _motherDuckDirectory is not null;

    /// <summary>The refusal when MotherDuck is off.</summary>
    public const string MotherDuckOff =
        "MotherDuck is not enabled on this server. Set Graticula:MotherDuck to true and restart: the server then "
        + "downloads MotherDuck's DuckDB extension from DuckDB's repository into its state directory on first use. "
        + "It is not shipped with the image, because MotherDuck's terms let a customer download and install it and "
        + "say nothing about anybody else redistributing it (ADR-067 §3).";

    /// <summary>The refusal when DuckDB database files are off, which is when folders are.</summary>
    public const string DuckDbFilesOff =
        "DuckDB database files are not enabled on this server. Set Graticula:GeoParquetRoot (or the environment "
        + "variable Graticula__GeoParquetRoot) to the directory they may be registered under and restart.";

    /// <summary>Turns a DuckDB database file request into the locator a registration stores — ADR-067 §5.3.</summary>
    /// <param name="requested">The file, relative to the root or absolute inside it.</param>
    /// <param name="srid">The EPSG code its geometry is in, for columns whose type carries none.</param>
    /// <param name="locator">The locator to seal.</param>
    /// <param name="why">Why it was refused.</param>
    /// <returns>Whether it may be registered.</returns>
    public bool TryLocateDuckDb(string? requested, int? srid, out string? locator, out string? why)
    {
        locator = null;

        if (Root is null)
        {
            why = DuckDbFilesOff;
            return false;
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            why = $"A file is required: a .duckdb file inside {Root}, by its path relative to it or its whole path.";
            return false;
        }

        if (srid is <= 0)
        {
            why = "`srid` is an EPSG code, a positive number.";
            return false;
        }

        string file = Normalise(Path.GetFullPath(Path.IsPathRooted(requested)
            ? requested.Trim()
            : Path.Combine(Root, requested.Trim())));

        if (DuckDbFileRefusal(file) is { } refused)
        {
            why = refused;
            return false;
        }

        why = null;
        locator = GeoParquetLocator.ForAttached(false, JsonSerializer.Serialize(new StoredAttached(file, null, null, srid), AttachedJson));
        return true;
    }

    /// <summary>Turns a MotherDuck request into the locator a registration stores — ADR-067 §5.4.</summary>
    /// <param name="database">The MotherDuck database name.</param>
    /// <param name="token">The access token.</param>
    /// <param name="srid">The EPSG code for geometry columns whose type carries none.</param>
    /// <param name="locator">The locator to seal.</param>
    /// <param name="why">Why it was refused.</param>
    /// <returns>Whether it may be registered.</returns>
    public bool TryLocateMotherDuck(string? database, string? token, int? srid, out string? locator, out string? why)
    {
        locator = null;

        if (!MotherDuckEnabled)
        {
            why = MotherDuckOff;
            return false;
        }

        if (string.IsNullOrWhiteSpace(database) || !MotherDuckName().IsMatch(database))
        {
            why = "`database` is a MotherDuck database name: letters, digits and underscores, starting with a letter or an underscore.";
            return false;
        }

        // <b>Refused before anything is asked</b>: with no token MotherDuck's extension opens a browser and waits.
        if (string.IsNullOrEmpty(token) || token.Length > 8192 || !JwtShaped().IsMatch(token))
        {
            why = "`token` is a MotherDuck access token — three dot-separated parts, from Settings → Access Tokens in MotherDuck.";
            return false;
        }

        if (srid is <= 0)
        {
            why = "`srid` is an EPSG code, a positive number.";
            return false;
        }

        why = null;
        locator = GeoParquetLocator.ForAttached(true, JsonSerializer.Serialize(new StoredAttached(null, database, token, srid), AttachedJson));
        return true;
    }

    /// <summary>The database a stored attached locator describes, token included.</summary>
    /// <param name="locator">A DuckDB file or MotherDuck locator.</param>
    /// <returns>The database.</returns>
    public static AttachedDuckDb ParseAttached(string locator)
    {
        StoredAttached stored = JsonSerializer.Deserialize<StoredAttached>(GeoParquetLocator.AttachedOf(locator), AttachedJson)
            ?? throw new InvalidOperationException("The stored DuckDB database is empty.");

        return new AttachedDuckDb(stored.File, stored.Database, stored.Token, stored.Srid);
    }

    private static readonly JsonSerializerOptions AttachedJson = new(JsonSerializerDefaults.Web);

    /// <summary>What is sealed for a DuckDB file or a MotherDuck database. Never shown.</summary>
    private sealed record StoredAttached(string? File, string? Database, string? Token, int? Srid);

    /// <summary>Why a DuckDB database file may not be read, or null — the folder's rules, and the file's own.</summary>
    private string? DuckDbFileRefusal(string file)
    {
        if (!Inside(file))
        {
            return $"'{file}' is outside {Root}, the only directory this server may read DuckDB files from.";
        }

        if (!file.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase))
        {
            return "A DuckDB database file is a .duckdb file.";
        }

        if (file.AsSpan().IndexOfAny(PatternCharacters) >= 0 || file.Contains('\'', StringComparison.Ordinal))
        {
            return $"'{file}' contains a character DuckDB reads as a pattern or a quote. Rename the file.";
        }

        FileInfo info = new(file);

        if (!info.Exists)
        {
            return $"There is no file at '{file}'.";
        }

        if (info.LinkTarget is not null)
        {
            return $"'{file}' is a symbolic link, and a DuckDB file must be a file inside {Root}.";
        }

        // The write-ahead log is read at attach and allowed by name, so a link there reads somewhere else (a security review).
        if (new FileInfo(file + ".wal") is { Exists: true, LinkTarget: not null })
        {
            return $"'{file}.wal' is a symbolic link, and a DuckDB file's log must be a file beside it.";
        }

        return Path.GetDirectoryName(file) is { } folder ? Unsafe(Normalise(folder)) : null;
    }

    /// <summary>MotherDuck's extension file, or why it is not ready — never waiting for a download.</summary>
    /// <remarks>
    /// <para>
    /// <b>Downloaded, not shipped</b> — ADR-067 §3: MotherDuck's terms grant a customer the right to download and
    /// install it and say nothing about redistribution. An operator who switched MotherDuck on is that customer.
    /// </para>
    /// <para>
    /// <b>In the background, from the moment the server starts, and never on a request thread</b> — a security
    /// review found the first version downloading under a lock inside a request, where a stalled response body
    /// held that request and every one behind it with no limit. A request that arrives before the extension is
    /// ready is told so at once; one that arrives after a failure starts one new attempt and is told that too.
    /// </para>
    /// </remarks>
    private string MotherDuckExtension()
    {
        Task<string> install;

        lock (_motherDuckDownload)
        {
            install = _motherDuckInstall ??= Task.Run(InstallMotherDuck);

            if (install.IsFaulted || install.IsCanceled)
            {
                string why = install.Exception?.GetBaseException().Message ?? "the download was cancelled";
                _motherDuckInstall = Task.Run(InstallMotherDuck);
                throw new InvalidOperationException(
                    $"MotherDuck's extension could not be installed ({why}); another attempt has started. Try again in a minute.");
            }
        }

        if (!install.IsCompleted)
        {
            throw new InvalidOperationException(
                "MotherDuck's extension is still being downloaded from DuckDB's repository. Try again in a minute.");
        }

        return install.Result;
    }

    /// <summary>Finds or downloads MotherDuck's extension, and proves DuckDB loads it before it is kept.</summary>
    /// <remarks>
    /// <b>Bounded in time and in size, written under a name no other process uses, and deleted if DuckDB will
    /// not load it</b> — the review's three findings about a partial file shared between servers on one volume
    /// becoming a broken extension nobody removes. DuckDB checks the signature when it loads; unsigned
    /// extensions stay off, so a file altered on the way is refused here rather than at a layer's first query.
    /// </remarks>
    private string InstallMotherDuck()
    {
        const string Name = "motherduck.duckdb_extension";

        if (_options.ExtensionDirectory is { } shipped
            && Path.Combine(shipped, GeoParquetFolder.ExtensionPlatform, Name) is { } provided
            && File.Exists(provided))
        {
            return provided;
        }

        string version;

        using (DuckDB.NET.Data.DuckDBConnection probe = new("DataSource=:memory:"))
        {
            probe.Open();
            using DuckDB.NET.Data.DuckDBCommand command = probe.CreateCommand();
            command.CommandText = "select version()";
            version = (string)command.ExecuteScalar()!;
        }

        string target = Path.Combine(_motherDuckDirectory!, version, GeoParquetFolder.ExtensionPlatform, Name);

        if (File.Exists(target) && Loads(target))
        {
            return target;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string partial = $"{target}.{Environment.ProcessId}.{Guid.NewGuid():n}.partial";

        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(5));
            using System.Net.Http.HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
            using Stream compressed = http.GetStreamAsync(
                new Uri($"https://extensions.duckdb.org/{version}/{GeoParquetFolder.ExtensionPlatform}/{Name}.gz"),
                deadline.Token).GetAwaiter().GetResult();
            using System.IO.Compression.GZipStream gzip = new(compressed, System.IO.Compression.CompressionMode.Decompress);

            using (FileStream file = File.Create(partial))
            {
                byte[] buffer = new byte[81920];
                long written = 0;
                int read;

                while ((read = gzip.ReadAsync(buffer, deadline.Token).AsTask().GetAwaiter().GetResult()) > 0)
                {
                    written += read;

                    if (written > MostExtensionBytes)
                    {
                        throw new InvalidOperationException("the download is larger than any MotherDuck extension");
                    }

                    file.Write(buffer, 0, read);
                }
            }

            if (!Loads(partial))
            {
                throw new InvalidOperationException("DuckDB would not load the downloaded file");
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            File.Delete(partial);
        }

        static bool Loads(string path)
        {
            try
            {
                using DuckDB.NET.Data.DuckDBConnection check = new("DataSource=:memory:");
                check.Open();
                using DuckDB.NET.Data.DuckDBCommand command = check.CreateCommand();
                command.CommandText =
                    "set autoinstall_known_extensions = false; set autoload_known_extensions = false; "
                    + $"load '{path.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal)}'";
                command.ExecuteNonQuery();
                return true;
            }
            catch (DuckDB.NET.Data.DuckDBException)
            {
                return false;
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MotherDuckName();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex JwtShaped();

    /// <summary>Whether remote locations can be read: <c>httpfs</c> is where the deployment said — ADR-067 §5.2.</summary>
    public bool RemoteEnabled =>
        _options.ExtensionDirectory is { } directory && File.Exists(GeoParquetFolder.HttpfsPath(directory));

    /// <summary>Turns a remote location request into the locator a registration stores.</summary>
    /// <param name="request">What the administrator sent.</param>
    /// <param name="locator">The locator to seal.</param>
    /// <param name="why">Why it was refused.</param>
    /// <returns>Whether it may be registered.</returns>
    public bool TryLocateRemote(RemoteLocationRequest request, out string? locator, out string? why)
    {
        if (!RemoteEnabled)
        {
            locator = null;
            why = RemoteGeoParquetLocations.Off;
            return false;
        }

        return RemoteGeoParquetLocations.TryLocate(request, _allowPrivate, out locator, out why);
    }

    /// <summary>The key an instance is kept under: the canonical folder, or a hash of a remote locator.</summary>
    /// <remarks>
    /// <b>Hashed, so the dictionary holds no credential</b> — two registrations of one bucket with
    /// different keys are two instances, which is right, because each reads with its own.
    /// </remarks>
    private static string KeyOf(string locator) =>
        GeoParquetLocator.IsRemote(locator) || GeoParquetLocator.IsAttached(locator)
            ? "hashed:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(locator)))
            : Normalise(Path.GetFullPath(GeoParquetLocator.FolderOf(locator)));

    /// <summary>Where a locator reads from, for a sentence: the folder, the remote location or the database, without credentials.</summary>
    public static string LocationOf(string locator) =>
        GeoParquetLocator.IsRemote(locator)
            ? RemoteGeoParquetLocations.Parse(locator).Location
            : GeoParquetLocator.IsAttached(locator)
                ? ParseAttached(locator).Location
                : GeoParquetLocator.FolderOf(locator);

    /// <summary>The root folders may be registered under, or null when the feature is off.</summary>
    public string? Root { get; }

    /// <summary>The sentence a caller gets when the feature is off.</summary>
    public const string Off =
        "GeoParquet folders are not enabled on this server. Set Graticula:GeoParquetRoot (or the "
        + "environment variable Graticula__GeoParquetRoot) to the directory they may be registered "
        + "under and restart — a folder is served from inside this process, so which directories "
        + "may be read is the deployment's decision rather than the API's.";

    /// <summary>
    /// Turns what an administrator typed into the locator a registration stores.
    /// </summary>
    /// <param name="requested">A folder, relative to the root or absolute inside it.</param>
    /// <param name="locator">The locator to seal and store.</param>
    /// <param name="why">Why it was refused.</param>
    /// <returns>Whether the folder may be registered.</returns>
    public bool TryLocate(string? requested, out string? locator, out string? why)
    {
        locator = null;
        why = null;

        if (Root is null)
        {
            why = Off;
            return false;
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            why = $"A folder is required: the name of one inside {Root}, or its whole path.";
            return false;
        }

        string folder = Normalise(Path.GetFullPath(Path.IsPathRooted(requested)
            ? requested.Trim()
            : Path.Combine(Root, requested.Trim())));

        if (!Inside(folder))
        {
            why = $"'{requested}' is outside {Root}, the only directory this server may read GeoParquet from.";
            return false;
        }

        if (!Directory.Exists(folder))
        {
            why = $"There is no folder at '{folder}'.";
            return false;
        }

        if (Unsafe(folder) is { } unsafeWhy)
        {
            why = unsafeWhy;
            return false;
        }

        locator = GeoParquetLocator.For(folder);
        return true;
    }

    /// <summary>The folder a stored locator names, opened and sandboxed.</summary>
    /// <param name="locator">A GeoParquet locator.</param>
    /// <returns>The folder's DuckDB.</returns>
    public GeoParquetFolder FolderFor(string locator)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (GeoParquetLocator.IsRemote(locator))
        {
            return Kept(KeyOf(locator), () => OpenRemote(locator));
        }

        if (GeoParquetLocator.IsAttached(locator))
        {
            return Kept(KeyOf(locator), () => OpenAttached(locator));
        }

        // <b>Canonical before it is compared</b> — a security review's finding. A stored
        // `…/geoparquet/../../etc` begins with the root as text and is not inside it.
        string folder = Normalise(Path.GetFullPath(GeoParquetLocator.FolderOf(locator)));

        if (Root is null)
        {
            throw new InvalidOperationException(
                $"A layer is served from the GeoParquet folder '{folder}' and this server has no "
                + "Graticula:GeoParquetRoot, so it reads no folders. " + Off);
        }

        if (!Inside(folder))
        {
            throw new InvalidOperationException(
                $"A layer is served from the GeoParquet folder '{folder}', which is outside "
                + $"Graticula:GeoParquetRoot ({Root}). The root has moved since the folder was "
                + "registered, and a folder outside it is not read.");
        }

        if (Unsafe(folder) is { } unsafeWhy)
        {
            throw new InvalidOperationException(unsafeWhy);
        }

        return Kept(folder, () => new GeoParquetFolder(folder, _options));
    }

    private GeoParquetFolder Kept(string key, Func<GeoParquetFolder> open)
    {
        Lazy<GeoParquetFolder> opened = _folders.GetOrAdd(key, _ => new Lazy<GeoParquetFolder>(open));

        try
        {
            return opened.Value;
        }
        catch
        {
            // A folder that could not be opened — deleted, unreadable — is tried again next time
            // rather than remembered as broken for the life of the process.
            _folders.TryRemove(new KeyValuePair<string, Lazy<GeoParquetFolder>>(key, opened));
            throw;
        }
    }

    /// <summary>A DuckDB file or a MotherDuck database, checked again and opened — ADR-067 §5.3–5.4.</summary>
    private GeoParquetFolder OpenAttached(string locator)
    {
        AttachedDuckDb attached = ParseAttached(locator);

        if (attached.IsMotherDuck)
        {
            if (!MotherDuckEnabled)
            {
                throw new InvalidOperationException("A layer is served from MotherDuck and this server does not read it. " + MotherDuckOff);
            }

            string extension;

            try
            {
                extension = MotherDuckExtension();
            }
            catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
            {
                throw new InvalidOperationException($"MotherDuck's extension could not be installed: {e.Message}");
            }

            return new GeoParquetFolder(attached, _options with { MotherDuckExtension = extension });
        }

        if (Root is null)
        {
            throw new InvalidOperationException(DuckDbFilesOff);
        }

        string file = Normalise(Path.GetFullPath(attached.File!));

        if (DuckDbFileRefusal(file) is { } why)
        {
            throw new InvalidOperationException(why);
        }

        return new GeoParquetFolder(attached with { File = file }, _options);
    }

    /// <summary>A remote location, checked and opened — ADR-067 §5.2.</summary>
    /// <remarks>
    /// <b>The address check again, at open</b>: a name can move to a private address after it was
    /// registered, and this is the last moment before DuckDB is handed it.
    /// </remarks>
    private GeoParquetFolder OpenRemote(string locator)
    {
        if (!RemoteEnabled)
        {
            throw new InvalidOperationException(
                "A layer is served from a remote GeoParquet location and this server cannot read one. "
                + RemoteGeoParquetLocations.Off);
        }

        RemoteGeoParquet remote = RemoteGeoParquetLocations.Parse(locator);

        if (RemoteGeoParquetLocations.Unreachable(remote, _allowPrivate) is { } why)
        {
            throw new InvalidOperationException(why);
        }

        return new GeoParquetFolder(remote, _options);
    }

    /// <summary>A file's own version, for a tile cache key — or null when it cannot be read.</summary>
    /// <param name="locator">A GeoParquet locator.</param>
    /// <param name="tableName">The file, named the way a layer's table name already is: without
    /// its <c>.parquet</c> extension.</param>
    /// <remarks>
    /// <b>Added 2026-09-13 so a replaced file cannot go on serving stale tiles.</b>
    /// <see cref="GeoParquetFeatureSource"/> already refuses a query against a file that no
    /// longer matches what was published — a different geometry column, a broken identity — but
    /// a replacement that keeps the same shape answers every query correctly and every cached
    /// tile <em>incorrectly</em>, because nothing about the replacement changed a cache key built
    /// only from the layer's schema. <c>GeoParquetTable.Version</c> is built from the file's
    /// length and modification time, so it changes on every replacement and on nothing else —
    /// which is exactly what <see cref="Graticula.Tiles.TileCacheKey.FingerprintOf"/> needs to
    /// make a replacement structurally invalidating, the same way a schema change already is.
    /// </remarks>
    /// <returns>The version, or null when the folder or the file cannot be read right now.</returns>
    public string? VersionOf(string locator, string tableName)
    {
        try
        {
            return FolderFor(locator).Find(tableName)?.Version;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // <b>Null, not a thrown failure.</b> This is read to build a cache key, before the
            // tile query itself has run; if the folder genuinely cannot be read, the query a few
            // lines later reports that in its own words, and this is not the place to pre-empt it
            // with a different message for the same fact.
            return null;
        }
    }

    /// <summary>Closes a folder's DuckDB, so the next use opens it afresh.</summary>
    /// <param name="locator">A GeoParquet locator.</param>
    /// <returns>Whether one was open.</returns>
    public bool Close(string locator)
    {
        if (!GeoParquetLocator.Is(locator)
            || !_folders.TryRemove(KeyOf(locator), out Lazy<GeoParquetFolder>? opened))
        {
            return false;
        }

        if (opened.IsValueCreated)
        {
            opened.Value.Dispose();
        }

        return true;
    }

    /// <summary>What a folder holds, in the probe's vocabulary.</summary>
    /// <param name="locator">A GeoParquet locator.</param>
    /// <returns>The files that can be published, and the ones that cannot with why.</returns>
    public ProbeResult Probe(string locator)
    {
        string folder;

        try
        {
            folder = LocationOf(locator);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            return new ProbeResult(ProbeOutcome.CannotConnect, "The stored remote location cannot be read.", null, null, []);
        }

        GeoParquetFolder opened;
        GeoParquetFolder? transient = null;

        try
        {
            // <b>A folder nothing serves is probed and closed again</b> — a security review found that
            // testing any folder under the root left its DuckDB open for the life of the process.
            // One that a layer already reads is probed through the instance it reads with.
            opened = IsOpen(locator)
                ? FolderFor(locator)
                : transient = Opened(locator);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new ProbeResult(ProbeOutcome.CannotConnect, e.Message, null, null, []);
        }
        catch (DuckDB.NET.Data.DuckDBException e)
        {
            return new ProbeResult(ProbeOutcome.CannotConnect, $"'{folder}' cannot be read: {FirstLine(e.Message)}", null, null, []);
        }

        using (transient)
        {
            return Probe(folder, locator, opened);
        }
    }

    private ProbeResult Probe(string folder, string locator, GeoParquetFolder opened)
    {
        IReadOnlyList<GeoParquetTable> files;

        try
        {
            files = opened.List();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Opened earlier and gone since — the folder was deleted or its permissions changed.
            Close(locator);
            return new ProbeResult(ProbeOutcome.CannotConnect, $"The folder '{folder}' cannot be read: {e.Message}", null, null, []);
        }
        catch (DuckDB.NET.Data.DuckDBException e)
        {
            // A bucket that refuses the listing — wrong region, no permission, no such bucket — says so
            // here, in DuckDB's words, which name the HTTP status.
            return new ProbeResult(ProbeOutcome.CannotConnect, $"'{folder}' cannot be listed: {FirstLine(e.Message)}", null, null, []);
        }

        List<SourceTable> tables = [];
        List<SkippedTable> skipped = [];

        foreach (GeoParquetTable file in files)
        {
            if (file.Problem is { } problem || file.Kind is null || file.Geometry.Srid is null)
            {
                skipped.Add(new SkippedTable(
                    opened.IsAttached ? "main." + file.Name : file.Name + ".parquet",
                    file.Problem ?? "Every geometry in the file is null, so there is no geometry type to publish it as."));
                continue;
            }

            tables.Add(new SourceTable(
                GeoParquetTableSchema,
                file.Name,
                file.Geometry.Column,
                file.Geometry.Srid.Value,
                file.Kind.Value.ToString(),
                file.CandidateObjectIdColumn,
                PrimaryKeyColumn: null,
                file.IdentityCandidates,
                Writable: false));
        }

        string message = opened.IsAttached
            ? (files.Count == 0
                ? $"{opened.EngineVersion} found no tables in the main schema of '{folder}'."
                : $"{opened.EngineVersion} read {files.Count} table{(files.Count == 1 ? string.Empty : "s")} in '{folder}': "
                  + $"{tables.Count} can be published"
                  + (skipped.Count == 0 ? "." : $", and {skipped.Count} cannot — each says why.")
                  + " Its layers are read-only.")
            : opened.IsRemote
            ? (files.Count == 0
                ? $"{opened.EngineVersion} found no .parquet files at '{folder}'."
                : $"{opened.EngineVersion} read {files.Count} .parquet file{(files.Count == 1 ? string.Empty : "s")} at "
                  + $"'{folder}': {tables.Count} can be published"
                  + (skipped.Count == 0 ? "." : $", and {skipped.Count} cannot — each says why.")
                  + " Remote GeoParquet layers are read-only, and each query reads over the network."
                  + (opened.RemoteListingTruncated
                      ? $" Listing stopped at {GeoParquetFolder.MostRemoteFiles:N0} files; register a narrower prefix to reach the rest."
                      : string.Empty))
            : files.Count == 0
            ? $"{opened.EngineVersion} found no .parquet files in '{folder}'."
            : $"{opened.EngineVersion} read {files.Count} .parquet file{(files.Count == 1 ? string.Empty : "s")} in "
              + $"'{folder}': {tables.Count} can be published"
              + (skipped.Count == 0 ? "." : $", and {skipped.Count} cannot — each says why.")
              + " GeoParquet layers are read-only.";

        return new ProbeResult(
            tables.Count > 0 ? ProbeOutcome.Usable : ProbeOutcome.UnusableGeometry,
            message,
            opened.EngineVersion,
            null,
            tables,
            skipped);
    }

    private bool IsOpen(string locator) => _folders.ContainsKey(KeyOf(locator));

    private static string FirstLine(string message)
    {
        int newline = message.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? message : message[..newline];
    }

    /// <summary>A folder checked as <see cref="FolderFor"/> checks it, opened for one use.</summary>
    private GeoParquetFolder Opened(string locator)
    {
        if (GeoParquetLocator.IsRemote(locator))
        {
            return OpenRemote(locator);
        }

        if (GeoParquetLocator.IsAttached(locator))
        {
            return OpenAttached(locator);
        }

        string folder = Normalise(Path.GetFullPath(GeoParquetLocator.FolderOf(locator)));

        if (Root is null)
        {
            throw new InvalidOperationException(Off);
        }

        if (!Inside(folder))
        {
            throw new InvalidOperationException($"'{folder}' is outside {Root}.");
        }

        if (Unsafe(folder) is { } unsafeWhy)
        {
            throw new InvalidOperationException(unsafeWhy);
        }

        return new GeoParquetFolder(folder, _options);
    }

    /// <summary>Why a folder under the root may not be read even so, or null.</summary>
    /// <remarks>
    /// <para>
    /// <b>No pattern characters.</b> DuckDB reads <c>*</c>, <c>?</c> and brackets in a path as a
    /// pattern, so a folder named <c>a?</c> would name its siblings too — a security review's
    /// finding, refused rather than escaped because no spelling of a folder needs them.
    /// </para>
    /// <para>
    /// <b>No links between the root and the folder.</b> The root itself may be one — where a
    /// deployment mounts its data is its own business — but a link inside it points somewhere the
    /// root does not name, and the sandbox compares text rather than following links.
    /// </para>
    /// </remarks>
    private string? Unsafe(string folder)
    {
        if (folder.AsSpan().IndexOfAny(PatternCharacters) >= 0)
        {
            return $"'{folder}' contains a character DuckDB reads as a file pattern (* ? [ ] {{ }}). "
                + "Rename the folder.";
        }

        string relative = Root is { Length: > 0 } root && folder.Length > root.Length
            ? folder[(root.Length + 1)..]
            : string.Empty;

        string walked = Root ?? string.Empty;

        foreach (string segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walked = walked + "/" + segment;

            if (Directory.Exists(walked) && new DirectoryInfo(walked).LinkTarget is not null)
            {
                return $"'{walked}' is a symbolic link, and a GeoParquet folder must be a directory inside "
                    + $"{Root} rather than a pointer to somewhere else.";
            }
        }

        return null;
    }

    /// <summary>
    /// The schema name a GeoParquet layer is published under — DuckDB's default schema.
    /// </summary>
    /// <remarks>
    /// A layer names a schema and a table because every PostGIS layer does, and the catalogue's
    /// uniqueness and addressing are built on the pair. A file has no schema; <c>main</c> is what
    /// DuckDB calls the schema a query runs in, so the pair reads as what it would be if the file
    /// were attached — and a folder is one schema, so the table name alone tells two files apart.
    /// </remarks>
    public const string GeoParquetTableSchema = "main";

    /// <summary>
    /// Why a publication cannot be served from this folder, or null when it can.
    /// </summary>
    /// <param name="locator">The source's locator.</param>
    /// <param name="publication">What is being published.</param>
    /// <returns>A sentence for the publisher, or null.</returns>
    /// <remarks>
    /// <b>Checked against the file, not against what the form sent.</b> The console fills the
    /// request from the probe, but the request is an API call anybody with the privilege can write,
    /// and a layer published with the wrong reference or a non-unique identity answers every query
    /// successfully and wrongly. The PostGIS path asks the database the same two questions —
    /// validity and the declared reference — and this is their equivalent for a file.
    /// </remarks>
    public string? RefusalFor(string locator, LayerPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);

        GeoParquetFolder folder;

        try
        {
            folder = FolderFor(locator);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }

        string table = $"{publication.SchemaName}.{publication.TableName}";
        string called = folder.IsAttached ? $"'main.{publication.TableName}'" : $"'{publication.TableName}.parquet'";

        if (!string.Equals(publication.SchemaName, GeoParquetTableSchema, StringComparison.Ordinal))
        {
            return folder.IsAttached
                ? $"'{table}' is not served from '{folder.Folder}': only tables in its '{GeoParquetTableSchema}' schema are."
                : $"'{table}' is not a file in this folder: a GeoParquet layer's schema is always "
                  + $"'{GeoParquetTableSchema}' and its table is the file name without '.parquet'.";
        }

        GeoParquetTable? file;

        try
        {
            file = folder.Find(publication.TableName);
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }

        if (file is null)
        {
            return $"There is no {called} in '{folder.Folder}'.";
        }

        if (file.Problem is { } problem)
        {
            return $"{called} cannot be published: {problem}";
        }

        if (!string.Equals(publication.GeometryColumn, file.Geometry.Column, StringComparison.Ordinal))
        {
            return $"{called} keeps its geometry in '{file.Geometry.Column}', "
                + $"not '{publication.GeometryColumn}'.";
        }

        if (publication.Srid != file.Geometry.Srid)
        {
            return $"{called} says its coordinates are in EPSG:{file.Geometry.Srid}, "
                + $"and the publication declares EPSG:{publication.Srid}. "
                + (file.SridDeclared
                    ? "That reference is the one the source was registered with; to publish in another, correct the registration."
                    : "A file's reference is read from the file and cannot be overridden: a layer published in any other one answers "
                      + "every request with its features somewhere they are not.");
        }

        if (!file.IdentityCandidates.Contains(publication.IdentityColumn, StringComparer.Ordinal))
        {
            return $"'{publication.IdentityColumn}' cannot be the identity of '{called}: "
                + "a GeoParquet layer's identity is an integer column measured unique and never null, "
                + $"or {GeoParquetFolder.RowNumberColumn}. The candidates are "
                + string.Join(", ", file.IdentityCandidates) + ".";
        }

        if (publication.ObjectIdColumn is { } objectId
            && !string.Equals(objectId, publication.IdentityColumn, StringComparison.Ordinal))
        {
            return "A GeoParquet layer's object id is its identity column, because the identity is "
                + "the only column measured unique; send the same name for both.";
        }

        if (file.Kind is not { } kind || Family(kind) != Family(publication.GeometryType))
        {
            return $"{called} holds {(file.Kind?.ToString() ?? "no")} geometry, "
                + $"not {publication.GeometryType}.";
        }

        return null;
    }

    /// <summary>
    /// Whether a table whose reference was declared at registration holds coordinates that could be in it,
    /// or null when it does, or when this server cannot tell — ADR-067 condition 5.
    /// </summary>
    /// <param name="locator">The source's locator.</param>
    /// <param name="publication">What is being published.</param>
    /// <param name="projector">The datastore's projector, which knows each reference's area of use.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A sentence for the publisher, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>Only for a declared reference.</b> A reference read from the data is the data's word; one typed into a
    /// registration is a person's, and a DuckDB file cannot keep one of its own (ADR-067 §4) — so every table
    /// in a file is published on a declaration.
    /// </para>
    /// <para>
    /// <b>A heuristic, and named as one</b>, as <c>DeclaredReference</c> names its PostGIS twin: the table's
    /// extent is moved into longitude and latitude from the declared reference and must fall inside that
    /// reference's area of use, widened by a degree. It catches the failure that matters — metres declared as
    /// degrees, one national grid declared as another continent's — and cannot catch two projected systems
    /// whose areas overlap, because metres look like metres.
    /// </para>
    /// </remarks>
    public async Task<string?> DeclaredReferenceRefusalAsync(
        string locator, LayerPublication publication, IProjector projector, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(projector);

        if (!GeoParquetLocator.IsAttached(locator))
        {
            return null;
        }

        GeoParquetFolder folder;
        GeoParquetTable? table;

        try
        {
            folder = FolderFor(locator);
            table = folder.Find(publication.TableName);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return e.Message;
        }

        if (table is null || table.Problem is not null || !table.SridDeclared || table.Geometry.Srid is not { } srid)
        {
            return null;
        }

        Envelope? extent = await Task.Run(() => folder.AttachedExtent(table), cancellation).ConfigureAwait(false);
        Envelope? domain = await projector.DomainOfAsync(srid, cancellation).ConfigureAwait(false);

        if (extent is not { } box)
        {
            // An empty table, or one whose geometry could not be read: nothing to be wrong about yet.
            return null;
        }

        if (domain is not { } area)
        {
            // <b>Refused rather than passed</b> — a security review: a code whose area of use this deployment does
            // not know was the one declaration the check waved through.
            return $"EPSG:{srid} has no area of use this server knows, so a declaration that main.{publication.TableName} "
                + "is in it cannot be checked against its coordinates. Register the source with a reference the "
                + "projection database describes — 4326 for longitude and latitude, 3857 for web-Mercator metres.";
        }

        Envelope degrees;

        if (srid == 4326)
        {
            degrees = box;
        }
        else
        {
            try
            {
                Polygon outline = new(new LinearRing(XySequence.Wrap(
                    [box.MinX, box.MinY, box.MaxX, box.MinY, box.MaxX, box.MaxY, box.MinX, box.MaxY, box.MinX, box.MinY])));

                (IReadOnlyList<Geometry> moved, _) = await projector
                    .ProjectAsync([outline], srid, 4326, cancellation).ConfigureAwait(false);

                degrees = moved[0].Envelope;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return $"main.{publication.TableName}'s coordinates ({Describe(box)}) cannot be in EPSG:{srid}: moving them "
                    + $"into longitude and latitude failed ({e.Message}). The reference the source was registered with "
                    + "is probably not the one its geometry is in.";
            }
        }

        Envelope widened = new(area.MinX - 1, area.MinY - 1, area.MaxX + 1, area.MaxY + 1);

        if (double.IsFinite(degrees.MinX) && double.IsFinite(degrees.MaxY)
            && degrees.MinX >= widened.MinX && degrees.MaxX <= widened.MaxX
            && degrees.MinY >= widened.MinY && degrees.MaxY <= widened.MaxY)
        {
            return null;
        }

        return $"main.{publication.TableName}'s coordinates ({Describe(box)}) do not fall inside EPSG:{srid}'s area of use "
            + $"({Describe(area)} in degrees), and the source was registered as being in EPSG:{srid}. A layer published "
            + "on a wrong reference answers every request with its features somewhere they are not. Register the source "
            + "with the reference its geometry is in.";

        static string Describe(Envelope e) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{e.MinX:G6}, {e.MinY:G6} to {e.MaxX:G6}, {e.MaxY:G6}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Lazy<GeoParquetFolder> opened in _folders.Values)
        {
            if (opened.IsValueCreated)
            {
                opened.Value.Dispose();
            }
        }

        _folders.Clear();
    }

    private static int Family(GeometryKind kind) => kind switch
    {
        GeometryKind.Point or GeometryKind.MultiPoint => 0,
        GeometryKind.LineString or GeometryKind.MultiLineString => 1,
        _ => 2,
    };

    private bool Inside(string folder)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return Root is not null
            && (string.Equals(folder, Root, comparison)
                || folder.StartsWith(Root + "/", comparison)
                || Root.Length == 0);
    }

    private static string Normalise(string path) => path.Replace('\\', '/').TrimEnd('/');
}

/// <summary>
/// The probe every data-source endpoint calls, sending a GeoParquet locator to the folder and
/// everything else to PostgreSQL.
/// </summary>
internal sealed class DataSourceProbes(IDataSourceProbe postgres, GeoParquetSources geoParquet) : IDataSourceProbe
{
    public Task<ProbeResult> ProbeAsync(string connectionString, CancellationToken cancellationToken) =>
        GeoParquetLocator.Is(connectionString)
            ? Task.Run(() => geoParquet.Probe(connectionString), cancellationToken)
            : postgres.ProbeAsync(connectionString, cancellationToken);

    public Task<DatabaseListing> ListDatabasesAsync(string connectionString, CancellationToken cancellationToken) =>
        GeoParquetLocator.Is(connectionString)
            ? Task.FromResult(new DatabaseListing(
                ProbeOutcome.CannotConnect,
                "A GeoParquet folder has no databases; its files are listed by testing the folder.",
                []))
            : postgres.ListDatabasesAsync(connectionString, cancellationToken);
}
