using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The last round of the redesign of 2026-11-02 — the ux-designer's verification of dev: a phone's targets are 44 pixels,
/// the service page's Layers card is one card, and Server › Services is drawn as its mockup draws it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each assertion is the mechanism and not the fixture</b>: the height of what is on screen, the box a heading and its list
/// share, the left of the folders and of the table. None reads a name the fixture happens to hold.
/// </para>
/// </remarks>
public sealed class LastRoundOfTheRedesignTests : ConsoleTest
{
    /// <summary>
    /// What is drawn under 44 pixels, per visible control. A link inside a sentence, a checkbox inside a label that is itself
    /// 44 pixels, a control the page hides from the reader (inert, closed, clipped) and the map's own zoom pair are not counted.
    /// </summary>
    private const string Under44 = """
        (() => {
          const vis = e => { const r = e.getBoundingClientRect(); const c = getComputedStyle(e);
            return r.width > 0 && r.height > 0 && c.visibility !== 'hidden' && !e.closest('[inert]')
              && !e.closest('details:not([open]) > :not(summary)') && !e.closest('.sr-only'); };
          const small = [];
          for (const e of document.querySelectorAll("a[href], button, input:not([type=hidden]), select, textarea, summary, [role=button], [role=tab]")) {
            if (!vis(e) || e.id === 'skipToContent' || e.classList.contains('skiplink')) continue;
            const r = e.getBoundingClientRect();
            if (r.top > innerHeight * 6) continue;
            const inline = e.tagName === 'A' && getComputedStyle(e).display === 'inline' && e.closest('p, li, dd, small, .hint, .note');
            if (inline) continue;
            if (r.height >= 43.5 && r.width >= 43.5) continue;
            const label = e.closest('label');
            const lr = label && label.getBoundingClientRect();
            if (lr && lr.height >= 43.5 && lr.width >= 43.5) continue;
            small.push((e.getAttribute('aria-label') || e.textContent || e.id || e.tagName).trim().replace(/\s+/g, ' ').slice(0, 30)
              + ':' + Math.round(r.width) + 'x' + Math.round(r.height));
          }
          return small;
        })()
        """;

    /// <summary>
    /// A sample of the phone's screens — Studio's content, members and roles, Server's services and data sources, System's
    /// general and backups: every visible interactive element is at least 44 pixels tall.
    /// </summary>
    [Theory]
    [InlineData("/studio/#/content", "document.querySelectorAll('#contentRows tr.mediarow').length > 0")]
    [InlineData("/studio/#/members", "document.querySelectorAll('#members tr').length > 0")]
    [InlineData("/studio/#/roles", "document.querySelectorAll('#roleRows button.rowname').length > 0")]
    [InlineData("/server/#/services", "document.querySelectorAll('#services tr.svcrow').length > 0")]
    [InlineData("/server/#/sources", "document.querySelectorAll('#sources tr').length > 0")]
    [InlineData("/system/#/settings", "!!document.getElementById('generalSaveBar') && document.querySelector('#view-settings.on')")]
    [InlineData("/system/#/backups", "!!document.getElementById('bkEnabled')")]
    public async Task On_a_phone_every_visible_control_is_44_pixels_tall(string address, string loaded)
    {
        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });

        try
        {
            (string token, _) = await SignInAsync();
            await OpenAsync(address, token);
            await WaitForAsync(loaded, $"{address} never drew what it is measured on.");
            await Task.Delay(600);

            string[] small = await Browser.EvaluateAsync<string[]>(Under44) ?? [];

            Assert.True(small.Length == 0, $"At 390 pixels {address} draws controls under 44 pixels: " + string.Join(" | ", small));
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }

    /// <summary>
    /// The Layers heading and its list are one card: the heading is not a grid item stretched by the aside's rows, so the two
    /// share a parent and the list starts where the heading ends — at 1,280 and at 390.
    /// </summary>
    [Theory]
    [InlineData(1280)]
    [InlineData(390)]
    public async Task The_layers_heading_and_its_list_are_one_card(int width)
    {
        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new { width, height = 900, deviceScaleFactor = 1, mobile = width < 700 });

        try
        {
            (string token, _) = await SignInAsync();
            string service = await FirstServiceAsync();
            await OpenAsync($"/studio/#/service/{service}", token);
            await WaitForAsync(
                "(() => { const l = document.getElementById('serviceLayersPanel'); return !!l && l.getClientRects().length > 0 "
                + "&& document.querySelectorAll('#serviceLayerRows tr').length > 0; })()",
                "The service's Overview drew no layers.");

            string verdict = await Browser.EvaluateAsync<string>("""
                (() => {
                  const head = document.getElementById('serviceLayersHead').getBoundingClientRect();
                  const list = document.getElementById('serviceLayersPanel').getBoundingClientRect();
                  const same = document.getElementById('serviceLayersHead').parentElement === document.getElementById('serviceLayersPanel').parentElement;
                  const aside = document.getElementById('serviceDetails');
                  const asideHeight = aside ? aside.getBoundingClientRect().height : 0;
                  return JSON.stringify({ same, gap: Math.round(list.top - head.bottom), headHeight: Math.round(head.height),
                    left: Math.round(head.left) === Math.round(list.left), asideHeight: Math.round(asideHeight) });
                })()
                """) ?? string.Empty;

            using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(verdict);
            System.Text.Json.JsonElement root = parsed.RootElement;

            Assert.True(root.GetProperty("same").GetBoolean(), "The Layers heading and its list are separate grid items again: " + verdict);
            Assert.True(root.GetProperty("left").GetBoolean(), "The heading and the list do not start at the same left: " + verdict);
            Assert.InRange(root.GetProperty("gap").GetInt32(), -20, 4);
            Assert.True(
                root.GetProperty("headHeight").GetInt32() < 120,
                "The heading is stretched to the height of the aside's rows: " + verdict);
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }

    /// <summary>
    /// Server › Services as its mockup draws it: the folders are the left column and the table starts to their right, a row has
    /// Type, Folder and Status columns, the head carries no sentence, and Empty services is chosen from the list's menu rather
    /// than standing on the page.
    /// </summary>
    [Fact]
    public async Task Services_has_folders_on_the_left_and_Type_Folder_and_Status_columns_at_1280()
    {
        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new { width = 1280, height = 900, deviceScaleFactor = 1, mobile = false });

        try
        {
            (string token, _) = await SignInAsync();
            await OpenAsync("/server/#/services", token);
            await WaitForAsync("document.querySelectorAll('#services tr.svcrow').length > 0", "Services drew no rows.");

            string verdict = await Browser.EvaluateAsync<string>("""
                (() => {
                  const side = document.getElementById('folderRail').getBoundingClientRect();
                  const table = document.getElementById('services').closest('table').getBoundingClientRect();
                  const heads = [...document.querySelectorAll('#view-services thead th')].filter(h => h.getClientRects().length).map(h => h.textContent.trim());
                  const row = document.querySelector('#services tr.svcrow');
                  const cell = n => { const c = row.querySelector('td.col.' + n); return !!c && c.getClientRects().length > 0 && c.textContent.trim().length > 0; };
                  const head = document.querySelector('#serverMain > .pagehead');
                  return JSON.stringify({ folderRight: Math.round(side.right), tableLeft: Math.round(table.left), heads,
                    type: cell('type'), folder: cell('folder'), status: cell('status'),
                    sentence: !!head.querySelector('p') && head.querySelector('p').getClientRects().length > 0,
                    emptyShown: document.getElementById('emptyServices').getClientRects().length > 0 });
                })()
                """) ?? string.Empty;

            using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(verdict);
            System.Text.Json.JsonElement root = parsed.RootElement;

            Assert.True(root.GetProperty("folderRight").GetInt32() <= root.GetProperty("tableLeft").GetInt32(), "The folders are not beside the table: " + verdict);
            Assert.True(root.GetProperty("type").GetBoolean() && root.GetProperty("folder").GetBoolean() && root.GetProperty("status").GetBoolean(),
                "A row has no Type, Folder or Status cell on show: " + verdict);
            Assert.Contains("Status", root.GetProperty("heads").ToString(), StringComparison.Ordinal);
            Assert.False(root.GetProperty("sentence").GetBoolean(), "The head still carries a sentence under the title: " + verdict);
            Assert.False(root.GetProperty("emptyShown").GetBoolean(), "Empty services stands on the page before it is chosen: " + verdict);
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }

    /// <summary>
    /// On a phone the folders are a sheet behind the head's scope button, and the title is in the first screenful: the sheet is
    /// a dialog, choosing a folder closes it and names it on the button, and the heading is above y = 200.
    /// </summary>
    [Fact]
    public async Task On_a_phone_Services_has_its_title_above_200_and_its_folders_in_a_sheet()
    {
        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });

        try
        {
            (string token, _) = await SignInAsync();
            await OpenAsync("/server/#/services", token);
            await WaitForAsync("document.querySelectorAll('#services tr.svcrow').length > 0", "Services drew no rows.");

            Assert.True(
                await Browser.EvaluateAsync<bool>("document.querySelector('#view-services h1').getBoundingClientRect().top < 200"),
                "The title is below the first 200 pixels: the sub-page strip or the folders are above it.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#folderRail")), "The folders stand on the page; on a phone they are a sheet.");

            await ClickAsync("#servicesScopeBtn");
            await WaitForAsync(
                "document.getElementById('servicesSide').getAttribute('role') === 'dialog' && document.getElementById('folderRail').getClientRects().length > 0",
                "The scope button did not open the folders sheet.");

            await ClickAsync("#servicesSheetClose");
            await WaitForAsync(
                "!document.getElementById('servicesSide').classList.contains('open') && document.activeElement?.id === 'servicesScopeBtn'",
                "Closing the sheet did not return focus to the scope button.");
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }

    /// <summary>A hosted feature layer the seed publishes (tools/seed-conformance.py, prefix <c>ci</c>), which has layers to list.</summary>
    private static Task<string> FirstServiceAsync() => Task.FromResult("hosted/ci_buildings");
}
