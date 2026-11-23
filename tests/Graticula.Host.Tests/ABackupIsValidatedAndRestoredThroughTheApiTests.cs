using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-317: a backup validated, and restored, through the admin API of a running server — <b>a real process over a database
/// of the test's own</b>, because a restore renames the whole database and the shared one is every other suite's.
/// </summary>
/// <remarks>
/// Needs <c>GRATICULA_TEST_PG</c> as a login that may create databases, and the PostgreSQL client tools: on the path, or in
/// <c>GRATICULA_TEST_PG_TOOLS</c>.
/// </remarks>
[Trait("Needs", "TwoNodes")]
public sealed class ABackupIsValidatedAndRestoredThroughTheApiTests
{
    /// <summary>
    /// Condition 1: a good backup validates — counted, its scratch database gone — and a corrupted one fails naming the
    /// part, creating nothing.
    /// </summary>
    [Fact]
    public async Task A_good_backup_validates_and_a_corrupted_one_is_refused_by_name()
    {
        await using Deployment deployment = await Deployment.StartAsync(failAt: null);
        string backup = await deployment.BackUpAsync();

        JsonElement good = await deployment.RunAsync($"/admin/backups/{backup}/validate", null);
        Assert.True(good.GetProperty("state").GetString() == "succeeded", good.ToString());
        Assert.True(good.GetProperty("result").GetProperty("services").GetInt64() >= 0, good.ToString());
        Assert.Equal(0L, await deployment.DatabasesLikeAsync("_validate_"));

        // One byte of the dump changed.
        string dump = Path.Combine(deployment.Backups, backup, "database.dump");
        byte[] bytes = await File.ReadAllBytesAsync(dump);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(dump, bytes);

        JsonElement bad = await deployment.RunAsync($"/admin/backups/{backup}/validate", null);
        Assert.Equal("failed", bad.GetProperty("state").GetString());
        Assert.Contains("database.dump", bad.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Equal(0L, await deployment.DatabasesLikeAsync("_validate_"));
    }

    /// <summary>
    /// Condition 2: a restore round trip — what was made after the backup is gone, the server serves again, and the
    /// replaced database is kept under the name the operation gives.
    /// </summary>
    [Fact]
    public async Task A_restore_through_the_api_brings_the_backup_back_and_keeps_the_old_database()
    {
        await using Deployment deployment = await Deployment.StartAsync(failAt: null);
        string backup = await deployment.BackUpAsync();
        await deployment.Site.ExecuteAsync("create table zz_after_the_backup (id int)");

        (int refused, _) = await deployment.Node.SendAsync(HttpMethod.Post, $"/admin/backups/{backup}/restore", deployment.Token, new { });
        Assert.Equal(400, refused);

        JsonElement done = await deployment.RunAsync($"/admin/backups/{backup}/restore", new { confirm = backup });
        Assert.True(done.GetProperty("state").GetString() == "succeeded", done.ToString());
        Assert.True(deployment.SawRestoring, "readiness never answered 503 'restoring' during the restore");

        string kept = done.GetProperty("result").GetProperty("keptDatabase").GetString()!;
        Assert.Equal(1L, await deployment.DatabasesLikeAsync(kept));
        deployment.Keep(kept);

        NpgsqlConnection.ClearAllPools();
        Assert.True(await deployment.Site.ScalarAsync("select to_regclass('zz_after_the_backup')::text") is null or DBNull, "the table made after the backup is still there");
        Assert.Equal(0L, await deployment.DatabasesLikeAsync("_restore_"));

        await deployment.WaitReadyAsync();
        (int services, string body) = await deployment.Node.SendAsync(HttpMethod.Get, "/rest/services?f=json", null);
        Assert.True(services == 200, body);
        Assert.Contains(done.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()), n => n == "switch");
    }

    /// <summary>
    /// Condition 3: a failure injected between the two renames leaves the old database serving, named as it was, with
    /// what was made after the backup still in it.
    /// </summary>
    [Fact]
    public async Task A_failure_in_the_switch_leaves_the_old_database_serving()
    {
        await using Deployment deployment = await Deployment.StartAsync(failAt: "switch");
        string backup = await deployment.BackUpAsync();
        await deployment.Site.ExecuteAsync("create table zz_after_the_backup (id int)");

        JsonElement failed = await deployment.RunAsync($"/admin/backups/{backup}/restore", new { confirm = backup });
        Assert.True(failed.GetProperty("state").GetString() == "failed", failed.ToString());
        Assert.True(failed.GetProperty("result").GetProperty("failedAt").GetString() == "switch", failed.ToString());

        NpgsqlConnection.ClearAllPools();
        Assert.Equal("zz_after_the_backup", await deployment.Site.ScalarAsync("select to_regclass('zz_after_the_backup')::text"));
        Assert.Equal(0L, await deployment.DatabasesLikeAsync("_restore_"));
        Assert.Equal(0L, await deployment.DatabasesLikeAsync("_before_restore_"));

        await deployment.WaitReadyAsync();
        (int services, string body) = await deployment.Node.SendAsync(HttpMethod.Get, "/rest/services?f=json", null);
        Assert.True(services == 200, body);
    }

    /// <summary>
    /// Condition 5: a site of two nodes restored through one of them — the other closes too, answering 503 'restoring', and
    /// both come back on the restored database.
    /// </summary>
    [Fact]
    public async Task A_restore_through_one_node_of_a_site_closes_and_brings_back_both()
    {
        await using Deployment deployment = await Deployment.StartAsync(failAt: null, nodes: 2);
        string backup = await deployment.BackUpAsync();
        await deployment.Site.ExecuteAsync("create table zz_after_the_backup (id int)");

        JsonElement done = await deployment.RunAsync($"/admin/backups/{backup}/restore", new { confirm = backup });
        Assert.True(done.GetProperty("state").GetString() == "succeeded", done.ToString());
        deployment.Keep(done.GetProperty("result").GetProperty("keptDatabase").GetString()!);
        Assert.True(deployment.SecondSawRestoring, "the other node never answered 503 'restoring' during the restore");

        await deployment.WaitReadyAsync();
        await deployment.WaitReadyAsync(deployment.Second);

        NpgsqlConnection.ClearAllPools();
        Assert.True(await deployment.Site.ScalarAsync("select to_regclass('zz_after_the_backup')::text") is null or DBNull);
        (int services, string body) = await deployment.Second!.SendAsync(HttpMethod.Get, "/rest/services?f=json", null);
        Assert.True(services == 200, body);
    }

    /// <summary>A server over a database of its own, signed in.</summary>
    private sealed class Deployment : IAsyncDisposable
    {
        private readonly List<string> _databases = [];
        private readonly string _admin;

        private Deployment(string name, string admin, TwoNodeSite site, string backups)
        {
            Name = name;
            _admin = admin;
            Site = site;
            Backups = backups;
            _databases.Add(name);
        }

        public string Name { get; }

        public TwoNodeSite Site { get; }

        public string Backups { get; }

        public TwoNodeSite.Node Node { get; private set; } = null!;

        /// <summary>The site's second node, when it was started with two.</summary>
        public TwoNodeSite.Node? Second { get; private set; }

        /// <summary>Whether the second node's readiness was seen answering 503 'restoring' while an operation ran.</summary>
        public bool SecondSawRestoring { get; private set; }

        public string Token { get; private set; } = null!;

        /// <summary>Whether readiness was seen answering 503 'restoring' while an operation ran.</summary>
        public bool SawRestoring { get; private set; }

        public static async Task<Deployment> StartAsync(string? failAt, int nodes = 1)
        {
            string shared = Environment.GetEnvironmentVariable("GRATICULA_TEST_PG") is { Length: > 0 } pg
                ? pg
                : throw new InvalidOperationException("GRATICULA_TEST_PG is not set, so these tests FAIL rather than skip.");
            string name = "zz_dr_" + Guid.NewGuid().ToString("N")[..10];
            string admin = new NpgsqlConnectionStringBuilder(shared) { Database = "postgres", SearchPath = null }.ConnectionString;

            await using (NpgsqlConnection connection = new(admin))
            {
                await connection.OpenAsync();
                await using NpgsqlCommand create = new($"create database {name} template template0", connection);
                await create.ExecuteNonQueryAsync();
            }

            string own = new NpgsqlConnectionStringBuilder(shared) { Database = name, SearchPath = null }.ConnectionString;
            await using (NpgsqlConnection connection = new(own))
            {
                await connection.OpenAsync();
                await using NpgsqlCommand postgis = new("create extension if not exists postgis", connection);
                await postgis.ExecuteNonQueryAsync();
            }

            string backups = Path.Combine(Path.GetTempPath(), name + "-backups");
            Directory.CreateDirectory(backups);

            TwoNodeSite site = new(build: null, migrate: true, database: own);
            Deployment deployment = new(name, admin, site, backups);
            await site.InitializeAsync();

            Dictionary<string, string> settings = new() { ["Graticula__BackupPath"] = backups };
            if (nodes > 1)
            {
                settings["Graticula__Nodes"] = nodes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (Environment.GetEnvironmentVariable("GRATICULA_TEST_PG_TOOLS") is { Length: > 0 } tools)
            {
                settings["Graticula__PostgresTools"] = tools;
            }

            if (failAt is not null)
            {
                settings["GRATICULA_TEST_RESTORE_FAIL_AT"] = failAt;
            }

            deployment.Node = await site.StartAsync("dr-a", settings);
            if (nodes > 1)
            {
                deployment.Second = await site.StartAsync("dr-b", settings);
            }
            deployment.Token = await deployment.Node.SignInAsync();
            return deployment;
        }

        /// <summary>Takes a backup through the API and answers its name.</summary>
        public async Task<string> BackUpAsync()
        {
            (int started, string said) = await Node.SendAsync(HttpMethod.Post, "/admin/backups", Token);
            Assert.True(started == 202, said);
            Stopwatch clock = Stopwatch.StartNew();
            string list = string.Empty;

            while (clock.Elapsed < TimeSpan.FromMinutes(3))
            {
                await Task.Delay(500);
                (int status, list) = await Node.SendAsync(HttpMethod.Get, "/admin/backups", Token);

                if (status != 200)
                {
                    continue;
                }

                JsonElement listed = JsonDocument.Parse(list).RootElement;

                if (listed.GetProperty("backups").GetArrayLength() > 0)
                {
                    return listed.GetProperty("backups")[0].GetProperty("name").GetString()!;
                }

                if (listed.GetProperty("last") is { ValueKind: JsonValueKind.Object } last
                    && last.GetProperty("error").GetString() is { } error)
                {
                    Assert.Fail("The backup failed: " + error);
                }
            }

            throw new TimeoutException("No backup within three minutes: " + list);
        }

        /// <summary>Starts an operation and follows its status — without a token, as a closed gate answers it — until it ends.</summary>
        public async Task<JsonElement> RunAsync(string path, object? body)
        {
            (int started, string said) = await Node.SendAsync(HttpMethod.Post, path, Token, body ?? new { });
            Assert.True(started == 202, said + Environment.NewLine + Node.Log[Math.Max(0, Node.Log.Length - 4000)..]);
            string id = JsonDocument.Parse(said).RootElement.GetProperty("id").GetString()!;
            Stopwatch clock = Stopwatch.StartNew();

            while (clock.Elapsed < TimeSpan.FromMinutes(5))
            {
                await Task.Delay(300);

                try
                {
                    (int ready, string readyBody) = await Node.SendAsync(HttpMethod.Get, "/healthz/ready", null);
                    if (ready == 503 && readyBody.Contains("\"restoring\"", StringComparison.Ordinal))
                    {
                        SawRestoring = true;
                    }

                    if (Second is not null)
                    {
                        (int other, string otherBody) = await Second.SendAsync(HttpMethod.Get, "/healthz/ready", null);
                        if (other == 503 && otherBody.Contains("\"restoring\"", StringComparison.Ordinal))
                        {
                            SecondSawRestoring = true;
                        }
                    }

                    (int status, string answer) = await Node.SendAsync(HttpMethod.Get, $"/admin/backups/operations/{id}", Token);

                    if (status == 200 && JsonDocument.Parse(answer).RootElement is { } operation
                        && operation.GetProperty("state").GetString() != "running")
                    {
                        return operation.Clone();
                    }
                }
                catch (HttpRequestException)
                {
                    // The node's connections are being replaced; ask again.
                }
            }

            throw new TimeoutException($"Operation {id} did not end within five minutes.");
        }

        /// <summary>How many databases of this cluster are named like this deployment's with <paramref name="part"/>.</summary>
        public async Task<long> DatabasesLikeAsync(string part)
        {
            await using NpgsqlConnection connection = new(_admin);
            await connection.OpenAsync();
            await using NpgsqlCommand count = new("select count(*) from pg_database where datname like @like", connection);
            count.Parameters.AddWithValue("like", (part.StartsWith(Name, StringComparison.Ordinal) ? part : Name + part) + "%");
            return (long)(await count.ExecuteScalarAsync())!;
        }

        /// <summary>Waits until the node — or the one given — answers ready again.</summary>
        public async Task WaitReadyAsync(TwoNodeSite.Node? which = null)
        {
            TwoNodeSite.Node node = which ?? Node;
            Stopwatch clock = Stopwatch.StartNew();
            string last = string.Empty;

            while (clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                (int status, string body) = await node.SendAsync(HttpMethod.Get, "/healthz/ready", null);
                if (status == 200)
                {
                    return;
                }

                last = body;
                await Task.Delay(500);
            }

            Assert.Fail("The node did not answer ready after the restore: " + last + Environment.NewLine + node.Log);
        }

        /// <summary>A database the restore kept, dropped at the end.</summary>
        public void Keep(string database) => _databases.Add(database);

        public async ValueTask DisposeAsync()
        {
            Node?.Kill();
            Second?.Kill();
            NpgsqlConnection.ClearAllPools();

            await using NpgsqlConnection connection = new(_admin);
            await connection.OpenAsync();

            foreach (string database in _databases)
            {
                await using NpgsqlCommand drop = new($"drop database if exists \"{database}\" with (force)", connection);
                await drop.ExecuteNonQueryAsync();
            }

            try
            {
                Directory.Delete(Backups, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
