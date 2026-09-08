using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The Publish screen composes a service and sends it as one act.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-057](../../docs/adr/ADR-057-composing-and-publishing-a-service.md), and the screen
/// it replaces is still next door.</b> Server's *New service* drawer asks for a container, then
/// a group, then a layer index nobody can find — a design review on 2026-09-06 called it the
/// API rendered as a form. This screen asks for none of that: tables go into a tree, the tree
/// is the service, and one request writes it.
/// </para>
/// <para>
/// <b>What this harness can and cannot see.</b> Every non-GET is trapped and answered with
/// <c>{}</c>, so the publish itself is covered over real HTTP by
/// <c>PublishCompositionConformanceTests</c>. What is under test here is the half that is the
/// screen: that a table can be got into the composition at all, that the summary says what will
/// exist, and that pressing Publish sends one request to the composition endpoint rather than
/// the three the old drawer needed.
/// </para>
/// </remarks>
public sealed class PublishScreenTests : ConsoleTest
{
    /// <summary>
    /// A table becomes a layer, the summary names it, and Publish sends one request.
    /// </summary>
    [Fact]
    public async Task A_table_becomes_a_layer_and_publishing_sends_one_request()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(
            "(() => { const e = document.getElementById('pubDbTree'); "
            + "return !!e && e.offsetParent !== null; })()",
            "The Publish screen did not draw its Databases pane. This console has shipped a "
            + "control that existed and rendered nowhere three times; that is what offsetParent "
            + "is here for.");

        // <b>The datastore, which every fixture has.</b> Opening it probes the source, which is
        // a real read of somebody's database — so the wait is on the answer rather than a sleep.
        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubdb]').length > 0",
            "No registered database is listed, so there is nothing to compose from.");

        await ClickAsync("#pubDbTree [data-pubdb]");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubtable]').length > 0",
            "Opening a database listed no tables. Either the probe failed or the tree does not "
            + "draw what it read.");

        // <b>Clicked, not dragged.</b> A synthetic click is what this harness can send, and the
        // screen accepts both on purpose — the same lesson the connection dialog's combo
        // learned on 2026-09-05, where listening only for what a real mouse sends made a
        // control this suite could not press.
        string chosen = await Browser.EvaluateAsync<string>(
            "document.querySelector('#pubDbTree [data-pubtable][draggable=true]')"
            + "?.getAttribute('data-pubtable') || ''") ?? string.Empty;

        Assert.False(
            string.IsNullOrWhiteSpace(chosen),
            "No table in this database can be published at all, so there is nothing this test "
            + "can compose with. Since migration 40 the only reason is a table with no geometry "
            + "column or no integer this server can use as an object id — being served by "
            + "another service is not one (ADR-057 §5i).");

        await ClickAsync($"#pubDbTree [data-pubtable='{chosen}']");

        await WaitForAsync(
            "document.querySelectorAll('#pubTree [data-pubnode]').length === 1",
            "The table did not become a layer in the composition.");

        // <b>The number a client will address it by, on the row.</b> This used to read a list
        // under the preview that repeated every name in the tree; the owner's word for that was
        // *saçma*, and the list was indeed already on the left. What the list carried that the
        // tree did not is the index, and that is where it is now.
        await WaitForAsync(
            "(document.querySelector('#pubTree [data-pubnode] .pubindex')?.textContent || '')"
            + ".trim() === '0'",
            "The first layer does not say it will be layer 0. The tree's order is the service's "
            + "numbering and a client asks for FeatureServer/0 by that number.");

        Assert.False(
            await Browser.EvaluateAsync<bool>("document.getElementById('pubOpen').disabled"),
            "Publish is still disabled with a layer in the composition.");

        await ClickAsync("#pubOpen");

        await WaitForAsync(
            "(() => { const e = document.getElementById('pbName'); "
            + "return !!e && e.offsetParent !== null; })()",
            "The Publish dialog did not open.");

        await Browser.EvaluateAsync<bool>(
            """(document.getElementById("pbName").value = "ZZZFromTheScreen", true)""");

        /*
          <b>What it can do, and the body is read rather than the URL — ADR-057 §5g.</b> The
          owner asked for capabilities at publish: *"Yetenekler seçilecek. Feature, MapServer,
          Vector Tile vs gibi."* `window.__writes` records the method and the path, which proves
          the request went somewhere and says nothing about what it carried — and a screen that
          draws four boxes and sends none of them looks identical from there.

          <b>Query is disabled and still sent.</b> A ceiling without it is refused by the server,
          so the box is drawn ticked and unclickable; the assertion below is what keeps the two
          from drifting apart, because a disabled checkbox is exactly the kind of control whose
          value quietly stops being read.
        */
        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch;
          window.__body = null;
          window.fetch = async (input, init) => {
            const where = typeof input === "string" ? input : (input && input.url) || "";
            if (where.includes("/admin/publish")) window.__body = (init && init.body) || "";
            return real(input, init);
          };
          return true;
        })();
        """);

        // Delete comes off, so what is sent is a real choice rather than the default set.
        await ClickAsync("#pbDelete");

        await WaitForAsync(
            "(document.getElementById('pbCapsSays')?.innerText || '').includes('Query,Create,Update')",
            "The dialog does not say what the service will advertise, so an operator ticking "
            + "boxes has nothing telling them what the ticks add up to.");

        await ClickAsync("#pbGo");

        // <b>One request, to the composition endpoint.</b> The old drawer needed three, in the
        // API's order; this is the assertion that the screen does not quietly do the same thing
        // with a nicer surface.
        await WaitForAsync(
            "(window.__writes || []).some(w => w.startsWith('POST') && w.includes('/admin/publish'))",
            "Publishing did not send a composition. The recorded writes were: "
            + string.Join(" | ", await WritesAsync()));

        Assert.DoesNotContain(
            "/admin/featureservices",
            string.Join(" | ", await WritesAsync()),
            StringComparison.Ordinal);

        string sent = await Browser.EvaluateAsync<string>("window.__body || ''") ?? string.Empty;

        Assert.Contains("\"capabilities\":[\"Query\",\"Create\",\"Update\"]", sent, StringComparison.Ordinal);
        Assert.Contains("\"servesFeatures\":true", sent, StringComparison.Ordinal);
        Assert.Contains("\"servesTiles\":true", sent, StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Right-clicking a layer offers a menu, and *zoom to layer* is on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner instruction, 2026-09-06:</b> *"sağ clickte zoom to layer yapabilmeliyim. sadece
    /// rename değil."* What was there was a chain of confirmations — *Ungroup "x"? Cancel to
    /// rename it instead* — which is two questions in one box, with the second reachable only by
    /// refusing the first, and nowhere to put a third.
    /// </para>
    /// <para>
    /// <b>Asserted on what is offered, not on what happens.</b> Zooming needs a map and an
    /// extent from the server, and this suite answers every write from inside the page — so the
    /// act cannot complete here. What can be checked is the thing that was missing: that the
    /// menu exists and that the item is on it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Right_clicking_a_layer_offers_a_menu_with_zoom_on_it()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubTree"), "The Publish screen drew no contents pane.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          pubTree = [{
            kind: "layer", id: "L" + (++pubSeq), name: "zz_menu",
            source: "00000000-0000-0000-0000-000000000000", sourceName: "probe",
            schema: "public", table: "zz_menu", geometry: "geom", identity: "objectid",
            srid: 3857, geometryType: "MultiPolygon", type: "MultiPolygon",
          }];

          pubDraw();
          return true;
        })();
        """);

        await WaitForAsync(
            "document.querySelectorAll('#pubTree [data-pubnode]').length === 1",
            "The layer did not draw.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          const row = document.querySelector('#pubTree [data-pubnode] .pubrow');
          const at = row.getBoundingClientRect();

          row.dispatchEvent(new MouseEvent("contextmenu", {
            bubbles: true, clientX: at.left + 40, clientY: at.top + 8 }));

          return true;
        })();
        """);

        await WaitForAsync(
            Shown("#pubmenu"),
            "Right-clicking a layer opened no menu. It used to ask a chain of confirmations, "
            + "which is why there was nowhere to put *zoom to layer*.");

        string items = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#pubmenu [data-pubact]')]"
            + ".map(b => b.dataset.pubact).join(',')") ?? string.Empty;

        Assert.Contains("zoom", items, StringComparison.Ordinal);
        Assert.Contains("rename", items, StringComparison.Ordinal);
        Assert.Contains("symbol", items, StringComparison.Ordinal);
        Assert.Contains("remove", items, StringComparison.Ordinal);

        // <b>Not on a layer, because a layer is not a group.</b> A menu that offers every act on
        // every node teaches people that half of it does nothing.
        Assert.DoesNotContain("ungroup", items, StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Any EPSG code can be typed, and the server says whether it can serve in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner instruction, 2026-09-06:</b> *"sadece 3 projeksiyon görebiliyorum. kendi
    /// istediğimi de girebilmeliyim."* The control was a select of three codes — a list of what
    /// somebody had thought of, where PROJ knows thousands.
    /// </para>
    /// <para>
    /// <b>And it asks rather than assuming.</b> An input that takes any number and fails at
    /// publish is worse than a list of three: the operator finds out after composing. The screen
    /// puts the code to <c>GET /admin/references/{srid}</c> while it is typed, and that is a
    /// read — so it reaches the real server through this suite's trap, which only holds writes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_reference_can_be_typed_and_the_server_answers_for_it()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        // <b>Behind the map's properties since 2026-09-07.</b> The reference is the map's, so
        // it is reached the way the map's other properties are — `The_reference_is_reached_by
        // _right_clicking_the_map` is what covers the route; this fact is about the answer.
        await WaitForAsync(Shown("#pubShot"), "The map is not on the screen.");

        await Browser.EvaluateAsync<bool>("(openMapProperties(), true)");

        await WaitForAsync(Shown("#pubSrid"), "There is no box to type a reference into.");

        Assert.Equal(
            "input",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('pubSrid').tagName.toLowerCase()"));

        // <b>One this server knows and is not in the console's short list.</b> UTM zone 36N
        // covers Türkiye and nothing on this screen names it, so an answer about it can only
        // have come from the server.
        await Browser.EvaluateAsync<bool>("""
        (document.getElementById("pubSrid").value = "32636", pubReference(), true)
        """);

        await WaitForAsync(
            "!(document.getElementById('pubSridSaysHead')?.textContent || '')"
            + ".includes('asking')"
            + " && (document.getElementById('pubSridSaysHead')?.textContent || '').length > 0",
            "The screen never said anything about the reference that was typed.");

        Assert.DoesNotContain(
            "cannot",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('pubSridSaysHead').textContent") ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        // <b>And a code nothing knows is refused where it is typed.</b>
        await Browser.EvaluateAsync<bool>("""
        (document.getElementById("pubSrid").value = "999999", pubReference(), true)
        """);

        await WaitForAsync(
            "(document.getElementById('pubSridSaysHead')?.textContent || '')"
            + ".toLowerCase().includes('cannot')",
            "A code this server cannot project to was accepted without a word. The publish "
            + "would refuse it — after the composition was built, which is the worst moment.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The reference is a property of the map, opened by right-clicking it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner instruction, 2026-09-07:</b> *"onu şu anda bulunduğu yerden alıp map'e sağ
    /// tıklayınca açılan bir ekrana koyalım. sonuçta map'in projeksiyonu hepsini kapsayacak."*
    /// The box sat on the page toolbar between <i>Preview</i> and <i>Clear</i>, which put a
    /// property of the map among the verbs.
    /// </para>
    /// <para>
    /// <b>Asserted from the map rather than from the dialog.</b> Checking that a dialog with an
    /// input exists would pass with no way to reach it, which is the shape of every control
    /// this console has shipped and left unreachable — including the symbol editor next door,
    /// whose markup, menu item and swatch were all present while the function they called had
    /// never been written.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_reference_is_reached_by_right_clicking_the_map()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubShot"), "The map is not on the screen.");

        Assert.False(
            await Browser.EvaluateAsync<bool>(Shown("#pubSrid")),
            "The reference box is still on screen without anybody opening the map's properties. "
            + "It was moved off the toolbar on purpose: it is a property of the map, not a "
            + "fourth verb beside Preview and Clear.");

        // <b>The gesture, not the handler.</b> Calling the menu builder directly would pass on
        // a screen where nothing listens for a right-click over the drawing.
        await Browser.EvaluateAsync<bool>("""
        (() => {
          const map = document.getElementById("pubShot");
          map.dispatchEvent(new MouseEvent("contextmenu",
            { bubbles: true, clientX: 300, clientY: 300 }));
          return true;
        })();
        """);

        await WaitForAsync(
            "(() => { const m = document.getElementById('pubmenu'); "
            + "return !!m && m.getClientRects().length > 0; })()",
            "Right-clicking the map offered nothing. The map and the root row are two views of "
            + "one thing and the owner asked for the menu on the map.");

        await WaitForAsync(
            "!!document.querySelector('#pubmenu [data-pubact=props]')",
            "The map's menu does not offer its properties, so the reference the whole service "
            + "is served in is now unreachable.");

        await ClickAsync("#pubmenu [data-pubact=props]");

        await WaitForAsync(
            Shown("#pubSrid"),
            "Map properties did not open, or opened without the box it exists to hold.");

        // <b>And the map's row says which, with the dialog shut.</b> A reference visible only
        // inside a dialog is one that is forgotten between composing and publishing.
        await Browser.EvaluateAsync<bool>("""
        (document.getElementById("pubSrid").value = "32636", pubReference(), true)
        """);

        await WaitForAsync(
            "(document.querySelector('#pubTree .pubroot .pubsr')?.textContent || '')"
            + ".includes('32636')",
            "The map's row does not carry the reference that was just chosen.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A pasted definition is what the Publish dialog says, and what the request carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The dialog was lying, and this is the assertion that was missing.</b> Its <i>Served
    /// in</i> line worked the reference out for itself from the box and only knew how to read a
    /// code, so a pasted definition — accepted everywhere else since 2026-09-06 — made the last
    /// line an operator reads before pressing Publish say <i>each layer's own</i> while the
    /// request it then sent carried the definition.
    /// </para>
    /// <para>
    /// <b>D-46.</b> One behaviour in two places, one copy taught about definitions and the
    /// other not. The repair is that the sentence is written once, where the question is
    /// answered, and the map's row, the map's properties and this dialog all read it — so this
    /// test asserts on both ends of that: what the dialog shows and what the body holds.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_pasted_definition_is_confirmed_and_sent()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubDbTree"), "The Databases pane did not draw.");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubdb]').length > 0",
            "No registered database is listed.");

        await ClickAsync("#pubDbTree [data-pubdb]");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubtable][draggable=true]').length > 0",
            "This database offers no publishable table.");

        await ClickAsync("#pubDbTree [data-pubtable][draggable=true]");

        await WaitForAsync(
            "document.querySelectorAll('#pubTree [data-pubnode]').length === 1",
            "The table did not become a layer.");

        // <b>A definition, not a code</b> — TUREF / TM30 is a national grid and the point of
        // accepting one at all.
        await Browser.EvaluateAsync<bool>("""
        (() => {
          document.getElementById("pubSrid").value =
            'PROJCS["TUREF / TM30",GEOGCS["TUREF",DATUM["Turkish_National_Reference_Frame",'
            + 'SPHEROID["GRS 1980",6378137,298.257222101]],PRIMEM["Greenwich",0],'
            + 'UNIT["degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],'
            + 'PARAMETER["central_meridian",30],UNIT["metre",1]]';
          pubReference();
          return true;
        })();
        """);

        await WaitForAsync(
            "(document.getElementById('pubSridSaysHead')?.textContent || '').includes('TUREF')",
            "The screen does not recognise the pasted definition as a reference with a name.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch;
          window.__body = null;
          window.fetch = async (input, init) => {
            const where = typeof input === "string" ? input : (input && input.url) || "";
            if (where.includes("/admin/publish") && !where.includes("/preview")
                && !where.includes("/extent")) {
              window.__body = (init && init.body) || "";
            }
            return real(input, init);
          };
          return true;
        })();
        """);

        await ClickAsync("#pubOpen");

        await WaitForAsync(Shown("#pbName"), "The Publish dialog did not open.");

        string said = await Browser.EvaluateAsync<string>(
            "document.getElementById('pbSridSays').textContent") ?? string.Empty;

        Assert.DoesNotContain("each layer", said, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("TUREF", said, StringComparison.Ordinal);

        await Browser.EvaluateAsync<bool>(
            """(document.getElementById("pbName").value = "ZZZWktFromTheScreen", true)""");

        await ClickAsync("#pbGo");

        await WaitForAsync("!!window.__body", "Publishing sent nothing.");

        string sent = await Browser.EvaluateAsync<string>("window.__body") ?? string.Empty;

        Assert.Contains("\"sridWkt\":\"PROJCS", sent, StringComparison.Ordinal);

        // <b>And no code beside it.</b> The server refuses both at once and so does the schema;
        // a screen that sent a stale 3857 alongside the definition would be refused at the end
        // of a composition rather than here.
        Assert.Contains("\"srid\":null", sent, StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The swatch under a layer opens the symbol editor, and choosing a colour reaches the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This control threw a <c>ReferenceError</c> from the day it was drawn.</b> The dialog
    /// markup, the swatch button, the <i>Symbol…</i> menu item, both click handlers and
    /// <c>pubSymbolDocument</c> — which turns the answer into CIM — were all written;
    /// <c>openPubSymbol</c> never was. So both ways in failed silently, `node.symbol` was set by
    /// nothing, and every composition published <c>symbology: null</c>.
    /// </para>
    /// <para>
    /// <b>It survived because no test pressed it.</b> Every fact on this class asserts on the
    /// tree, the request or the map, and a swatch showing the default colours looks exactly
    /// like a swatch showing a layer nobody has restyled. <c>NothingWentWrong</c> would have
    /// caught it on the first click — there had never been one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_swatch_opens_a_symbol_editor_that_exists()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubDbTree"), "The Databases pane did not draw.");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubdb]').length > 0",
            "No registered database is listed.");

        await ClickAsync("#pubDbTree [data-pubdb]");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubtable][draggable=true]').length > 0",
            "This database offers no publishable table.");

        await ClickAsync("#pubDbTree [data-pubtable][draggable=true]");

        await WaitForAsync(
            "!!document.querySelector('#pubTree [data-pubsym]')",
            "The layer has no swatch, so there is nothing to press.");

        await ClickAsync("#pubTree [data-pubsym]");

        await WaitForAsync(
            Shown("#pubsymFill") + " || " + Shown("#pubsymLine"),
            "Pressing the swatch opened no editor. Until 2026-09-07 it called a function that "
            + "had never been written — and the click handler swallows what it throws, which is "
            + "why nothing on this page says so and why the control looked fine for a day.");

        // <b>A colour that is nothing like the generated one</b>, so the swatch cannot pass by
        // accidentally still showing the default.
        await Browser.EvaluateAsync<bool>("""
        (() => {
          const box = document.getElementById("pubsymLine");
          box.value = "#ff0000";
          box.dispatchEvent(new Event("input", { bubbles: true }));
          return true;
        })();
        """);

        await WaitForAsync(
            "(document.querySelector('#pubTree .pubswatch')?.getAttribute('style') || '')"
            + ".includes('#ff0000')",
            "The colour that was chosen did not reach the layer's swatch, so the editor is "
            + "writing somewhere the composition does not read.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The menu takes focus, the arrows move through it, and Escape gives focus back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The roles were a spelling rather than a behaviour.</b> <c>#pubmenu</c> has carried
    /// <c>role="menu"</c> and its items <c>role="menuitem"</c> since they were written, and
    /// nothing implemented what those roles mean: focus never entered the menu, the arrow keys
    /// did nothing, and Escape left focus wherever it had been. A claimed role that is not
    /// implemented is worse than no role — it tells assistive software to expect an interaction
    /// model the page does not have.
    /// </para>
    /// <para>
    /// <b>It matters most for this menu.</b> Since 2026-09-07 the map's coordinate system is
    /// reached only from here, and the menu is at the end of the document — it has to be, since
    /// a menu clipped by its own scrolling pane loses its last item — so tabbing to it means
    /// tabbing past every control of every layer ahead of it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_menu_can_be_walked_with_the_keyboard()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubShot"), "The map is not on the screen.");

        // <b>Opened from the root row's button, which is the keyboard route in.</b> The pointer
        // has a right-click; a keyboard has this.
        await ClickAsync("#pubRootMenu");

        await WaitForAsync(
            "(() => { const m = document.getElementById('pubmenu'); "
            + "return !!m && m.getClientRects().length > 0; })()",
            "The root row's button opened no menu.");

        await WaitForAsync(
            "document.activeElement?.hasAttribute('data-pubact') === true",
            "The menu opened without taking focus, so a keyboard user is left tabbing towards "
            + "it through every control between here and the end of the document.");

        string first = await Browser.EvaluateAsync<string>(
            "document.activeElement.getAttribute('data-pubact')") ?? string.Empty;

        await Browser.EvaluateAsync<bool>("""
        (() => {
          document.activeElement.dispatchEvent(new KeyboardEvent("keydown",
            { key: "ArrowDown", bubbles: true }));
          return true;
        })();
        """);

        string second = await Browser.EvaluateAsync<string>(
            "document.activeElement?.getAttribute('data-pubact') || ''") ?? string.Empty;

        Assert.NotEqual(first, second);

        Assert.False(
            string.IsNullOrEmpty(second),
            "Pressing Down took focus out of the menu rather than to its next item.");

        // <b>And End reaches the item this menu exists for.</b> Map properties is last on it.
        await Browser.EvaluateAsync<bool>("""
        (() => {
          document.activeElement.dispatchEvent(new KeyboardEvent("keydown",
            { key: "End", bubbles: true }));
          return true;
        })();
        """);

        Assert.Equal(
            "props",
            await Browser.EvaluateAsync<string>(
                "document.activeElement?.getAttribute('data-pubact') || ''"));

        await Browser.EvaluateAsync<bool>("""
        (() => {
          document.activeElement.dispatchEvent(new KeyboardEvent("keydown",
            { key: "Escape", bubbles: true }));
          return true;
        })();
        """);

        await WaitForAsync(
            "document.activeElement?.id === 'pubRootMenu'",
            "Escape shut the menu and left focus inside it — which for a keyboard user is focus "
            + "on nothing, at the end of the document, with no way back but Tab.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A typed code is named, whatever it is, and the list offers what the server knows.
    /// </summary>
    /// <remarks>
    /// <b>Owner instruction, 2026-09-07:</b> *"tanımlı tüm srid leri gösterebilir miyiz. mesela
    /// 3857 yazınca web mercator yazıyor ama 4236 yazınca adı çıkmıyor."* The screen knew five
    /// names from a constant in the page. This drives a code that is deliberately not one of the
    /// five, so the name on screen can only have come from the projection database.
    /// </remarks>
    [Fact]
    public async Task Any_code_is_named_and_the_list_comes_from_the_server()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubShot"), "The map is not on the screen.");

        await Browser.EvaluateAsync<bool>("(openMapProperties(), true)");

        await WaitForAsync(Shown("#pubSrid"), "The reference box did not open.");

        // <b>Not one of the five in `PUB_REFERENCES`.</b> Asserted, so this test cannot start
        // passing because somebody added it to the constant.
        Assert.DoesNotContain(
            "4236",
            await Browser.EvaluateAsync<string>(
                "JSON.stringify(PUB_REFERENCES.map(r => r.code))") ?? string.Empty,
            StringComparison.Ordinal);

        await Browser.EvaluateAsync<bool>("""
        (document.getElementById("pubSrid").value = "4236", pubReference(), true)
        """);

        await WaitForAsync(
            "(document.getElementById('pubSridSaysHead')?.textContent || '')"
            + ".includes('Hu Tzu Shan')",
            "A code outside the console's own short list was accepted without a name. That is "
            + "the difference between 'this is usable' and 'this is the reference you meant' — "
            + "4236 is one keystroke from 4326 and its area of use is Taiwan.");

        // <b>And the list is the server's, not the constant's.</b> Five options would be the
        // constant; a search for a word nothing in it contains proves where they came from.
        await Browser.EvaluateAsync<bool>("""
        (document.getElementById("pubSrid").value = "turef", pubSuggest(), true)
        """);

        await WaitForAsync(
            "[...document.querySelectorAll('#pubReferences option')]"
            + ".some(o => (o.textContent || '').toUpperCase().includes('TUREF'))",
            "Typing a reference's name offered nothing. The box takes any code, which is only "
            + "half of choosing your own — the other half is finding one without knowing its "
            + "number already.");

        await WaitForAsync(
            "(document.getElementById('pubSuggestSays')?.textContent || '').length > 0",
            "The screen does not say how much of the projection database it is showing, so a "
            + "box offering twenty of eight thousand looks like a box offering everything.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The preview is a map, it says nothing when the composition is empty, and it takes a drop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three owner instructions, 2026-09-06, and two of them were repeats.</b> *"preview
    /// kısmında bir harita olsun. nothing to draw yet yazmasın."* — the pane held a sentence
    /// explaining that there was nothing to show, where a ground would have answered *where am
    /// I* without being read. *"datastore kalksın oradan demiştim hala orada."* — the Databases
    /// pane listed the one store whose tables are already services, which is an act with no
    /// subject, and the instruction had been given once already. *"map'e databaseden taşıdığım
    /// toc'a gelsin."*
    /// </para>
    /// <para>
    /// <b>Asserted here because none of the three is visible to any other test.</b> A screen
    /// can pass every behavioural test on this class with no map at all — the composition,
    /// the request and the tree are all unaffected by what the middle pane draws. That is how
    /// the first version of this screen shipped as three lists and was reported as done.
    /// </para>
    /// <para>
    /// <b>The drawing itself is not asserted, and cannot be from here.</b> This suite answers
    /// every write from inside the page, so the preview request never reaches a server and no
    /// picture comes back — <c>PublishCompositionConformanceTests</c> is where the drawing is
    /// checked, against a real one, with the pixels counted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_preview_is_a_map_that_says_nothing_when_there_is_nothing()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubTree"), "The Publish screen drew no contents pane.");

        await WaitForAsync(
            "!!document.querySelector('#pubMap .ol-viewport')",
            "The preview is not a map. It is meant to show the ground before anything is "
            + "composed, so that an empty composition is an empty map rather than a sentence.");

        // <b>Nothing said over an empty map.</b> The note is for a refusal the server gave, and
        // *there is nothing to draw* is not one — it is a description of the screen.
        Assert.False(
            await Browser.EvaluateAsync<bool>(Shown("#pubMapSays")),
            "The empty map carries a note. With no layers there is nothing to say that the "
            + "map does not already say.");

        Assert.False(
            await Browser.EvaluateAsync<bool>(Shown("#pubShotImg")),
            "A drawing is shown with nothing composed.");

        // <b>The datastore is not one of the databases here.</b> Its tables are already
        // services; offering to compose one is offering an act with no subject.
        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubdb]').length > 0"
            + " || (document.getElementById('pubDbTree')?.innerText || '').includes('No database')",
            "The Databases pane drew neither a database nor an explanation of why not.");

        Assert.DoesNotContain(
            "datastore",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('pubDbTree')?.innerText || ''") ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The contents pane is a tree: a root, a tick, a symbol and a mark where it reprojects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The screen shipped as three flat lists and was presented as built to the design study
    /// it came from.</b> The owner put the two side by side on 2026-09-06 and asked whether they
    /// were the same screen. They were not: no root, no disclosure, no tick, no symbol, no
    /// reprojection mark, and a text summary where the drawing belonged. Everything worked and
    /// nothing looked like what had been agreed.
    /// </para>
    /// <para>
    /// <b>So the shape is asserted, not only the behaviour.</b> Every other test on this screen
    /// drags a table and watches a request; all of them passed against the flat version. A
    /// structure nobody checks is a structure that quietly does not exist —
    /// [D-90](../../docs/architecture-debt.md)'s lesson, applied to layout rather than to a
    /// button.
    /// </para>
    /// <para>
    /// <b>The composition is put in directly rather than dragged.</b> Dragging has its own test
    /// above; this one is about what the pane draws, and building the tree through the pointer
    /// would make a layout failure look like a drag failure. The layers name references that
    /// differ on purpose, because the reprojection mark is drawn only where there is one and
    /// *drawn on everything* is the failure it is easiest to ship.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_contents_pane_is_a_tree_with_a_root_a_tick_and_a_symbol()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubTree"), "The Publish screen drew no contents pane.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          const layer = (name, srid, type) => ({
            kind: "layer", id: "L" + (++pubSeq), name,
            source: "00000000-0000-0000-0000-000000000000", sourceName: "probe",
            schema: "public", table: name, geometry: "geom", identity: "objectid",
            srid, geometryType: type, type,
          });

          pubTree = [
            layer("zz_same", 3857, "MultiPolygon"),
            { kind: "group", id: "G_zz", name: "zz_group", children: [
              layer("zz_other", 4326, "MultiLineString"),
            ] },
          ];

          pubDraw();
          return true;
        })();
        """);

        await WaitForAsync(
            "document.querySelectorAll('#pubTree [data-pubnode]').length === 3",
            "The composition did not draw its three nodes.");

        // <b>A root, because every layer here hangs off one service.</b> Without it there is
        // nothing to right-click when the thing being changed is the service itself, which is
        // where its name and its reference are.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelector('#pubTree [data-pubroot]') !== null"),
            "The contents pane has no root node, so the service has nowhere to be named.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelectorAll('#pubTree [data-pubshow]').length === 3"),
            "Not every node carries a visibility tick.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelectorAll('#pubTree [data-pubsym]').length === 2"),
            "The layers do not show the symbol they will be drawn with. A group has none, "
            + "because a group holds no data — so two of the three nodes should.");

        // <b>The mark is on the one layer stored in something else, and on nothing else.</b>
        // Served in 3857: `zz_other` is stored in 4326 and is warped; `zz_same` is not.
        Assert.Equal(
            1,
            await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#pubTree .pubwarp').length"));

        // <b>A reference is a code, not a quantity.</b> `num` groups thousands, so every badge
        // on this screen read `EPSG:3,857` — which is not a code anybody can paste or look up.
        Assert.DoesNotContain(
            "3,857",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('pubTree').innerText") ?? string.Empty,
            StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Two tables of one name become two layers of two names.
    /// </summary>
    /// <remarks>
    /// <b>`public.parcels` and `arsiv.parcels` are an ordinary pair.</b> A layer's name is
    /// unique inside its service — `layer_name_unique_in_service` — so composing both under one
    /// name is refused, at the end, after the whole composition is built. The screen can see it
    /// coming, so it suffixes on the way in and the operator renames it afterwards if the suffix
    /// is not what they wanted.
    /// <para>
    /// <b>Asked of the function rather than staged in the fixture.</b> This fixture happens to
    /// hold no two tables of one name across its schemas, and seeding a pair to prove a naming
    /// rule would be a fixture change for a screen's arithmetic.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_second_table_of_the_same_name_gets_a_name_of_its_own()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubdb]').length > 0",
            "No registered database is listed.");

        await ClickAsync("#pubDbTree [data-pubdb]");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubtable][draggable=true]').length > 0",
            "No free table to compose with.");

        await ClickAsync("#pubDbTree [data-pubtable][draggable=true]");

        await WaitForAsync(
            "document.querySelectorAll('#pubTree [data-pubnode]').length === 1",
            "The table did not become a layer.");

        string taken = await Browser.EvaluateAsync<string>(
            "pubLayers()[0].name") ?? string.Empty;

        Assert.False(string.IsNullOrWhiteSpace(taken), "The composed layer has no name.");

        Assert.Equal(
            $"{taken}_2",
            await Browser.EvaluateAsync<string>(
                $"pubFreeName({System.Text.Json.JsonSerializer.Serialize(taken)})"));

        // And a name nothing is using comes back untouched.
        Assert.Equal(
            "ZZZUnused",
            await Browser.EvaluateAsync<string>("pubFreeName('ZZZUnused')"));

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A gesture asks for one drawing, the one it replaces is cancelled, and an empty view asks
    /// for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner opened the network panel: 280 requests and 3.4 MB.</b> *"bu scalede yoksa
    /// neden çizmeye çalışıyor? görüneni çizmesi lazım."* And then, on the cancellation:
    /// *"zoom değişince gelene göre eski requestlerin cancel da olması lazım. bu var olan bir
    /// gis yapısıdır."* Every settled frame was a full render — a spatial query per layer
    /// against somebody's database — and a pan across a city left a trail of them running to
    /// completion for answers nobody would look at.
    /// </para>
    /// <para>
    /// <b>Counted rather than reasoned about.</b> Three numbers, and each of the three
    /// mechanisms fails on its own: without the wait, twelve view changes are twelve requests;
    /// without the abort, the superseded ones run to the end; without the extent check, a view
    /// on the other side of the world still asks for a picture of nothing.
    /// </para>
    /// <para>
    /// <b>Two of the screen's own facts are injected, because this suite cannot supply them.</b>
    /// Every write is answered from inside the page, so <c>/admin/publish/extent</c> never
    /// reaches a server and no drawing ever comes back — which means the screen never learns
    /// where the composition is and never frames the map on it. On a real server the first draw
    /// lands and both are true from then on. Injecting them is what makes the third number
    /// measurable at all; the numbers before it need nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_gesture_asks_once_cancels_what_it_replaces_and_skips_an_empty_view()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubTree"), "The Publish screen drew no contents pane.");

        await WaitForAsync(
            "!!document.querySelector('#pubMap .ol-viewport')", "The preview is not a map.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          window.__asked = 0;
          window.__stopped = 0;

          const real = window.fetch;

          window.fetch = (input, init) => {
            const where = typeof input === "string" ? input : (input && input.url) || "";

            if (where.includes("/admin/publish/preview")) {
              window.__asked++;
              init?.signal?.addEventListener("abort", () => window.__stopped++);
            }

            return real(input, init);
          };

          return true;
        })();
        """);

        await Browser.EvaluateAsync<bool>("""
        (() => {
          pubTree = [{
            kind: "layer", id: "L" + (++pubSeq), name: "zz_quiet",
            source: "00000000-0000-0000-0000-000000000000", sourceName: "probe",
            schema: "public", table: "zz_quiet", geometry: "geom", identity: "objectid",
            srid: 3857, geometryType: "MultiPolygon",
          }];

          pubDraw();
          return true;
        })();
        """);

        await Task.Delay(2500);

        int settled = await Browser.EvaluateAsync<int>("window.__asked");

        Assert.True(
            settled is > 0 and <= 2,
            $"Composing one layer asked for {settled} drawings. One is the answer; a handful "
            + "means the screen is redrawing on something other than the composition changing.");

        // Twelve view changes in quick succession, as a hand on a wheel makes.
        await Browser.EvaluateAsync<bool>("""
        (() => {
          const v = pubMap.getView();

          for (let i = 0; i < 12; i++) v.setZoom(v.getZoom() - 0.5);

          return true;
        })();
        """);

        await Task.Delay(2000);

        int afterZoom = await Browser.EvaluateAsync<int>("window.__asked");

        Assert.True(
            afterZoom - settled <= 2,
            $"Twelve view changes asked for {afterZoom - settled} drawings. Each is a query per "
            + "layer against somebody's database, and the reader only ever sees the last.");

        // <b>Superseded on purpose, because the wait above means it rarely happens by accident.</b>
        // Collapsing twelve view changes into one request is the other repair working; to see a
        // cancellation there has to be a drawing in flight when the next one starts, so two are
        // started in the same tick.
        await Browser.EvaluateAsync<bool>("(pubShoot(true), pubShoot(true), true)");

        await Task.Delay(1500);

        Assert.True(
            await Browser.EvaluateAsync<int>("window.__stopped") > 0,
            "A drawing started while another was in flight and the first was not cancelled. One "
            + "that is merely ignored still runs to the end on the server — which is the half of "
            + "this the owner named: *eski requestlerin cancel da olması lazım*.");

        // <b>What a successful first draw would have taught the screen.</b>
        await Browser.EvaluateAsync<bool>("""
        (() => {
          pubLearnWhere = async body => {
            pubWhere = { of: body, box: [3657465, 4862565, 3664835, 4869935] };
          };

          pubFramed = true;
          return true;
        })();
        """);

        // <b>Counted here, after the deliberate supersede above.</b> Comparing against the
        // number from before it would be counting those two as if the pan had made them.
        int before = await Browser.EvaluateAsync<int>("window.__asked");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          pubMap.getView().setCenter([-8000000, 4000000]);
          pubMap.getView().setZoom(9);
          return true;
        })();
        """);

        await Task.Delay(2000);

        Assert.Equal(before, await Browser.EvaluateAsync<int>("window.__asked"));

        Assert.Contains(
            "in view",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('pubMapSays')?.textContent || \"\"") ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A table dragged in keeps its geometry: the tree's symbol agrees with the pane's mark.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner put the two panes side by side and they disagreed.</b> `geopoint_2835ac42`
    /// drew a dot in Databases and a green rectangle in the contents tree, with no type name
    /// beside it — *"gerçekte olan icon gösterimi sağda, toc'da ise poligon ve yeşil fill ile
    /// gibi"*, 2026-09-06. The composed node called the geometry `type` and the swatch read
    /// `geometryType`, so the swatch read nothing and fell through to *area*.
    /// </para>
    /// <para>
    /// <b>Neither existing test could see it.</b> One checks the marks in the Databases pane,
    /// where the field is right; the other builds a composition by hand and sets both names,
    /// which is precisely the bug being written out of the test. This one drags a real table
    /// through the real path and compares the two panes — which is what the owner did.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_dragged_table_keeps_its_geometry_in_the_tree()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubTree"), "The Publish screen drew no contents pane.");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubdb]').length > 0",
            "No registered database is listed.");

        await ClickAsync("#pubDbTree [data-pubdb]");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubtable][draggable=true]').length > 0",
            "Opening a database offered no publishable table.");

        // <b>A point, on purpose.</b> Falling through to *area* is the failure, so a polygon
        // table would pass a broken screen — which is how this shipped.
        string picked = await Browser.EvaluateAsync<string>("""
        (() => {
          const rows = [...document.querySelectorAll('#pubDbTree [data-pubtable][draggable=true]')];
          const dot = rows.find(r => r.querySelector('.pubgeom.dot'))
            || rows.find(r => r.querySelector('.pubgeom.line'))
            || rows[0];

          return dot ? dot.getAttribute('data-pubtable') : '';
        })();
        """) ?? string.Empty;

        Assert.False(
            string.IsNullOrWhiteSpace(picked),
            "No table in this database can be published, so nothing can be dragged.");

        string kindInPane = await Browser.EvaluateAsync<string>($$"""
        (() => {
          const row = document.querySelector('#pubDbTree [data-pubtable={{'"'}}{{picked}}{{'"'}}]');
          const mark = row?.querySelector('.pubgeom');

          return mark?.classList.contains('dot') ? 'dot'
            : mark?.classList.contains('line') ? 'line' : 'fill';
        })();
        """) ?? string.Empty;

        await ClickAsync($"#pubDbTree [data-pubtable='{picked}']");

        await WaitForAsync(
            "document.querySelectorAll('#pubTree [data-pubnode]').length === 1",
            "The table did not become a layer.");

        // <b>The swatch, and the name beside it.</b> An empty name is the tell: the swatch has
        // a shape whatever it reads, and *area* is what it falls through to.
        string kindInTree = await Browser.EvaluateAsync<string>("""
        (() => {
          const s = document.querySelector('#pubTree .pubswatch');

          return s?.classList.contains('dot') ? 'dot'
            : s?.classList.contains('line') ? 'line' : 'fill';
        })();
        """) ?? string.Empty;

        Assert.Equal(kindInPane, kindInTree);

        Assert.False(
            string.IsNullOrWhiteSpace(
                await Browser.EvaluateAsync<string>(
                    "document.querySelector('#pubTree .pubsymname')?.textContent?.trim() || \"\"")),
            "The layer's symbol has no geometry name beside it, which is what an unread "
            + "geometry type looks like.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Every table in the Databases pane says whether it is points, lines or areas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner instruction, 2026-09-06:</b> *"sağ taraftaki database bağlantısında listenen
    /// tabloların point mi line mı poligon mu olduğunu bir simge ile gösterir misin?"* The mark
    /// beside each table was a dot meaning *publishable* — which the row already says by being
    /// draggable, and says in words underneath when it is not. It did not say the one thing
    /// somebody scanning sixty table names wants.
    /// </para>
    /// <para>
    /// <b>Checked against each row's own answer, not against the fixture's table names.</b> The
    /// mark carries the geometry type in its title, so the assertion is that the shape drawn and
    /// the type reported agree — which holds on any fixture and catches the failure that
    /// matters: a classifier that falls through to *area* for a spelling it did not expect. The
    /// probe says `POLYGON` where the catalogue says `MultiPolygon`, and matching either exactly
    /// would be right two thirds of the time.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Each_table_shows_whether_it_is_points_lines_or_areas()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubdb]').length > 0",
            "No registered database is listed.");

        await ClickAsync("#pubDbTree [data-pubdb]");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubtable]').length > 0",
            "Opening a database listed no tables.");

        Assert.Equal(
            0,
            await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#pubDbTree [data-pubtable]').length"
                + " - document.querySelectorAll('#pubDbTree [data-pubtable] .pubgeom').length"));

        // <b>The shape and the type it claims, compared row by row.</b>
        string wrong = await Browser.EvaluateAsync<string>("""
        [...document.querySelectorAll('#pubDbTree [data-pubtable] .pubgeom')]
          .map(m => {
            const said = (m.getAttribute("title") || "").toLowerCase();
            const wanted = said.includes("point") ? "dot"
              : said.includes("line") ? "line" : "fill";

            return m.classList.contains(wanted) ? "" : said + " drawn as " + m.className;
          })
          .filter(Boolean)
          .join(" | ")
        """) ?? string.Empty;

        Assert.True(
            wrong.Length == 0,
            $"A table's mark does not match the geometry it reports: {wrong}");

        // <b>And the three are told apart, not all drawn the same.</b> A classifier answering
        // *area* for everything would satisfy the comparison above on a fixture of polygons.
        Assert.True(
            await Browser.EvaluateAsync<int>(
                "new Set([...document.querySelectorAll('#pubDbTree [data-pubtable] .pubgeom')]"
                + ".map(m => m.classList.contains('dot') ? 'dot'"
                + " : m.classList.contains('line') ? 'line' : 'fill')).size") > 1,
            "Every table in this database draws the same shape, so either the fixture holds one "
            + "geometry or the mark is not reading the type.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A table another service already serves can still be dragged into a new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test used to assert the opposite, and the opposite was wrong.</b> It read *a
    /// table already served is offered and refused*, and it passed for as long as the screen
    /// struck those tables through and ignored the click. The owner saw the result on 2026-09-06
    /// — most of a developer's database greyed out — and said what the rule actually is:
    /// <i>"bir tablonun bir serviste kullanılması, başka bir serviste kullanılmasını engellemez.
    /// in use durumu saçma."</i>
    /// </para>
    /// <para>
    /// <b>The rule it enforced was never decided.</b> <c>layer_table_unique</c> came with
    /// migration 1's <c>create table layer</c> and nothing recorded why; ADR-057 §5i then closed
    /// an open question by citing it. Migration 40 scopes it to the service. The test is
    /// inverted rather than deleted, because the interesting fact is the same one — what happens
    /// when somebody reaches for a table another service holds — and only the answer changed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_table_another_service_serves_can_still_be_composed_with()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubdb]').length > 0",
            "No registered database is listed.");

        await ClickAsync("#pubDbTree [data-pubdb]");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubtable]').length > 0",
            "Opening a database listed no tables.");

        // <b>Nothing wears the mark any more, and its absence is the assertion.</b> A screen
        // that kept the class and stopped acting on it would look identical to one that had
        // dropped the rule, and the next reader would restore the refusal to match the styling.
        Assert.Equal(
            0,
            await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#pubDbTree [data-pubtable].used').length"));

        // The one thing that genuinely cannot be composed with is a table this server cannot
        // address — no geometry column, or no integer to use as an object id.
        string servable = await Browser.EvaluateAsync<string>(
            "document.querySelector('#pubDbTree [data-pubtable][draggable=true]')"
            + "?.getAttribute('data-pubtable') || ''") ?? string.Empty;

        Assert.False(
            string.IsNullOrWhiteSpace(servable),
            "No table in this database can be published at all, so this test is checking "
            + "nothing.");

        await ClickAsync($"#pubDbTree [data-pubtable='{servable}']");

        await WaitForAsync(
            "document.querySelectorAll('#pubTree [data-pubnode]').length === 1",
            "A table this server can address did not become a layer. Since migration 40 the "
            + "only reason to refuse one is that it has no geometry or no object id, and this "
            + "one was offered as draggable.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A name already published by this operator is offered as a replacement, not refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-057](../../docs/adr/ADR-057-composing-and-publishing-a-service.md) §5e, which
    /// was decided on 2026-09-05 and had nothing behind it until 2026-09-08.</b> The dialog read
    /// the name only when Publish was pressed, so a collision arrived after the composition was
    /// finished — and the half of the decision about a name of your own had nowhere to happen.
    /// </para>
    /// <para>
    /// <b>The check is a GET, which is why this suite can see it.</b> Every non-GET here is
    /// trapped and answered with <c>{}</c>; the name check asks a question and changes nothing,
    /// so it reaches the real server and the answer under the box is the server's.
    /// </para>
    /// <para>
    /// <b>Asserted on the button as well as on the sentence.</b> A screen that explained the
    /// collision and left Publish armed would still destroy a service on the next press, and
    /// that is the failure this test is for: the tick is what arms it, and the label is what
    /// says which of the two acts is about to happen.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_name_of_your_own_is_offered_as_a_replacement()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(Shown("#pubTree"), "The Publish screen drew no contents pane.");

        // A composition, without touching a database: what is under test is the dialog, and a
        // layer it will never send is enough to open one.
        await Browser.EvaluateAsync<bool>("""
        (() => {
          pubTree = [{
            kind: "layer", id: "L" + (++pubSeq), name: "zz_name",
            source: "00000000-0000-0000-0000-000000000000", sourceName: "probe",
            schema: "public", table: "zz_name", geometry: "geom", identity: "objectid",
            srid: 3857, geometryType: "MultiPolygon", type: "MultiPolygon",
          }];

          pubDraw();
          return true;
        })();
        """);

        await ClickAsync("#pubOpen");

        await WaitForAsync(
            "(() => { const e = document.getElementById('pbName'); "
            + "return !!e && e.offsetParent !== null; })()",
            "The Publish dialog did not open.");

        // <b>A service this fixture certainly has, published by the account this suite signs in
        // as.</b> The seed publishes everything into `hosted` as the administrator, which is who
        // is typing here — so this reaches the *yours* branch rather than the one that refuses.
        string address = await AnyServiceAddressAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(address),
            "This fixture publishes nothing at all, so there is no name of the operator's own "
            + "to collide with and this test is checking nothing.");

        string[] parts = address.Split('/');
        string where = parts.Length > 1 ? parts[0] : string.Empty;
        string mine = parts[^1];

        await Browser.EvaluateAsync<bool>($$"""
        (() => {
          const name = document.getElementById("pbName");
          const folder = document.getElementById("pbFolder");

          folder.value = {{System.Text.Json.JsonSerializer.Serialize(where)}};
          folder.dispatchEvent(new Event("input", { bubbles: true }));

          name.value = {{System.Text.Json.JsonSerializer.Serialize(mine)}};
          name.dispatchEvent(new Event("input", { bubbles: true }));
          return true;
        })();
        """);

        await WaitForAsync(
            "(() => { const e = document.getElementById('pbNameSays'); "
            + "return !!e && e.offsetParent !== null "
            + "&& (e.innerText || '').includes('by you'); })()",
            "The dialog did not say that the name is already published by this operator. A "
            + "control that exists and renders nowhere has shipped here three times, which is "
            + "what offsetParent is in this assertion for.");

        Assert.True(
            await Browser.EvaluateAsync<bool>("document.getElementById('pbGo').disabled"),
            "Publish is armed over a service the operator already owns, with nothing ticked. "
            + "The next press would replace it.");

        await ClickAsync("#pbReplace");

        Assert.False(
            await Browser.EvaluateAsync<bool>("document.getElementById('pbGo').disabled"),
            "Ticking the replacement did not arm Publish, so the decision the dialog asks for "
            + "leads nowhere.");

        Assert.Contains(
            "Replace",
            await Browser.EvaluateAsync<string>("document.getElementById('pbGo').textContent")
                ?? string.Empty,
            StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }
}
