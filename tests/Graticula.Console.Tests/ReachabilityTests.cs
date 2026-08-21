using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Whether every service's own settings can be reached by clicking.
/// </summary>
public sealed class ReachabilityTests : ConsoleTest
{
    /// <summary>
    /// Every service on the list reaches its Limits page from a click on its row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-59's fourth defect, and the one that shows why a browser was needed.</b>
    /// Moving Capabilities and Limits off the layer pages and onto the service
    /// (D-61) left behind a shortcut written when a service page was a one-row
    /// table: a service holding a single layer skipped the drill-in and opened the
    /// layer instead. So the settings existed, the route existed, the page
    /// rendered — and eight of nine services had no way to get there. The owner
    /// found it in a sentence: <em>"tüm servislerden limits ler uçmuş. neden.
    /// onlar nerede?"</em>
    /// </para>
    /// <para>
    /// <b>Nothing short of clicking would have caught it.</b> Every part worked in
    /// isolation, including the address, and a test that navigated by setting
    /// <c>location.hash</c> would have passed on all nine while a person could
    /// reach one. What was broken was only which route a click chose, which is a
    /// fact about the browser and not about the code.
    /// </para>
    /// <para>
    /// <b>Every row, not the first.</b> The shortcut applied to the services with
    /// one member, so a suite that checked one row had a one-in-nine chance of
    /// finding this. Checking all of them costs a navigation each and is the whole
    /// reason the defect is expressible as a test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_service_reaches_its_own_limits_page_from_a_click()
    {
        (string token, _) = await SignInAsync();

        // <b>The work list comes from the catalogue, not from a screen.</b> Reading
        // it off the rows made the test depend on the console being right about
        // what it holds in order to check whether it was — and worse, it silently
        // recorded one folder's services against another folder's address, which
        // is how it came to fail about one run in three.
        List<(string Service, string Folder, string[] Siblings)> work = new();

        // The ArcGIS catalogue publishes image services; the console has no screen for
        // one and does not list them at all — D-136. Walking to one waits for a page
        // that will not arrive and then reports a reachability failure about the
        // console's own screens, which is the wrong sentence about the wrong thing.
        HashSet<string> imagery = await ImageServicesAsync();

        foreach ((string folder, string[] services) in await FoldersWithServicesAsync())
        {
            foreach (string service in services)
            {
                if (imagery.Contains(service))
                {
                    continue;
                }

                work.Add((service, folder, services));
            }
        }

        Assert.NotEmpty(work);

        foreach ((string service, string folder, string[] siblings) in work)
        {
            string row = $"tr[data-service={JsonSerializer.Serialize(service)}] span.name";

            // Back to the list every time, and opened by a click every time. The
            // point is which route a click chooses, so continuing from the page the
            // previous click left open would test the second service through the
            // first one's address.
            await OpenAsync(ServicesIn(folder), token);
            await ShowingAsync(folder, siblings);

            // <b>Narrowed to this one first, because the listing pages at ten.</b>
            // Walking every service and clicking its row assumed every service was on
            // page one, which held until a folder had eleven. Filtering is also what
            // an operator with a hundred services does, so the click being tested is
            // the click they make.
            await FilterAsync("serviceFilter", service[(service.LastIndexOf('/') + 1)..]);
            await ClickAsync(row);

            await WaitForAsync(
                "location.hash.startsWith('#/service/')",
                $"Clicking '{service}' did not open the service. A service that opens one of its "
                + "layers instead has no reachable Capabilities or Limits at all, which is what "
                + "left eight of nine without them.");

            // <b>Either shape of Limits counts, because there are two kinds of
            // service and both have bounds.</b> A feature service gets the left
            // nav; a service with no layers — the geometry service — gets its own
            // panel, since there is no list to put beside it and a nav of one item
            // is furniture. What is being asserted is the rule under both: from a
            // click on a row, the operator reaches the screen that edits what this
            // service is allowed to spend.
            await WaitForAsync(
                "document.querySelector('#serviceNav a[data-service-page=\"limits\"]')"
                + " || !document.getElementById('serviceLimits').hidden",
                $"'{service}' opened, and offered nowhere to read or change its limits. They are "
                + "stored on the service and no other screen edits them.");

            // <b>And it is reached rather than merely present, which this test did not check until
            // ADR-034 §5k put the settings behind a tab.</b> The assertion above passed on a page whose
            // Settings panel was hidden, because it asks whether the element exists — and *reaches* is
            // the word in this test's own name. So: press Settings when the page has one, then require
            // the control to be visible by `offsetParent`, which is the check this repository uses after
            // shipping an invisible control three times.
            await ClickIfPresentAsync("#serviceTabs a[data-service-tab=\"settings\"]");

            await WaitForAsync(
                "(() => { const nav = document.querySelector("
                + "'#serviceNav a[data-service-page=\"limits\"]');"
                + " const own = document.getElementById('serviceLimits');"
                + " return (nav && nav.offsetParent !== null)"
                + " || (own && own.offsetParent !== null); })()",
                $"'{service}' has a limits page in its markup and the operator cannot see it. That is "
                + "the defect shape this console has shipped three times — a control that exists and "
                + "is not on screen.");
        }
    }
}
