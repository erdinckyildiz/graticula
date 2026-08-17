using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The services directory must not advertise what an anonymous caller cannot
/// read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other test in this suite authenticates, which makes the whole suite
/// blind to one class of fault.</b> An administrator's token opens everything, so
/// a service that is listed publicly and then refuses the public passes every
/// existing check while being useless to the only caller that matters — the
/// unmodified ArcGIS client of Q-07 and Q-17. That client walks the directory,
/// finds a FeatureServer, asks for its document, and stops.
/// </para>
/// <para>
/// <b>The invariant is one-directional, and deliberately so.</b> Listing implies
/// readability; readability does not imply listing. A layer may be readable by
/// name without appearing in a folder, which is a legitimate configuration.
/// </para>
/// <para>
/// <b>The converse is not tested here, and that is worth stating rather than
/// leaving as an appearance of coverage.</b> Proving that a service *absent* from
/// the anonymous directory is also refused anonymously needs a private service to
/// exist, and this suite runs against whatever deployment it is pointed at — in
/// an all-public deployment such a test would pass while proving nothing, which
/// is worse than not having it. That direction is checked interactively by the
/// console's Anonymous view, which compares each layer's sharing scope against
/// what an anonymous probe actually got.
/// </para>
/// </remarks>
public sealed class AnonymousAccessConformanceTests : ArcGisClient
{
    /// <summary>
    /// Walks the anonymous directory and reads every FeatureServer it offers.
    /// </summary>
    [Fact]
    public async Task The_directory_does_not_advertise_what_an_anonymous_caller_cannot_read()
    {
        List<string> services = await AnonymousFeatureServicesAsync();

        Assert.NotEmpty(services);

        List<string> unreadable = [];

        foreach (string service in services)
        {
            (HttpStatusCode status, _) = await AnonymousAsync($"/rest/services/{service}/FeatureServer");

            if (status != HttpStatusCode.OK)
            {
                unreadable.Add($"{service} → {(int)status} on its service document");
            }
        }

        Assert.True(
            unreadable.Count == 0,
            "The anonymous services directory lists services that an anonymous caller cannot "
            + "then read. This is the failure an ArcGIS client meets first, and it looks to the "
            + "operator like the layer having vanished rather than like a sharing scope:"
            + Environment.NewLine + string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// Reads the first layer of every anonymously listed FeatureServer.
    /// </summary>
    /// <remarks>
    /// Separate from the service document, because they are refused by different
    /// code. A service can be shared while the layer inside it is not, and the
    /// client that stops there has a service document promising a layer that is
    /// not there.
    /// </remarks>
    [Fact]
    public async Task A_listed_service_offers_at_least_one_readable_layer()
    {
        List<string> services = await AnonymousFeatureServicesAsync();

        List<string> empty = [];

        foreach (string service in services)
        {
            (HttpStatusCode status, string body) =
                await AnonymousAsync($"/rest/services/{service}/FeatureServer");

            if (status != HttpStatusCode.OK)
            {
                // Reported by the test above. Not repeated here, so one fault does
                // not produce two failures saying the same thing.
                continue;
            }

            JsonElement document = JsonDocument.Parse(body).RootElement;

            if (!document.TryGetProperty("layers", out JsonElement layers)
                || layers.ValueKind != JsonValueKind.Array
                || layers.GetArrayLength() == 0)
            {
                empty.Add($"{service} → its service document offers no layers");
                continue;
            }

            bool any = false;

            foreach (JsonElement layer in layers.EnumerateArray())
            {
                if (!layer.TryGetProperty("id", out JsonElement id))
                {
                    continue;
                }

                (HttpStatusCode layerStatus, _) =
                    await AnonymousAsync($"/rest/services/{service}/FeatureServer/{id.GetInt32()}");

                if (layerStatus == HttpStatusCode.OK)
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                empty.Add($"{service} → every layer it lists was refused anonymously");
            }
        }

        Assert.True(
            empty.Count == 0,
            "A service readable by an anonymous caller listed layers that were not:"
            + Environment.NewLine + string.Join(Environment.NewLine, empty));
    }

    /// <summary>
    /// Every FeatureServer an anonymous caller is offered, across the root and
    /// its folders.
    /// </summary>
    private async Task<List<string>> AnonymousFeatureServicesAsync()
    {
        List<string> found = [];
        List<string> roots = [string.Empty];

        (HttpStatusCode status, string body) = await AnonymousAsync("/rest/services");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement root = JsonDocument.Parse(body).RootElement;

        if (root.TryGetProperty("folders", out JsonElement folders)
            && folders.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement folder in folders.EnumerateArray())
            {
                if (folder.GetString() is { Length: > 0 } name)
                {
                    roots.Add(name);
                }
            }
        }

        // The root document's own service list is read from the same parse, and
        // each folder is fetched. A folder that refuses is a fault in itself — the
        // directory named it.
        foreach (string folder in roots)
        {
            JsonElement document;

            if (folder.Length == 0)
            {
                document = root;
            }
            else
            {
                (HttpStatusCode folderStatus, string folderBody) =
                    await AnonymousAsync($"/rest/services/{folder}");

                Assert.True(
                    folderStatus == HttpStatusCode.OK,
                    $"The root directory lists the folder '{folder}' and then answered "
                    + $"{(int)folderStatus} when an anonymous caller opened it.");

                document = JsonDocument.Parse(folderBody).RootElement;
            }

            if (!document.TryGetProperty("services", out JsonElement services)
                || services.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement service in services.EnumerateArray())
            {
                if (service.TryGetProperty("type", out JsonElement type)
                    && type.GetString() == "FeatureServer"
                    && service.TryGetProperty("name", out JsonElement name)
                    && name.GetString() is { Length: > 0 } text)
                {
                    found.Add(text);
                }
            }
        }

        return found;
    }
}
