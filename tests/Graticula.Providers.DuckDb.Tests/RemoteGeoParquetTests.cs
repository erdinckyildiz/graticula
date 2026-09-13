using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Testing;
using Xunit;

namespace Graticula.Providers.DuckDb.Tests;

/// <summary>
/// GeoParquet read over the network — ADR-067 §5.2 — against a small HTTP server in this process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plain http on loopback, which the host refuses and the provider does not.</b> Whether a
/// location is https and public is the registration's rule (<c>GeoParquetSources</c>), tested there;
/// what is tested here is what DuckDB does with a URL once it has one — reads it, confines itself to
/// it, and never learns a credential it could leak.
/// </para>
/// <para>
/// <b>The extension is downloaded, and a test that cannot get it FAILS rather than skips</b>, the
/// rule every environment-dependent suite in this repository follows: green with its subject absent
/// is worse than no test.
/// </para>
/// </remarks>
public sealed class RemoteGeoParquetTests : IDisposable
{
    private readonly TemporaryFolder _served = new();
    private readonly FileServer _server;

    public RemoteGeoParquetTests()
    {
        GeoParquetFixture.Write(_served.File("grid.parquet"), Shapes.GridColumns, Shapes.Grid(10), srid: 3857);
        GeoParquetFixture.Write(_served.File("other.parquet"), Shapes.GridColumns, Shapes.Grid(2), srid: 3857);
        _server = new FileServer(_served.Path);
    }

    public void Dispose()
    {
        _server.Dispose();
        _served.Dispose();
    }

    private static GeoParquetOptions Options() => new()
    {
        MemoryLimit = "256MB",
        Threads = 2,
        ExtensionDirectory = Extensions.Directory(),
    };

    [Fact]
    public void A_remote_location_never_creates_a_DuckDB_secret()
    {
        // ADR-067 §3: a secret created before the allow-list makes DuckDB read outside it.
        IReadOnlyList<string> settings = GeoParquetFolder.RemoteSettings(
            new RemoteGeoParquet("s3://bucket/prefix/", "eu-west-1", "minio.example.org", "AKIAEXAMPLE", "s3cr3t", "path", UseSsl: false),
            Options());

        Assert.DoesNotContain(settings, s => s.Contains("secret", StringComparison.OrdinalIgnoreCase)
            && !s.StartsWith("set global s3_secret_access_key", StringComparison.Ordinal)
            && !s.StartsWith("set allow_persistent_secrets", StringComparison.Ordinal));

        int load = IndexOf(settings, "load ");
        int key = IndexOf(settings, "set global s3_access_key_id");
        int allowed = IndexOf(settings, "set allowed_");
        int closed = IndexOf(settings, "set enable_external_access = false");

        Assert.True(load < key && key < allowed && allowed < closed, string.Join("\n", settings));
        Assert.Equal("set lock_configuration = true", settings[^1]);
        Assert.Contains("set allowed_directories = ['s3://bucket/prefix/']", settings);
    }

    [Fact]
    public void Nothing_is_inherited_from_the_server_s_own_AWS_environment()
    {
        // Measured on 1.5.5: loading httpfs copies these into its settings. A security review's P1.
        (string Name, string Value)[] environment =
        [
            ("AWS_ACCESS_KEY_ID", "AKIAFROMTHEENVIRONMENT"),
            ("AWS_SECRET_ACCESS_KEY", "secret-from-the-environment"),
            ("AWS_SESSION_TOKEN", "token-from-the-environment"),
            ("AWS_REGION", "eu-north-1"),
            ("DUCKDB_S3_ENDPOINT", "elsewhere.example.org"),
        ];

        Dictionary<string, string?> before = environment.ToDictionary(e => e.Name, e => Environment.GetEnvironmentVariable(e.Name));

        try
        {
            foreach ((string name, string value) in environment)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            using GeoParquetFolder remote = new(new RemoteGeoParquet(_server.Url("grid.parquet")), Options());
            using DuckDBConnection connection = remote.Open();

            string Setting(string name) =>
                Scalar(connection, $"select coalesce(value, '') from duckdb_settings() where name = '{name}'") as string ?? "<absent>";

            Assert.Equal(string.Empty, Setting("s3_access_key_id"));
            Assert.Equal(string.Empty, Setting("s3_secret_access_key"));
            Assert.Equal(string.Empty, Setting("s3_session_token"));
            Assert.Equal("us-east-1", Setting("s3_region"));
            Assert.Equal("s3.amazonaws.com", Setting("s3_endpoint"));
            Assert.Equal("false", Setting("s3_allow_recursive_globbing"));
            Assert.Equal(1L, long.Parse(Setting("http_retries"), CultureInfo.InvariantCulture));
        }
        finally
        {
            foreach ((string name, string? value) in before)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void Two_locations_never_see_each_other_s_credentials()
    {
        // The settings are global so query connections see them; global must still mean one instance.
        using GeoParquetFolder first = new(
            new RemoteGeoParquet(_server.Url("grid.parquet"), AccessKeyId: "AKIAFIRSTLOCATION", SecretAccessKey: "first-secret"), Options());
        using GeoParquetFolder second = new(
            new RemoteGeoParquet(_server.Url("other.parquet"), AccessKeyId: "AKIASECONDLOCATION", SecretAccessKey: "second-secret"), Options());

        using DuckDBConnection a = first.Open();
        using DuckDBConnection b = second.Open();

        Assert.Equal("AKIAFIRSTLOCATION", Scalar(a, "select value from duckdb_settings() where name = 's3_access_key_id'"));
        Assert.Equal("AKIASECONDLOCATION", Scalar(b, "select value from duckdb_settings() where name = 's3_access_key_id'"));

        using GeoParquetFolder anonymous = new(new RemoteGeoParquet(_server.Url("grid.parquet")), Options());
        using DuckDBConnection c = anonymous.Open();

        Assert.Equal(string.Empty, Scalar(c, "select coalesce(value, '') from duckdb_settings() where name = 's3_access_key_id'"));
    }

    [Fact]
    public void Two_files_that_would_share_a_name_are_both_refused_rather_than_numbered()
    {
        // A security review's L3: numbering let a writer who adds `a+b.parquet` take over `a_b`.
        const string Prefix = "s3://bucket/prefix/";

        var (files, problems, names) = GeoParquetFolder.MapRemoteNames(
            [Prefix + "a+b.parquet", Prefix + "a-b.parquet", Prefix + "roads.parquet", Prefix + "odd*.parquet", Prefix + "notes.csv"],
            Prefix);

        Assert.Equal(["a_b", "roads", "odd_"], names);
        Assert.Equal(["roads"], files.Keys);
        Assert.Contains("a+b.parquet", problems["a_b"], StringComparison.Ordinal);
        Assert.Contains("a-b.parquet", problems["a_b"], StringComparison.Ordinal);
        Assert.Contains("pattern", problems["odd_"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_preparation_failure_keeps_no_DuckDB_exception_that_could_quote_the_statement()
    {
        GeoParquetOptions missing = Options() with { ExtensionDirectory = _served.File("no-extensions-here") };

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            new GeoParquetFolder(new RemoteGeoParquet(_server.Url("grid.parquet")), missing));

        Assert.Null(refused.InnerException);
    }

    [Fact]
    public void Its_text_never_carries_the_secret()
    {
        RemoteGeoParquet remote = new("s3://bucket/prefix/", AccessKeyId: "AKIAEXAMPLE", SecretAccessKey: "s3cr3t-value");

        Assert.DoesNotContain("s3cr3t-value", remote.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_location_that_cannot_be_prepared_says_why_without_the_statement()
    {
        GeoParquetOptions missing = Options() with { ExtensionDirectory = _served.File("no-extensions-here") };

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            new GeoParquetFolder(new RemoteGeoParquet(_server.Url("grid.parquet"), AccessKeyId: "AKIAEXAMPLE", SecretAccessKey: "s3cr3t-value"), missing));

        Assert.DoesNotContain("s3cr3t-value", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-value", refused.InnerException?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_file_over_http_is_a_table_and_answers_a_query()
    {
        using GeoParquetFolder remote = new(new RemoteGeoParquet(_server.Url("grid.parquet")), Options());

        GeoParquetTable table = Assert.Single(remote.List());

        Assert.Equal("grid", table.Name);
        Assert.Null(table.Problem);
        Assert.Equal(3857, table.Geometry.Srid);
        Assert.Equal(100, table.Rows);
        Assert.Matches("^[0-9a-f]{16}$", table.Version);

        // Only the row number: proving `objectid` unique would read the whole column over the network.
        Assert.Equal([GeoParquetFolder.RowNumberColumn], table.IdentityCandidates);

        GeoParquetFeatureSource source = new(
            remote,
            new LayerDefinition("grid", "main", "grid", "geom", 3857, GeoParquetFolder.RowNumberColumn, GeoParquetFolder.RowNumberColumn, isHosted: false),
            new ShiftingProjector());

        // ids 1..100, and `id % 3 == 1` is forest: 34 of them.
        Assert.True(WhereClause.TryParse(
            "kind = 'forest'", ["objectid", "name", "kind", "area", "day", "score"], n => $"\"{n}\"",
            out ParsedWhere forest, out string? error), error);

        Assert.Equal(34, await source.CountAsync(new FeatureQuery(1000, where: forest), CancellationToken.None));
        Assert.Equal(100, await source.CountAsync(new FeatureQuery(1000), CancellationToken.None));
    }

    [Fact]
    public void A_name_that_is_not_the_file_is_not_found()
    {
        using GeoParquetFolder remote = new(new RemoteGeoParquet(_server.Url("grid.parquet")), Options());

        Assert.Null(remote.Find("other"));
        Assert.NotNull(remote.Find("grid"));
    }

    [Fact]
    public void The_instance_reads_its_own_location_and_nothing_else()
    {
        using GeoParquetFolder remote = new(new RemoteGeoParquet(_server.Url("grid.parquet")), Options());
        using DuckDBConnection connection = remote.Open();

        Assert.Equal(100L, Scalar(connection, $"select count(*) from read_parquet('{_server.Url("grid.parquet")}')"));

        foreach (string outside in (string[])
            [
                $"select count(*) from read_parquet('{_server.Url("other.parquet")}')",
                $"select count(*) from read_parquet('{_served.File("grid.parquet").Replace('\\', '/')}')",
                "select count(*) from read_text('https://example.org/')",
                "set enable_external_access = true",
                "set allowed_directories = ['https://']",
            ])
        {
            DuckDBException refused = Assert.Throws<DuckDBException>(() => Scalar(connection, outside));
            Assert.True(
                refused.Message.Contains("disabled by configuration", StringComparison.OrdinalIgnoreCase)
                || refused.Message.Contains("locked", StringComparison.OrdinalIgnoreCase),
                $"{outside}: {refused.Message}");
        }
    }

    [Fact]
    public void Metadata_is_read_again_only_after_its_lifetime()
    {
        using GeoParquetFolder remote = new(
            new RemoteGeoParquet(_server.Url("grid.parquet")),
            Options() with { RemoteMetadataLifetime = TimeSpan.FromHours(1) });

        _ = remote.Find("grid");
        int requests = _server.Requests;

        for (int i = 0; i < 20; i++)
        {
            _ = remote.Find("grid");
        }

        Assert.Equal(requests, _server.Requests);
    }

    [Fact]
    public void A_file_whose_name_is_not_an_identifier_is_published_under_one_that_is()
    {
        // Overture's names, which the first end-to-end run found silently dropped (ADR-067).
        File.Copy(_served.File("grid.parquet"), _served.File("part-00000-3d6dbc8d-c000.zstd.parquet"));

        using GeoParquetFolder remote = new(new RemoteGeoParquet(_server.Url("part-00000-3d6dbc8d-c000.zstd.parquet")), Options());

        GeoParquetTable table = Assert.Single(remote.List());

        Assert.Equal("part_00000_3d6dbc8d_c000_zstd", table.Name);
        Assert.Null(table.Problem);
        Assert.Equal(_server.Url("part-00000-3d6dbc8d-c000.zstd.parquet"), remote.PathOf(table.Name));
        Assert.Equal(100, remote.Find(table.Name)!.Rows);
    }

    [Theory]
    [InlineData("roads", "roads")]
    [InlineData("my-roads", "my_roads")]
    [InlineData("part-00000-3d6d.zstd", "part_00000_3d6d_zstd")]
    [InlineData("2024 roads", "_2024_roads")]
    [InlineData("şehir", "_ehir")]
    public void A_table_name_is_made_of_letters_digits_and_underscores(string stem, string name) =>
        Assert.Equal(name, GeoParquetFolder.TableNameOf(stem));

    [Fact]
    public void A_table_name_is_never_longer_than_an_identifier_may_be() =>
        Assert.Equal(63, GeoParquetFolder.TableNameOf(new string('a', 200)).Length);

    [Theory]
    [InlineData("https://host/a/b/roads.parquet", "roads")]
    [InlineData("https://host/a/b/roads.parquet?x=1", "roads")]
    [InlineData("s3://bucket/prefix/places.PARQUET", "places")]
    [InlineData("https://host/a/b/", null)]
    [InlineData("https://host/a/.parquet", null)]
    [InlineData("https://host/a/roads.csv", null)]
    public void A_file_s_table_name_is_its_name_without_the_extension(string url, string? name) =>
        Assert.Equal(name, GeoParquetFolder.NameOfUrl(url));

    private static int IndexOf(IReadOnlyList<string> settings, string start)
    {
        for (int i = 0; i < settings.Count; i++)
        {
            if (settings[i].StartsWith(start, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new Xunit.Sdk.XunitException($"No statement starts with '{start}':\n" + string.Join("\n", settings));
    }

    private static object? Scalar(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>DuckDB's httpfs for this platform, downloaded once per machine.</summary>
    internal static class Extensions
    {
        private static readonly Lazy<string> Downloaded = new(Download, LazyThreadSafetyMode.ExecutionAndPublication);

        public static string Directory() => Downloaded.Value;

        private static string Download()
        {
            string version;

            using (DuckDBConnection db = new("DataSource=:memory:"))
            {
                db.Open();
                using DuckDBCommand command = db.CreateCommand();
                command.CommandText = "select version()";
                version = (string)command.ExecuteScalar()!;
            }

            string root = Path.Combine(Path.GetTempPath(), "graticula-duckdb-extensions", version);
            string target = GeoParquetFolder.HttpfsPath(root);

            if (File.Exists(target))
            {
                return root;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            string url = string.Create(CultureInfo.InvariantCulture,
                $"http://extensions.duckdb.org/{version}/{GeoParquetFolder.ExtensionPlatform}/httpfs.duckdb_extension.gz");

            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
            using Stream compressed = http.GetStreamAsync(new Uri(url)).GetAwaiter().GetResult();
            using GZipStream gzip = new(compressed, CompressionMode.Decompress);

            string partial = target + ".partial-" + Guid.NewGuid().ToString("n");

            using (FileStream file = File.Create(partial))
            {
                gzip.CopyTo(file);
            }

            File.Move(partial, target, overwrite: true);
            return root;
        }
    }

    /// <summary>A static file server on loopback that answers HEAD and ranges, which is what httpfs asks for.</summary>
    private sealed class FileServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _root;
        private readonly int _port;
        private int _requests;

        public FileServer(string root)
        {
            _root = root;
            _port = FreePort();
            _listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{_port}/"));
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public int Requests => Volatile.Read(ref _requests);

        public string Url(string file) => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{_port}/files/{file}");

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;

                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                Interlocked.Increment(ref _requests);
                _ = Task.Run(() => Answer(context));
            }
        }

        private void Answer(HttpListenerContext context)
        {
            using HttpListenerResponse response = context.Response;

            try
            {
                string name = Path.GetFileName(context.Request.Url!.AbsolutePath);
                string path = Path.Combine(_root, name);

                if (!context.Request.Url.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal) || !File.Exists(path))
                {
                    response.StatusCode = 404;
                    return;
                }

                byte[] bytes = File.ReadAllBytes(path);
                long start = 0, end = bytes.Length - 1;

                response.AddHeader("Accept-Ranges", "bytes");
                response.AddHeader("Last-Modified", File.GetLastWriteTimeUtc(path).ToString("R", CultureInfo.InvariantCulture));

                if (context.Request.Headers["Range"] is { } range && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    string[] parts = range["bytes=".Length..].Split('-');
                    start = long.Parse(parts[0], CultureInfo.InvariantCulture);
                    end = parts.Length > 1 && parts[1].Length > 0 ? Math.Min(long.Parse(parts[1], CultureInfo.InvariantCulture), end) : end;
                    response.StatusCode = 206;
                    response.AddHeader("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes {start}-{end}/{bytes.Length}"));
                }

                response.ContentLength64 = end - start + 1;

                if (context.Request.HttpMethod != "HEAD")
                {
                    response.OutputStream.Write(bytes, (int)start, (int)(end - start + 1));
                }
            }
            catch (Exception e) when (e is IOException or HttpListenerException or FormatException)
            {
                response.StatusCode = 500;
            }
        }

        private static int FreePort()
        {
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
    }
}
