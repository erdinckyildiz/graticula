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

        // <b>The summary is the middle pane, and it is not a map.</b> Nothing on this server
        // turns an unpublished composition into a picture, so what is drawn is the service that
        // will exist — which is also the thing this test can assert.
        await WaitForAsync(
            "(document.getElementById('pubWhat')?.innerText || '').includes('index 0')",
            "The summary does not say what will exist. It is the only thing on this screen that "
            + "reports the composition, so an empty one is a screen that shows nothing.");

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
}
