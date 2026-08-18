using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A service's sharing scope, set on the service and reachable from a service with no layers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner, 2026-08-18:</b> *"server tarafında sharing mekanizması çok işlemiyor."* Measured, and
/// they were right for a sharper reason than the screen: `PUT /admin/services/{name}/sharing` served
/// only *system* services, so a path spelled `/admin/services/…` answered 404 for every ordinary
/// service on the server. A scope was reachable only through `PUT /admin/layers/{name}/sharing`,
/// which writes the owning service's column — so a service with three layers had three routes to
/// one setting ([D-61]'s shape), and an **empty** service had none at all.
/// </para>
/// <para>
/// <b>The empty-service case is the one worth a test of its own.</b> Server creates empty services
/// and an empty service is created `private`, so before this the console could create a thing whose
/// sharing nothing on the server could change. That is not a screen problem and no UI test would
/// have found it.
/// </para>
/// </remarks>
public sealed class ServiceSharingPageTests : ConsoleTest
{
    private const string Probe = "zz_sharing_probe";

    /// <summary>The three scopes ADR-018 §3b defines.</summary>
    private static readonly string[] Scopes = ["private", "organization", "public"];

    /// <summary>
    /// A service with no layers can have its sharing scope set.
    /// </summary>
    [Fact]
    public async Task An_empty_service_can_be_shared()
    {
        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/featureservices",
            JsonSerializer.Serialize(new
            {
                name = Probe,
                folder = "hosted",
                description = "Created by ServiceSharingPageTests; removed when it finishes.",
            }));

        Assert.True(made is 200 or 201, $"Could not create the probe service: {made} {why}");

        try
        {
            // <b>Created private, which is the safe default and the reason this mattered.</b>
            // ADR-018 §3b: a fresh thing publishes nothing to the unauthenticated.
            (int got, string listing) = await AdminAsync(HttpMethod.Get, "/admin/featureservices");
            Assert.Equal(200, got);
            Assert.Contains(Probe, listing, StringComparison.Ordinal);

            foreach (string scope in Scopes)
            {
                (int status, string body) = await AdminAsync(
                    HttpMethod.Put,
                    $"/admin/services/{Probe}/sharing?folder=hosted",
                    JsonSerializer.Serialize(new { sharing = scope }));

                Assert.True(
                    status == 200,
                    $"Setting an empty service's scope to '{scope}' returned {status}: {body}. "
                    + "Before 2026-08-18 this was 404 for every ordinary service, and there was no "
                    + "other route at all for a service with no layers.");

                using JsonDocument answer = JsonDocument.Parse(body);

                Assert.Equal(scope, answer.RootElement.GetProperty("to").GetString());

                // <b>The scope it replaced, which is the half a `returning` clause cannot give.</b>
                // `returning` yields the updated row, so the old value comes from a subquery on the
                // statement's snapshot — and an audit record that reports the new value twice is
                // worse than one that reports nothing.
                Assert.False(
                    string.IsNullOrEmpty(answer.RootElement.GetProperty("from").GetString()),
                    "The response did not say what the scope was before, so the audit record "
                    + "cannot either.");
            }
        }
        finally
        {
            await AdminAsync(
                HttpMethod.Delete, $"/admin/featureservices/{Probe}?folder=hosted");
        }
    }

    /// <summary>
    /// The folder is part of the address, because a service name is not unique.
    /// </summary>
    /// <remarks>
    /// <b>The layer route sidestepped this and this one cannot.</b> Layer names are unique across
    /// the server; two folders may each hold a service called <c>parcels</c>. A handler that ignored
    /// the folder would share the wrong one, silently, and the wrong direction of that mistake is
    /// making somebody else's private service public.
    /// </remarks>
    [Fact]
    public async Task The_wrong_folder_is_refused_rather_than_matched()
    {
        (string Folder, string[] Services)[] folders = await FoldersWithServicesAsync();

        string? at = null;
        string? service = null;

        foreach ((string folder, string[] services) in folders)
        {
            if (folder.Length > 0 && services.Length > 0)
            {
                at = folder;
                service = services[0];
                break;
            }
        }

        Assert.False(
            service is null,
            "No service in any named folder, so the folder cannot be shown to matter. This fails "
            + "rather than skips.");

        string bare = service!.Contains('/', StringComparison.Ordinal)
            ? service[(service.LastIndexOf('/') + 1)..]
            : service;

        (int status, string body) = await AdminAsync(
            HttpMethod.Put,
            $"/admin/services/{Uri.EscapeDataString(bare)}/sharing?folder=zz_no_such_folder",
            JsonSerializer.Serialize(new { sharing = "public" }));

        Assert.Equal(404, status);

        // The refusal has to name the folder it looked in, or an operator retries the same call.
        Assert.Contains("zz_no_such_folder", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sharing is on the service's pages, not on each of its layers'.
    /// </summary>
    /// <remarks>
    /// <b>This is D-61's repair reaching the setting it missed.</b> D-61 moved Capabilities and
    /// Limits off the layer pages because their columns are on `service`. `service.sharing` is also
    /// on `service` — it is the column the serving path reads — and Sharing stayed a layer page
    /// anyway, so one setting had as many screens as the service had layers.
    /// </remarks>
    [Fact]
    public async Task Sharing_is_a_service_page_and_shows_the_current_scope()
    {
        (string token, _) = await SignInAsync();

        string address = await OpenFolderHoldingAsync("tr[data-service]", token);

        Assert.NotEmpty(address);

        string qualified = await Browser.EvaluateAsync<string>(
            "document.querySelector('tr[data-service]')?.dataset.service || ''") ?? string.Empty;

        Assert.NotEmpty(qualified);

        await OpenAsync(
            "/studio/#/service/" + string.Join(
                "/", Array.ConvertAll(qualified.Split('/'), Uri.EscapeDataString)),
            token);

        await WaitForAsync(
            "!!document.getElementById('capSharing')",
            "The service has no Sharing control on its own pages, so a service with no layers has "
            + "nowhere to set its scope and a service with three has three places.");

        // <b>It shows the scope the service actually has.</b> A select that opens on its first
        // option regardless is a control that reports `private` for a public service — and somebody
        // who trusts it and presses nothing has been told the wrong thing.
        string chosen = await Browser.EvaluateAsync<string>(
            "document.getElementById('capSharing')?.value || ''") ?? string.Empty;

        Assert.Contains(chosen, Scopes);

        (int status, string listing) = await AdminAsync(HttpMethod.Get, "/admin/featureservices");
        Assert.Equal(200, status);

        using JsonDocument services = JsonDocument.Parse(listing);

        string wantedName = qualified.Contains('/', StringComparison.Ordinal)
            ? qualified[(qualified.LastIndexOf('/') + 1)..]
            : qualified;

        string? real = null;

        foreach (JsonElement row in services.RootElement.GetProperty("services").EnumerateArray())
        {
            if (row.TryGetProperty("name", out JsonElement n)
                && string.Equals(n.GetString(), wantedName, StringComparison.OrdinalIgnoreCase)
                && row.TryGetProperty("sharing", out JsonElement sc))
            {
                real = sc.GetString();
                break;
            }
        }

        Assert.False(real is null, $"The catalogue does not report a scope for '{wantedName}'.");

        Assert.Equal(
            real!.ToLower(CultureInfo.InvariantCulture),
            chosen.ToLower(CultureInfo.InvariantCulture));

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }
}
