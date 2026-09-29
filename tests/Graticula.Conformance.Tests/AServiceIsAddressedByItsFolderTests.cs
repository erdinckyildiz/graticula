using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Two services with one name, in two folders, are two services to every administrative route.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-275](../../docs/architecture-debt.md).</b> A service's name is unique within its folder and
/// nowhere else, and the style, sprite and reference routes addressed a service by name alone. The
/// handler looked the name up, took the first row, asked whether the caller owned <em>that</em>
/// service, and then wrote every service of the name in every folder. So restyling
/// <c>zz_d275_elsewhere/zz_d275_twin</c> restyled the root's <c>zz_d275_twin</c> too — and a publisher
/// who owned one of them could write the other.
/// </para>
/// <para>
/// <b>Staged with two owners, because one owner cannot show the second half.</b> The suite's
/// administrator publishes the service in the folder, and a publisher this file creates and removes
/// publishes the one at the root. Both are private. The root service is the publisher's, which is the
/// shape the defect needed: the name reached a service the caller did not own.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection</b>, because it publishes and removes services and a walker
/// outside it would see them mid-change — [D-75](../../docs/architecture-debt.md). The names carry the
/// fixture prefix, so the walks skip them anyway.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AServiceIsAddressedByItsFolderTests : ArcGisClient, IAsyncLifetime
{
    private const string Twin = "zz_d275_twin";

    private const string Elsewhere = "zz_d275_elsewhere";

    private const string AdministratorsLayer = "zz_d275_administrators";

    private const string PublishersLayer = "zz_d275_publishers";

    private const string Publisher = "zz_d275_publisher";

    /// <summary>Made per run, for the reason <c>PublishOwnershipTests</c> gives.</summary>
    private static readonly string Password = $"Zz-{Guid.NewGuid():N}-1";

    private string _root = string.Empty;

    private string _administrator = string.Empty;

    private string _publisher = string.Empty;

    public async Task InitializeAsync()
    {
        _root = await RequireServerAsync();
        _administrator = await TokenAsync(_root)
            ?? throw new InvalidOperationException("these tests need an administrator's token");

        await TearDownAsync();

        (int status, string body) = await PublishOneAsync(Twin, AdministratorsLayer, Elsewhere);

        Assert.True(
            status is 200 or 201,
            $"The administrator could not publish '{Elsewhere}/{Twin}': {status} {body}");

        _publisher = await SignInAsPublisherAsync();

        (HttpStatusCode published, string said) = await PublishAsPublisherAsync();

        Assert.True(
            published is HttpStatusCode.OK or HttpStatusCode.Created,
            $"The publisher could not publish '{Twin}' at the root: {(int)published} {said}. Without "
            + "a second owner this test cannot tell the repair from the defect.");
    }

    public Task DisposeAsync() => TearDownAsync();

    /// <summary>
    /// Restyling the service in one folder leaves the same-named service at the root alone.
    /// </summary>
    [Fact]
    public async Task Restyling_one_folders_service_leaves_the_same_name_elsewhere_alone()
    {
        (HttpStatusCode put, string said) = await SendAsync(
            _administrator, HttpMethod.Put, $"/admin/services/{Twin}/style?folder={Elsewhere}",
            Style(AdministratorsLayer));

        Assert.True(
            put == HttpStatusCode.OK,
            $"Restyling '{Elsewhere}/{Twin}' answered {(int)put}: {said}. A 400 naming a missing "
            + "source layer means the route looked up the root's service instead: D-275.");

        Assert.True(
            await HasNoStyleAsync(null),
            $"Restyling '{Elsewhere}/{Twin}' stored a style on the root's '{Twin}' as well — a "
            + "name-only write, which is D-275.");

        Assert.False(
            await HasNoStyleAsync(Elsewhere),
            $"'{Elsewhere}/{Twin}' answered 200 to a restyle and has no style stored.");
    }

    /// <summary>
    /// A publisher who owns the service at the root cannot write the one in the folder, and writing
    /// their own does not reach it.
    /// </summary>
    [Fact]
    public async Task A_publisher_who_owns_one_cannot_write_the_other()
    {
        foreach ((HttpMethod method, string path, string? body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Put, $"/admin/services/{Twin}/style?folder={Elsewhere}", Style(AdministratorsLayer)),
            (HttpMethod.Delete, $"/admin/services/{Twin}/style?folder={Elsewhere}", null),
            (HttpMethod.Delete, $"/admin/services/{Twin}/sprite?folder={Elsewhere}", null),

            // ADR-093: a seed is started, counted and read by folder and name like the rest, and the
            // read-back of a private service the caller cannot read is a 404 like any other read.
            (HttpMethod.Post, $"/admin/services/{Twin}/cache/seeds?folder={Elsewhere}", "{\"minZoom\":0,\"maxZoom\":0}"),
            (HttpMethod.Post, $"/admin/services/{Twin}/cache/seeds?folder={Elsewhere}&dryRun=true", "{}"),
            (HttpMethod.Get, $"/admin/services/{Twin}/cache?folder={Elsewhere}", null),
        })
        {
            (HttpStatusCode status, string said) = await SendAsync(_publisher, method, path, body);

            Assert.True(
                status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                $"{method} {path} as a publisher who does not own it answered {(int)status}: {said}");
        }

        (HttpStatusCode own, string answer) = await SendAsync(
            _publisher, HttpMethod.Put, $"/admin/services/{Twin}/style", Style(PublishersLayer));

        Assert.True(
            own == HttpStatusCode.OK,
            $"The publisher restyling their own '{Twin}' at the root answered {(int)own}: {answer}. "
            + "A 403 means the route checked the other folder's service: D-275.");

        Assert.True(
            await HasNoStyleAsync(Elsewhere),
            $"The publisher restyled their own '{Twin}' and '{Elsewhere}/{Twin}' — the "
            + "administrator's — has a style now too. The ownership check approved one service and the "
            + "write reached both: D-275.");
    }

    /// <summary>A style drawing one source layer, so the server's check against the service passes.</summary>
    private static string Style(string sourceLayer) => JsonSerializer.Serialize(new
    {
        version = 8,
        sources = new { esri = new { type = "vector", url = "../../" } },
        layers = new[]
        {
            new Dictionary<string, string>
            {
                ["id"] = "a",
                ["type"] = "fill",
                ["source"] = "esri",
                ["source-layer"] = sourceLayer,
            },
        },
    });

    /// <summary>
    /// Whether the service at that address has no style stored, read as the administrator.
    /// </summary>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <returns>True when it answers the generated-style wrapper.</returns>
    private async Task<bool> HasNoStyleAsync(string? folder)
    {
        (HttpStatusCode status, string body) = await SendAsync(
            _administrator, HttpMethod.Get,
            $"/admin/services/{Twin}/style?folder={Uri.EscapeDataString(folder ?? string.Empty)}", null);

        Assert.True(status == HttpStatusCode.OK, $"Reading the style of '{folder}/{Twin}' answered {(int)status}: {body}");

        // A stored style comes back as the document itself; none stored comes back as a wrapper
        // saying so. The wrapper is the only body with a `stored` member.
        return JsonDocument.Parse(body).RootElement.TryGetProperty("stored", out JsonElement stored)
            && !stored.GetBoolean();
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string token, HttpMethod method, string path, string? json)
    {
        using HttpRequestMessage request = new(method, $"{_root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The publisher publishes their own service of the same name, at the root.</summary>
    /// <remarks>
    /// <b>The administrator finds the table and the publisher publishes it</b>, as
    /// <c>MyContentIsWhatTheCallerOwnsTests</c> does: a publisher cannot list data sources, and a
    /// service published with the administrator's token would be the administrator's.
    /// </remarks>
    private async Task<(HttpStatusCode Status, string Body)> PublishAsPublisherAsync()
    {
        string? datastore = await DatastoreIdAsync();

        Assert.False(datastore is null, "No datastore data source is registered.");

        (string Schema, string Table, string Geometry, string Type, string Identity, int Srid)? table =
            await FreeTableAsync();

        Assert.True(table is not null, "The fixture has no table that nothing publishes.");

        return await SendAsync(
            _publisher, HttpMethod.Post, "/admin/publish",
            JsonSerializer.Serialize(new
            {
                name = Twin,
                folder = (string?)null,
                sharing = "private",
                nodes = new[]
                {
                    new
                    {
                        layer = new
                        {
                            name = PublishersLayer,
                            dataSourceId = datastore,
                            schemaName = table!.Value.Schema,
                            tableName = table.Value.Table,
                            geometryColumn = table.Value.Geometry,
                            geometryType = table.Value.Type,
                            identityColumn = table.Value.Identity,
                            srid = table.Value.Srid,
                        },
                    },
                },
            }));
    }

    /// <summary>Makes the publisher and signs in as them, replacing the issued password.</summary>
    private async Task<string> SignInAsPublisherAsync()
    {
        (HttpStatusCode created, string body) = await SendAsync(
            _administrator, HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(new { name = Publisher, role = "publisher", userType = "creator" }));

        Assert.True(
            created is HttpStatusCode.OK or HttpStatusCode.Created,
            $"Creating the probe publisher answered {(int)created}: {body}");

        string issued = JsonDocument.Parse(body).RootElement.GetProperty("password").GetString()!;
        string first = await SignInAsync(issued);

        (HttpStatusCode changed, string why) = await SendAsync(
            first, HttpMethod.Post, "/rest/auth/password",
            JsonSerializer.Serialize(new { currentPassword = issued, newPassword = Password }));

        Assert.True(changed == HttpStatusCode.OK, $"The probe publisher could not set its password: {why}");

        return await SignInAsync(Password);
    }

    private async Task<string> SignInAsync(string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{_root}/rest/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { name = Publisher, password }), Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"The probe publisher could not sign in: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>Removes both services, the folder and the publisher, whatever a last run left.</summary>
    private async Task TearDownAsync()
    {
        if (_administrator.Length == 0)
        {
            return;
        }

        await UnpublishAsync(Twin, AdministratorsLayer, Elsewhere);
        await SendAsync(_administrator, HttpMethod.Delete, $"/admin/members/{Publisher}?deleteOwned=true", null);
        await UnpublishAsync(Twin, PublishersLayer);
        await SendAsync(_administrator, HttpMethod.Delete, $"/admin/folders/{Elsewhere}", null);
    }
}
