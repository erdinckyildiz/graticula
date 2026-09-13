using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
internal sealed class GeoParquetSources : IDisposable
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
    private bool _disposed;

    public GeoParquetSources(
        string? root,
        string memoryLimit = "1GB",
        int? threads = null,
        string? extensionDirectory = null,
        bool allowPrivate = false)
    {
        Root = string.IsNullOrWhiteSpace(root) ? null : Normalise(Path.GetFullPath(root));
        _options = new GeoParquetOptions
        {
            MemoryLimit = memoryLimit,
            Threads = threads,
            ExtensionDirectory = string.IsNullOrWhiteSpace(extensionDirectory) ? null : Path.GetFullPath(extensionDirectory),
        };
        _allowPrivate = allowPrivate;
    }

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
        GeoParquetLocator.IsRemote(locator)
            ? "remote:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(locator)))
            : Normalise(Path.GetFullPath(GeoParquetLocator.FolderOf(locator)));

    /// <summary>Where a locator reads from, for a sentence: the folder, or the remote location without credentials.</summary>
    public static string LocationOf(string locator) =>
        GeoParquetLocator.IsRemote(locator)
            ? RemoteGeoParquetLocations.Parse(locator).Location
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
                    file.Name + ".parquet",
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

        string message = opened.IsRemote
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

        if (!string.Equals(publication.SchemaName, GeoParquetTableSchema, StringComparison.Ordinal))
        {
            return $"'{table}' is not a file in this folder: a GeoParquet layer's schema is always "
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
            return $"There is no '{publication.TableName}.parquet' in '{folder.Folder}'.";
        }

        if (file.Problem is { } problem)
        {
            return $"'{publication.TableName}.parquet' cannot be published: {problem}";
        }

        if (!string.Equals(publication.GeometryColumn, file.Geometry.Column, StringComparison.Ordinal))
        {
            return $"'{publication.TableName}.parquet' keeps its geometry in '{file.Geometry.Column}', "
                + $"not '{publication.GeometryColumn}'.";
        }

        if (publication.Srid != file.Geometry.Srid)
        {
            return $"'{publication.TableName}.parquet' says its coordinates are in EPSG:{file.Geometry.Srid}, "
                + $"and the publication declares EPSG:{publication.Srid}. A file's reference is read "
                + "from the file and cannot be overridden: a layer published in any other one answers "
                + "every request with its features somewhere they are not.";
        }

        if (!file.IdentityCandidates.Contains(publication.IdentityColumn, StringComparer.Ordinal))
        {
            return $"'{publication.IdentityColumn}' cannot be the identity of '{publication.TableName}.parquet': "
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
            return $"'{publication.TableName}.parquet' holds {(file.Kind?.ToString() ?? "no")} geometry, "
                + $"not {publication.GeometryType}.";
        }

        return null;
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
