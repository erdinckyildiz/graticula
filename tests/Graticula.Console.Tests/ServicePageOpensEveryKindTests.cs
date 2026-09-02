using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Studio's item page opens a service of any kind, or says why it cannot show more.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-200](../../docs/architecture-debt.md).</b> The page built its address as
/// <c>/rest/services/{name}/FeatureServer</c> for every service whatever its kind — a 404 for an
/// image service — and then looked the item up in <c>/content/items</c>, which lists feature
/// services only, found nothing and returned. The result was blank input-shaped placeholders and
/// a 404 in the browser's console: the service is there, its own face answers, and the console
/// showed nothing and explained nothing.
/// </para>
/// <para>
/// <b>It asserts what a reader gets, not which requests were made.</b> The address has to name a
/// face that answers, and the facts column has to say something rather than stay empty — those
/// are the two halves of the defect and either one alone would let it come back.
/// </para>
/// <para>
/// <b>The fixture needs a service that is not a feature service.</b> Without one this fails rather
/// than skips, because a test that quietly passes on a catalogue with nothing to test is how this
/// defect survived a review of the screen next to it.
/// </para>
/// </remarks>
public sealed class ServicePageOpensEveryKindTests : ConsoleTest
{
    [Fact]
    public async Task A_service_that_is_not_a_feature_service_still_opens_in_Studio()
    {
        (string token, _) = await SignInAsync();

        (int listed, string directory) = await AdminAsync(HttpMethod.Get, "/rest/services/hosted?f=json");

        Assert.True(listed is 200, $"the services directory answered {listed}: {directory}");

        // <b>A service is listed once per face, not once.</b> `ci_buildings` appears as a
        // `FeatureServer` and again as a `VectorTileServer`, so *the first entry that is not a
        // FeatureServer* names a service that has one — which is how the first version of this
        // test asked the page for a tile address and called the answer a defect. What is wanted
        // is a service with no feature face at all.
        Dictionary<string, List<string>> faces = new(StringComparer.Ordinal);

        foreach (JsonElement service in JsonDocument.Parse(directory)
                     .RootElement.GetProperty("services").EnumerateArray())
        {
            string name = service.GetProperty("name").GetString() ?? string.Empty;

            if (!faces.TryGetValue(name, out List<string>? kinds))
            {
                faces[name] = kinds = [];
            }

            kinds.Add(service.GetProperty("type").GetString() ?? string.Empty);
        }

        string? other = null;
        string picked = "(none)";

        foreach (KeyValuePair<string, List<string>> service in faces)
        {
            if (!service.Value.Contains("FeatureServer"))
            {
                other = service.Key;
                picked = string.Join(", ", service.Value);
                break;
            }
        }

        Assert.False(
            other is null,
            "No service in this catalogue is anything but a FeatureServer, so there is nothing to "
            + "open. Publish an image service into the fixture — D-200 is about the kinds Studio "
            + "was never asked to draw.");

        await OpenAsync($"/studio/#/service/{other}", token);

        await WaitForAsync(
            "document.querySelector('#svcUrl') && document.querySelector('#svcUrl').value.length > 0",
            "Studio's item page never drew an address for a service that is not a feature service.");

        string address = await Browser.EvaluateAsync<string>(
            "document.querySelector('#svcUrl').value") ?? string.Empty;

        string where = await Browser.EvaluateAsync<string>("location.hash") ?? string.Empty;

        Assert.False(
            address.EndsWith("/FeatureServer", StringComparison.Ordinal),
            $"The page offers `{address}` for `{other}`, which is not a FeatureServer. That URL "
            + $"answers 404, and it is the one thing somebody opens this column for. (at {where}; its faces are {picked})");

        // <b>And the facts column says something.</b> An empty definition list beside a filled
        // coverage panel is the shape a reviewer read as *the page never populates*.
        await WaitForAsync(
            "document.querySelector('#svcFacts') "
            + "&& document.querySelector('#svcFacts').children.length > 0",
            "The facts column stayed empty for a service that is not a feature service, which is "
            + "the half-drawn page D-200 records.");

        string facts = await Browser.EvaluateAsync<string>(
            "document.querySelector('#svcFacts').textContent") ?? string.Empty;

        Assert.Contains("ImageServer", facts, StringComparison.Ordinal);

        // <b>And the subtitle under the name.</b> It was an empty string, which beside a service
        // that plainly has content reads as a page that gave up.
        string subtitle = await Browser.EvaluateAsync<string>(
            "(document.getElementById('serviceFacts') || {}).textContent || ''") ?? string.Empty;

        Assert.False(
            string.IsNullOrWhiteSpace(subtitle),
            "The line under the service's name is empty. Every other kind of service says what it "
            + "holds there, and an image service said nothing.");

        // <b>The coverage panel is what this kind's page actually is</b>, and it was working
        // before this repair — asserted so that fixing the column above cannot quietly cost it.
        string coverage = await Browser.EvaluateAsync<string>(
            "(document.getElementById('coverageFacts') || {}).textContent || ''") ?? string.Empty;

        Assert.Contains("Bands", coverage, StringComparison.Ordinal);

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }
}
