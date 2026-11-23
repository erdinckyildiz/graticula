using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Platform.Schema;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-318, D-391: a server that refuses to serve stays up, says why on every request, and comes into service by itself
/// when the reason goes — <b>a real process</b>, because what is tested is that the process does not exit.
/// </summary>
/// <remarks>
/// Before ADR-318 each of these processes exited (1, or 78 for configuration), so <c>WaitRefusingAsync</c> found nothing
/// listening and answered null, and every assertion below failed on its first line.
/// </remarks>
[Trait("Needs", "TwoNodes")]
public sealed class ARefusingServerStaysUpTests
{
    /// <summary>
    /// Condition 1: against an un-migrated store the server stays up, answers its three health routes and a data route
    /// 503 with the state, is still running twenty seconds later, and serves without a restart once <c>migrate --apply</c>
    /// is run from another process.
    /// </summary>
    [Fact]
    public async Task Against_an_unmigrated_store_it_stays_up_says_why_and_serves_once_migrated()
    {
        TwoNodeSite site = new(build: null, migrate: false);
        await site.InitializeAsync();

        try
        {
            TwoNodeSite.Node node = site.Launched("refuser");

            string? state = await node.WaitRefusingAsync(TimeSpan.FromSeconds(90));
            Assert.True(state == "store-not-initialised", $"it refused as {state ?? "nothing"}:\n{node.Log}");

            (int live, string liveBody) = await GetAsync(node, "/healthz/live");
            Assert.Equal(503, live);
            Assert.Equal("refusing", Json(liveBody).GetProperty("status").GetString());
            Assert.Contains("migration", Json(liveBody).GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

            (int ready, string readyBody) = await GetAsync(node, "/healthz/ready");
            Assert.Equal(503, ready);
            Assert.Equal("store-not-initialised", Json(readyBody).GetProperty("state").GetString());

            (int health, string healthBody) = await GetAsync(node, "/rest/info/healthcheck?f=json");
            Assert.Equal(503, health);
            Assert.False(Json(healthBody).GetProperty("success").GetBoolean());
            Assert.Equal("store-not-initialised", Json(healthBody).GetProperty("error").GetProperty("details")[0].GetString());

            (int data, string dataBody) = await GetAsync(node, "/rest/services?f=json");
            Assert.Equal(503, data);
            JsonElement error = Json(dataBody).GetProperty("error");
            Assert.Equal(503, error.GetProperty("code").GetInt32());
            Assert.False(Json(dataBody).TryGetProperty("services", out _), dataBody);

            await Task.Delay(TimeSpan.FromSeconds(20));
            Assert.True(node.Running, $"the refusing server exited:\n{node.Log}");

            (int migrated, string said) = await site.RunAsync(["migrate", "--apply", "--no-backup"]);
            Assert.True(migrated == 0, said);

            // The same process, not a new one: it re-checks every ten seconds and starts serving.
            await node.WaitLiveAsync(TimeSpan.FromSeconds(60));
            Assert.True(node.Running);
            Assert.Contains("has cleared", node.Log, StringComparison.Ordinal);
            // Serving now, as a first start serves: a store with no administrator answers its own setup refusal, not ours.
            Assert.DoesNotContain("refusing: ", (await GetAsync(node, "/rest/info?f=json")).Body, StringComparison.Ordinal);
        }
        finally
        {
            await site.DisposeAsync();
        }
    }

    /// <summary>
    /// A store older than this build — the migration not run — is refused as <c>store-not-migrated</c>, and the re-check
    /// takes it up when the store moves under it.
    /// </summary>
    [Fact]
    public async Task A_store_older_than_the_build_is_refused_as_not_migrated_until_it_is()
    {
        TwoNodeSite site = new();
        await site.InitializeAsync();

        try
        {
            int mine = PlatformMigrations.ComponentSchemaVersion.Value;
            await site.ExecuteAsync($"update platform_schema set applied_version = {mine - 1}");

            TwoNodeSite.Node node = site.Launched("behind");
            string? state = await node.WaitRefusingAsync(TimeSpan.FromSeconds(90));
            Assert.True(state == "store-not-migrated", $"it refused as {state ?? "nothing"}:\n{node.Log}");
            Assert.Contains("The migration has not been run", (await GetAsync(node, "/healthz/ready")).Body, StringComparison.Ordinal);

            await site.ExecuteAsync($"update platform_schema set applied_version = {mine}");
            await node.WaitLiveAsync(TimeSpan.FromSeconds(60));
            Assert.True(node.Running);
        }
        finally
        {
            await site.DisposeAsync();
        }
    }

    /// <summary>Condition 2: a missing setting is said on the port too, and the process stays up.</summary>
    [Fact]
    public async Task A_missing_setting_is_said_on_the_port_and_the_process_stays_up()
    {
        TwoNodeSite site = new(build: null, migrate: false);
        await site.InitializeAsync();

        try
        {
            TwoNodeSite.Node node = site.Launched("unkeyed", new Dictionary<string, string> { ["Graticula__SecretKey"] = string.Empty });

            string? state = await node.WaitRefusingAsync(TimeSpan.FromSeconds(60));
            Assert.True(state == "configuration", $"it refused as {state ?? "nothing"}:\n{node.Log}");

            (int status, string body) = await GetAsync(node, "/rest/services?f=json");
            Assert.Equal(503, status);
            Assert.Contains("SecretKey", Json(body).GetProperty("error").GetProperty("message").GetString()!, StringComparison.Ordinal);

            await Task.Delay(TimeSpan.FromSeconds(12));
            Assert.True(node.Running, $"the refusing server exited:\n{node.Log}");
        }
        finally
        {
            await site.DisposeAsync();
        }
    }

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    private static async Task<(int Status, string Body)> GetAsync(TwoNodeSite.Node node, string path)
    {
        using HttpResponseMessage answer = await node.Http.GetAsync(path);
        return ((int)answer.StatusCode, await answer.Content.ReadAsStringAsync());
    }
}
