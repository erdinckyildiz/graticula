using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A face somebody turned off is refused the way a service that is not there is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-031](../../docs/adr/ADR-031-service-capability-configuration.md) condition 2.</b>
/// *"Turning a face off is tested to produce the same refusal as absent, not a distinguishable
/// one, so that the capability configuration cannot be used to enumerate what exists."* The
/// configuration is administrative and the refusal is public, so a refusal that said *this
/// service exists and its feature face is off* would answer, for anonymous callers, the question
/// the 404 for a private service exists to refuse.
/// </para>
/// <para>
/// <b>Bodies, not statuses.</b> <c>WfsConformanceTests</c> already asserts the ArcGIS door
/// answers 404 when the feature face is off, and that assertion would still pass against a
/// refusal reading *the feature face of 'x' is turned off* — same status, and the fact given
/// away anyway. So this compares the whole response with the response for a name that does not
/// exist, after replacing each one's own service name with a placeholder: the name is the half a
/// caller supplied and everything else has to match.
/// </para>
/// <para>
/// <b>Four paths, because they refuse from four places.</b> The service document and the layer
/// document resolve through different methods, and <c>query</c> is the one with its own history
/// of resolving a layer by itself. Asserting one of them would leave the others to drift.
/// </para>
/// <para>
/// <b>And one of them drifted, four days after that sentence was written.</b>
/// <c>/FeatureServer/layers</c> was added on 2026-09-08 and resolved its service through
/// <c>ServiceLookup.ServiceAsync</c>, which answers about <i>sharing</i> — so with the feature
/// face off it answered <b>200 to an anonymous caller</b>, carrying every layer's field names,
/// extent, symbology and capabilities string, while <c>/FeatureServer</c> and
/// <c>/FeatureServer/0</c> both answered 404. Two doors refusing and two open is worse than none
/// refusing: the two that work are the evidence an operator has that the setting does anything.
/// The fix is <c>ServiceLookup.FeatureServiceAsync</c>, one door for the feature face; this list
/// is what stops the next route from going through the other one.
/// </para>
/// <para>
/// <b>It mutates a real service and puts back what it read</b> — the pattern
/// <c>WfsConformanceTests</c> arrived at after restoring nulls wiped an explicit empty ceiling and
/// failed a console test hours later. Restoring a value the test never read is not restoring.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class FaceOffLooksAbsentTests : ArcGisClient
{
    /// <summary>A name no catalogue has, used as the control.</summary>
    private const string Absent = "zz_no_such_service_at_all";

    /// <summary>What both bodies' service names are replaced with before they are compared.</summary>
    private const string Placeholder = "«the name asked for»";

    /// <summary>
    /// The feature face off and the service missing are the same answer.
    /// </summary>
    [Fact]
    public async Task A_feature_face_turned_off_is_refused_exactly_as_absent()
    {
        string root = await RequireServerAsync();
        string? service = await AnyServiceNameAsync();

        Assert.False(service is null, "This server lists no feature service to turn a face off on.");

        string[] parts = service!.Split('/');
        string? folder = parts.Length > 1 ? parts[0] : null;
        string bare = parts[^1];

        string prefix = folder is { Length: > 0 } ? $"/rest/services/{folder}" : "/rest/services";

        // Read before writing, and put back what was read rather than what "unconfigured" means.
        string before = await CapabilitiesAsync(root, bare, folder);

        await SetFeatureFaceAsync(root, bare, folder, serves: false);

        try
        {
            foreach (string suffix in new[]
                     {
                         "/FeatureServer",

                         // <b>Added 2026-09-08, and it was open when it was added.</b> The
                         // all-layers document resolves the service without resolving a layer,
                         // which is the shape that skips the check — see the remarks.
                         "/FeatureServer/layers",

                         "/FeatureServer/0",
                         "/FeatureServer/0/query?where=1%3D1",
                     })
            {
                (HttpStatusCode off, string offBody) =
                    await AnonymousAsync($"{prefix}/{bare}{suffix}");
                (HttpStatusCode gone, string goneBody) =
                    await AnonymousAsync($"{prefix}/{Absent}{suffix}");

                int offStatus = (int)off;
                int goneStatus = (int)gone;

                Assert.True(
                    offStatus == goneStatus,
                    $"At {suffix} a service with its feature face off answered {offStatus} and a "
                    + $"service that does not exist answered {goneStatus}. The status alone tells "
                    + "an anonymous caller which services exist — ADR-031 condition 2.");

                Assert.Equal(
                    goneBody.Replace(Absent, Placeholder, StringComparison.Ordinal),
                    offBody.Replace(bare, Placeholder, StringComparison.Ordinal));
            }
        }
        finally
        {
            await RestoreAsync(root, bare, before);
        }

        // And the face comes back on, so the restore is asserted rather than hoped for. A test
        // that leaves a face off fails every suite that runs after it, with its own name nowhere
        // in the failure.
        Assert.Equal(200, await StatusOfAsync($"{prefix}/{bare}/FeatureServer"));
    }

    /// <summary>
    /// A group layer's document goes dark with the feature face too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fifth path, and it needs a fixture the other test does not.</b>
    /// <c>LayerMetadataAsync</c> answers a group's document from the *service* alone — the
    /// branch runs before <c>ServiceLookup.LayerAsync</c>, which is where the feature-face check
    /// used to live. So a service with its feature face off still described its structure: the
    /// group's name, its index, and the ids of the layers inside it.
    /// </para>
    /// <para>
    /// <b>Separate from the four-path test above because the shape is different.</b> That one
    /// walks suffixes on any service; this one needs a service that <i>has</i> a group, and the
    /// index it asks for has to be the group's rather than a layer's — so it takes
    /// <c>GRATICULA_TEST_GROUPED</c> and reads the id out of the service document rather than
    /// assuming one.
    /// </para>
    /// <para>
    /// <b>Status only, not the body.</b> The four-path test compares whole bodies against an
    /// absent service's, which is the assertion ADR-031 condition 2 actually wants; here the
    /// group id is a number that does not exist on the absent service either, so the two
    /// refusals differ in the id they name and comparing bodies would fail on the caller's own
    /// input. What this adds is the path, and the path is what was missing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_group_layers_document_goes_dark_with_the_feature_face()
    {
        string root = await RequireServerAsync();

        string? service = Environment.GetEnvironmentVariable("GRATICULA_TEST_GROUPED");

        Assert.False(
            string.IsNullOrWhiteSpace(service),
            "GRATICULA_TEST_GROUPED is not set, so this test FAILS rather than skips. Name a "
            + "service with a group layer in it: a group's document is answered from a branch "
            + "that no other test in this class reaches.");

        string[] parts = service!.Trim('/').Split('/');
        string? folder = parts.Length > 1 ? parts[0] : null;
        string bare = parts[^1];

        string prefix = folder is { Length: > 0 } ? $"/rest/services/{folder}" : "/rest/services";

        // <b>The group's own id, read rather than assumed.</b> An index is never reused, so a
        // group's number depends on what the service was built from and guessing it would make
        // this test pass against a layer.
        (HttpStatusCode listed, string document) =
            await AnonymousAsync($"{prefix}/{bare}/FeatureServer?f=json");

        Assert.Equal(HttpStatusCode.OK, listed);

        int? group = null;

        foreach (JsonElement one in
            JsonDocument.Parse(document).RootElement.GetProperty("layers").EnumerateArray())
        {
            if (one.TryGetProperty("subLayerIds", out JsonElement children)
                && children.ValueKind == JsonValueKind.Array)
            {
                group = one.GetProperty("id").GetInt32();
                break;
            }
        }

        Assert.True(
            group is not null,
            $"{service} lists no layer with subLayerIds, so it has no group in it and this test "
            + "is pointed at the wrong service.");

        string at = $"{prefix}/{bare}/FeatureServer/{group}";

        Assert.Equal(200, await StatusOfAsync(at));

        string before = await CapabilitiesAsync(root, bare, folder);

        await SetFeatureFaceAsync(root, bare, folder, serves: false);

        try
        {
            int off = await StatusOfAsync(at);

            Assert.True(
                off == 404,
                $"With the feature face off, a group layer's document answered {off} rather than "
                + "404. The group branch answers from the service alone, so it skips the check "
                + "every other route in this class is covered by — ADR-031 condition 2.");
        }
        finally
        {
            await RestoreAsync(root, bare, before);
        }

        Assert.Equal(200, await StatusOfAsync(at));
    }

    /// <summary>A service's capability document, as it stands now.</summary>
    /// <summary>
    /// Turning `Query` off does not stop the tile path, and `servesTiles` does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-180](../../docs/architecture-debt.md) counted a face that was never one.</b> Its
    /// reasoning listed *the tile path* among the five reading faces the `Query` capability
    /// governs and therefore among the places the enforcement was missing. Measured
    /// 2026-08-27 and it is not: tiles have their own switch. This pins both halves so the
    /// row cannot drift back.
    /// </para>
    /// <para>
    /// <b>Both halves, because either alone is misleading.</b> That the tile path answers
    /// while `Query` is off looks like the defect D-180 was about, and it is only correct
    /// beside the fact that `servesTiles` does stop it. That `servesTiles` stops it proves
    /// nothing about whether `Query` should have.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_tile_path_answers_to_its_own_switch_rather_than_to_Query()
    {
        string root = await RequireServerAsync();
        string? service = await AnyTileServiceNameAsync();

        Assert.False(service is null, "This server lists no tile service to turn a face off on.");

        string[] parts = service!.Split('/');
        string? folder = parts.Length > 1 ? parts[0] : null;
        string bare = parts[^1];

        string prefix = folder is { Length: > 0 } ? $"/rest/services/{folder}" : "/rest/services";
        string document = $"{prefix}/{bare}/VectorTileServer?f=json";

        string before = await CapabilitiesAsync(root, bare, folder);

        try
        {
            // ---------------------------------------------------------------- Query off
            await SetCeilingAsync(root, bare, folder, ["Create"]);

            (HttpStatusCode refused, _) =
                await AnonymousAsync($"{prefix}/{bare}/FeatureServer/0/query?where=1%3D1&f=json");

            Assert.Equal(HttpStatusCode.Forbidden, refused);

            (HttpStatusCode tiles, _) = await AnonymousAsync(document);

            Assert.True(
                tiles == HttpStatusCode.OK,
                $"With the capability ceiling set to Create -- Query explicitly off -- the "
                + $"VectorTileServer document answered {(int)tiles}. Tiles answer to "
                + "servesTiles, not to Query, and D-180's reasoning counted this as a fifth "
                + "unenforced face when it is not one. If this has genuinely changed, the "
                + "decision belongs in ADR-031 rather than in a test.");

            // ---------------------------------------------------------------- tiles off
            await SetTileFaceAsync(root, bare, folder, serves: false);

            (HttpStatusCode off, _) = await AnonymousAsync(document);

            Assert.True(
                off == HttpStatusCode.NotFound,
                $"With servesTiles false the VectorTileServer document answered {(int)off}. "
                + "The switch that is about tiles must turn tiles off, and ADR-031 "
                + "condition 2 makes a face that is off indistinguishable from absent.");
        }
        finally
        {
            await RestoreAsync(root, bare, before);
        }
    }

    /// <summary>A service that serves tiles, or null.</summary>
    private static Task<string?> AnyTileServiceNameAsync()
    {
        string? named = Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE");

        return Task.FromResult(string.IsNullOrWhiteSpace(named) ? null : named.Trim('/'));
    }

    /// <summary>Sets the capability ceiling and leaves the two faces alone.</summary>
    private async Task SetCeilingAsync(
        string root, string name, string? folder, string[] capabilities)
    {
        using HttpRequestMessage request =
            new(HttpMethod.Put, new Uri($"{root}/admin/services/{Uri.EscapeDataString(name)}/capabilities"))
            {
                Content = JsonContent.Create(new
                {
                    folder,
                    servesFeatures = (bool?)null,
                    servesTiles = (bool?)null,
                    capabilities,
                }),
            };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Turns the tile face on or off, leaving the ceiling alone.</summary>
    private async Task SetTileFaceAsync(string root, string name, string? folder, bool? serves)
    {
        using HttpRequestMessage request =
            new(HttpMethod.Put, new Uri($"{root}/admin/services/{Uri.EscapeDataString(name)}/capabilities"))
            {
                Content = JsonContent.Create(new
                {
                    folder,
                    servesFeatures = (bool?)null,
                    servesTiles = serves,
                    capabilities = (string[]?)null,
                }),
            };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> CapabilitiesAsync(string root, string name, string? folder)
    {
        string path = $"/admin/services/{Uri.EscapeDataString(name)}/capabilities"
            + (folder is { Length: > 0 } ? $"?folder={Uri.EscapeDataString(folder)}" : string.Empty);

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Could not read {name}'s capabilities: {(int)response.StatusCode}");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Turns the feature face on or off, leaving everything else unconfigured.</summary>
    private async Task SetFeatureFaceAsync(string root, string name, string? folder, bool? serves)
    {
        using HttpRequestMessage request =
            new(HttpMethod.Put, new Uri($"{root}/admin/services/{Uri.EscapeDataString(name)}/capabilities"))
            {
                Content = JsonContent.Create(new
                {
                    folder,
                    servesFeatures = serves,
                    servesTiles = (bool?)null,
                    capabilities = (string[]?)null,
                    statementTimeoutMilliseconds = (int?)null,
                }),
            };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Could not turn {name}'s feature face off: {(int)response.StatusCode} "
            + await response.Content.ReadAsStringAsync());
    }

    /// <summary>Puts a service's capabilities back exactly as they were read.</summary>
    private async Task RestoreAsync(string root, string name, string document)
    {
        JsonElement read = JsonDocument.Parse(document).RootElement;

        Dictionary<string, object?> body = new(StringComparer.Ordinal);

        foreach (JsonProperty property in read.EnumerateObject())
        {
            if (property.Name is "name" or "configured" or "note" or "serverRequestDeadlineSeconds"
                or "kind" or "sharing")
            {
                continue;
            }

            body[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => property.Value.GetInt64(),
                JsonValueKind.Array => property.Value.EnumerateArray().Select(v => v.GetString()).ToArray(),
                _ => property.Value.GetString(),
            };
        }

        // The write shape spells this one differently from the read shape.
        if (body.Remove("statementTimeoutMs", out object? timeout))
        {
            body["statementTimeoutMilliseconds"] = timeout;
        }

        using HttpRequestMessage request =
            new(HttpMethod.Put, new Uri($"{root}/admin/services/{Uri.EscapeDataString(name)}/capabilities"))
            {
                Content = JsonContent.Create(body),
            };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Could not restore {name}'s capabilities: {(int)response.StatusCode} "
            + await response.Content.ReadAsStringAsync());
    }
}
