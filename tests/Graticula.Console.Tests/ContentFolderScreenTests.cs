using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-114 condition 3: My content shows the caller's folders, filters by one, and moves an item.
/// </summary>
/// <remarks>
/// <b>The folder and the layer are made through the API</b> and removed after; the page's own writes are trapped by the
/// harness, so what the Move press is checked for is the request it sends.
/// </remarks>
public sealed class ContentFolderScreenTests : ConsoleTest
{
    [Fact]
    public async Task My_content_shows_folders_filters_by_one_and_moves_an_item()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string layer = $"zz_folderscreen_{suffix}";
        string title = $"zz Screen {suffix}";
        string? folder = null;

        (int defined, string design) = await AdminAsync(HttpMethod.Post, "/admin/hosted/define",
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new object[] { new { name = "label", type = "text", nullable = true } },
            }));
        Assert.True(defined == 201, $"Defining {layer} answered {defined}: {design}");

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/content/folders", JsonSerializer.Serialize(new { title }));
            Assert.True(made == 201, $"Making the folder answered {made}: {madeBody}");
            folder = JsonDocument.Parse(madeBody).RootElement.GetProperty("id").GetString();

            (int moved, string movedBody) = await AdminAsync(HttpMethod.Post, "/content/move",
                JsonSerializer.Serialize(new { items = new[] { new { service = $"hosted/{layer}" } }, to = folder }));
            Assert.True(moved == 200, $"Moving answered {moved}: {movedBody}");

            (string token, _) = await SignInAsync();
            await OpenAsync("/studio/#/content/mine", token);

            await WaitForAsync($"!!document.querySelector('#contentFolders [data-folder-pick=\"{folder}\"]')",
                "My content does not show the caller's folder.");

            await ClickAsync($"#contentFolders [data-folder-pick=\"{folder}\"]");
            await WaitForAsync(
                $"[...document.querySelectorAll('#contentRows td.name a')].some(a => a.textContent.trim() === '{layer}')",
                "Choosing the folder does not show the item in it.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('#contentRows td.name a')].length === 1"),
                "Choosing a folder shows items that are not in it.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.activeElement?.dataset?.folderPick !== undefined"),
                "Choosing a folder dropped the focus.");

            // Move, from the row's menu.
            await Browser.EvaluateAsync<bool>("""
                (() => { document.querySelector('#contentRows details.menu').open = true; return true; })()
                """);
            await ClickAsync($"#contentRows [data-move-service=\"hosted/{layer}\"]");
            await WaitForAsync(Shown("#moveTo"), "Move to folder did not ask where to.");
            Assert.Equal(folder, await Browser.EvaluateAsync<string>("document.getElementById('moveTo').value"));

            // Move stays off until the folder chosen is a different one (design review 2026-10-01).
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('contentFolderGo').disabled"),
                "Move is offered to the folder the item is already in.");
            await Browser.EvaluateAsync<bool>("""
                (() => {
                  window.__writes = [];
                  const pick = document.getElementById('moveTo');
                  pick.value = '';
                  pick.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                })()
                """);
            await ClickAsync("#contentFolderGo");
            await WaitForAsync("(window.__writes || []).some(w => w.startsWith('POST') && w.includes('/content/move'))",
                "Move did not send the move.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (folder is not null)
            {
                await AdminAsync(HttpMethod.Post, "/content/move",
                    JsonSerializer.Serialize(new { items = new[] { new { service = $"hosted/{layer}" } }, to = (string?)null }));
                await AdminAsync(HttpMethod.Delete, $"/content/folders/{folder}");
            }

            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{layer}?folder=hosted&drop=true");
        }
    }
}
