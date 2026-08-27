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
/// <b>Three paths, because they refuse from three places.</b> The service document and the layer
/// document resolve through different methods, and <c>query</c> is the one with its own history
/// of resolving a layer by itself. Asserting one of them would leave the other two to drift.
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

    /// <summary>A service's capability document, as it stands now.</summary>
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
