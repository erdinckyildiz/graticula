using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The two screens that finish a geodatabase upload: choose what to publish, and what happened.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reached by handing the console its own state, because the other way in is somebody's data.</b>
/// These screens follow an inspection job, and an inspection needs a real File Geodatabase — the
/// owner's three are real client archives and stay out of this repository
/// ([ADR-030](../../docs/adr/ADR-030-reading-the-reference-implementation.md)'s neighbouring rule, and
/// plain sense). So the fixture is written here, in the shape the reader answers with, and pushed into
/// <c>inspecting</c> / <c>publishing</c> before the draw. What that cannot test is the *pipeline*; that
/// is measured end to end by hand and written down with its numbers
/// ([file-geodatabase-readers.md](../../docs/research/file-geodatabase-readers.md) §8g). What it does
/// test is every screen defect a design review found, which is the half that regresses silently.
/// </para>
/// <para>
/// <b>Four findings from the review of 2026-08-19, each as the check that would have caught it.</b>
/// The worst was invisible to me and obvious to a measurement: <c>label.field</c> uppercases, the
/// service-name field puts its explanation inside that label as a <c>.val</c>, and <c>.val</c> never
/// reset <c>text-transform</c> — so the URL echo whose entire purpose is *see the case you typed before
/// the service exists* rendered <c>/REST/SERVICES/HOSTED/PROJECT_INFORMATION/FEATURESERVER</c>. It
/// looked deliberate in a screenshot.
/// </para>
/// <para>
/// <b>Nothing here writes.</b> The state is injected and the assertions read; no upload happens, no job
/// is opened, and the *Publish* button is never pressed — pressing it would put layers in the operator's
/// datastore, which is the rule the whole suite is built on.
/// </para>
/// </remarks>
public sealed class GeodatabaseScreenTests : ConsoleTest
{
    /// <summary>
    /// What the reader answers with, in the shape the inspection job stores.
    /// </summary>
    /// <remarks>
    /// <b>Taken from a real answer rather than invented.</b> The names, the <c>wkb…25D</c> geometry
    /// spellings, the attachment table with <c>wkbNone</c> and the empty feature class are all shapes
    /// the owner's archives actually produced — including the 47-character feature-class name, which is
    /// what makes the column-width assertions mean anything.
    /// </remarks>
    private const string Inspected = """
        {"ok":true,"layers":[
          {"name":"OMSF_Extension","geometry":"wkbMultiPolygon25D","features":1,"srid":2952,
           "fields":[1,2,3]},
          {"name":"HaLRT_Locate_Areas","geometry":"wkbMultiPolygon25D","features":51,"srid":2952,
           "fields":[1,2,3,4]},
          {"name":"AECOM_Tree_Inventory_Frid_Street_Alignment_2024","geometry":"wkbPoint",
           "features":2026,"srid":2952,"fields":[1,2,3]},
          {"name":"OHN_Watercourse","geometry":"wkbMultiLineString25D","features":3659,"srid":2952,
           "fields":[1]},
          {"name":"Environmental_Land_Classifications_AECOM_2023","geometry":"wkbMultiPolygon",
           "features":70,"srid":2952,"fields":[1,2]},
          {"name":"AECOM_Monitoring_Well_Inventory","geometry":"wkbPoint","features":0,"srid":2952,
           "fields":[1,2]},
          {"name":"AECOM_Arch_ATTACH","geometry":"wkbNone","features":44,"srid":null,"fields":[1,2]}
        ],"messages":[]}
        """;

    /// <summary>A publish that partly failed, which is the state worth drawing carefully.</summary>
    /// <remarks>
    /// <b>The long refusal is the one that broke the table.</b> `42701` carries a 62-character
    /// identifier with no space in it, and the failed row was rendered in <c>.val</c>'s monospace — so
    /// the cell set a minimum width the dialog could not give it and the message was cut mid-word at
    /// both 1024 and 1440 pixels. The collision itself is D-105, fixed; a provider message reaching
    /// this column is not, so the column has to survive one.
    /// </remarks>
    private const string Published = """
        {"service":"Project_Information","published":2,"of":4,"layers":[
          {"layer":"OHN_Watercourse","published":true,"rows":3659,"flattened":3659},
          {"layer":"AECOM_Archeological_Assessment_Results","published":false,
           "why":"42701: column \"fid_aecom_arch_previouslyassessedarchaeolgicalassessmentarea\" specified more than once"},
          {"layer":"Environmental_Land_Classifications_AECOM_2023","published":true,"rows":70,
           "flattened":0},
          {"layer":"AECOM_Monitoring_Well_Inventory","published":false,
           "why":"'AECOM_Monitoring_Well_Inventory' holds no features, and this server builds a hosted table's columns by reading them — so there is nothing here to build one from."}
        ]}
        """;

    /// <summary>Opens the selection screen with the fixture in it.</summary>
    /// <remarks>
    /// <b><c>inspecting</c> and <c>itemStep</c> are top-level <c>let</c> bindings in a classic
    /// script</b>, so they are not on <c>window</c> and are reachable only from a global eval — which is
    /// what the debugger's <c>Runtime.evaluate</c> does. That is the whole mechanism this class runs on.
    /// </remarks>
    private async Task ChooseAsync(string token)
    {
        await OpenAsync("/studio/#/content", token);

        await Browser.EvaluateAsync<bool>(
            $$"""
              (() => {
                openAddItem();
                inspecting = {
                  opened: { job: "11111111-1111-1111-1111-111111111111", watch: "/admin/jobs/1111" },
                  asked: { name: "Project_Information", sharing: "organization" },
                  since: Date.now(), status: "done", error: null, picked: null,
                  job: { status: "done", detail: {{JsonSerializer.Serialize(Inspected)}} },
                };
                itemStep = "inspect";
                drawAddItem();
                return true;
              })()
              """);

        await WaitForAsync(
            "document.getElementById('gdbEcho') !== null",
            "The selection screen never drew. Either `drawInspected` threw — check the page errors — or "
            + "the fixture no longer matches the shape the reader answers with.");
    }

    /// <summary>
    /// The service name and the address it becomes are shown in the case they were typed.
    /// </summary>
    /// <remarks>
    /// <b>This is the finding, and the assertion is `text-transform`, not a screenshot.</b> A service
    /// name is a URL segment; the echo exists so an operator sees <c>Project Information</c> become an
    /// address before the service exists rather than after. Uppercased, it cannot do that job at all —
    /// and it also shouted a three-line sentence, which this stylesheet reserves for 11px labels.
    /// Asserted on the sentence as well as the echo, because the fix is on <c>.val</c> and a fix
    /// applied only to the echo would leave the sentence shouting.
    /// </remarks>
    [Fact]
    public async Task The_service_name_and_its_address_are_not_shouted()
    {
        (string token, _) = await SignInAsync();

        await ChooseAsync(token);

        await WaitForAsync(
            """
            (() => {
              const echo = document.getElementById('gdbEcho');
              const sentence = echo.closest('span.val');
              return getComputedStyle(echo).textTransform === 'none'
                  && getComputedStyle(sentence).textTransform === 'none'
                  && echo.textContent === 'Project_Information';
            })()
            """,
            "The URL echo or the sentence around it is being transformed. `label.field` uppercases and "
            + "this content lives inside one, so `.val` has to reset `text-transform` — otherwise the "
            + "control that shows an operator the case they typed cannot show it.");

        // <b>And it follows the field</b>, which is the other half of the control. A static echo would
        // pass the assertion above and be useless.
        await Browser.EvaluateAsync<bool>(
            """
            (() => {
              const field = document.getElementById('gdbService');
              field.value = 'Hamilton LRT';
              field.dispatchEvent(new Event('input', { bubbles: true }));
              return true;
            })()
            """);

        await WaitForAsync(
            "document.getElementById('gdbEcho').textContent === 'Hamilton LRT'",
            "The echo did not follow the field, so it is decoration rather than a control.");

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }

    /// <summary>
    /// Every row of the selection table is the same height, and each unpublishable one says why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two findings, one table, and both are about a reader rather than a rule.</b> The geometry
    /// column was auto-sized a few pixels narrower than <c>MultiPolygon Z</c> needs, so identical
    /// two-word values wrapped on some rows and not others — measured 77.5, 77.5, 54.25, 77.5, 54.25
    /// down one table. This console has said what that is worth once already: *a ragged table reads as
    /// a bug*.
    /// </para>
    /// <para>
    /// <b>And the reason a row cannot be ticked was a <c>title</c>, which is a mouse.</b> A keyboard
    /// reader could not reach it and a screen reader could not be relied on to speak it, so the same
    /// sentence is an <c>aria-label</c> now. The dash is deliberately not focusable: it is a fact in a
    /// dense table, not a control, and giving every disabled row a tab stop would cost more than it
    /// pays.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_selection_table_is_even_and_says_why_a_row_cannot_be_ticked()
    {
        (string token, _) = await SignInAsync();

        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new
        {
            width = 1024,
            height = 768,
            deviceScaleFactor = 1,
            mobile = false,
        });

        try
        {
            await ChooseAsync(token);

            // <b>The claim is *no short value wraps*, not *every row is the same height*.</b> The
            // first version of this test asserted equal heights and failed for the right reason:
            // `AECOM_Tree_Inventory_Frid_Street_Alignment_2024` is 47 characters and wraps in a
            // 680-pixel dialog, which is exactly what the name column is for. What must not wrap is a
            // value from a fixed vocabulary — `MultiPolygon Z` against a column auto-sized for
            // `MultiPolygon` — because two identical-looking values then set two different row heights
            // and the table reads as broken.
            await WaitForAsync(
                """
                (() => {
                  const rows = [...document.querySelectorAll('.gdbpick tbody tr')];
                  if (rows.length !== 7) return false;

                  // <b>How many vertical offsets the cell's content occupies</b>, measured with a
                  // Range. Two things this went through first, both wrong: a cell's own height is the
                  // *row's* height, so the row whose 47-character name wraps reports every one of its
                  // cells as two lines; and counting rects counts two for a cell holding an element,
                  // because a Range gives a rect for the element box and one for its line box, at the
                  // same offset. Distinct tops is the measure that means *wrapped*.
                  const offsets = cell => {
                    const range = document.createRange();
                    range.selectNodeContents(cell);
                    return new Set([...range.getClientRects()].map(r => Math.round(r.top))).size;
                  };

                  return rows.every(row =>
                    [3, 4, 5, 6].every(n => offsets(row.querySelector(`td:nth-child(${n})`)) === 1));
                })()
                """,
                "A short controlled-vocabulary value is wrapping or being clipped — `MultiPolygon Z` "
                + "against a column auto-sized for `MultiPolygon`. Every column but the feature-class "
                + "name needs `white-space: nowrap`; the name is the one with genuinely long content "
                + "and is allowed the extra line.");

            // <b>One row cannot be ticked, and it was two until D-106 closed the same afternoon.</b>
            // The empty feature class became publishable — the archive declares its fields, so it lands
            // as an empty layer — leaving only the attachment table, which has no geometry and
            // therefore nothing to create a column as. This test asserted two and failed on the change,
            // which is the assertion doing its job: the fixture's shapes did not move, the rule did.
            string[] said = await Browser.EvaluateAsync<string[]>(
                """
                [...document.querySelectorAll('.gdbpick td.tick span.val')]
                  .map(s => s.getAttribute('aria-label') || '')
                """) ?? [];

            Assert.Single(said);

            Assert.All(said, one => Assert.StartsWith("Cannot be published:", one, StringComparison.Ordinal));
            Assert.Contains("no geometry", said[0], StringComparison.Ordinal);

            // And the empty one is offered, with its count reading as a schema rather than as a zero.
            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    """
                    (() => {
                      const rows = [...document.querySelectorAll('.gdbpick tbody tr')];
                      const empty = rows.find(r => r.textContent.includes('Monitoring_Well'));
                      return empty !== null
                          && empty.querySelector('.gdbPick') !== null
                          && /schema only/i.test(empty.textContent);
                    })()
                    """),
                "The empty feature class is not offered, or its row does not say that publishing it "
                + "creates a schema rather than nothing. `0` in a features column reads as a fault.");

            // <b>The page must not carry the navigation column out sideways</b> — the table has its own
            // scroller for that, which is the rule `ScreenReviewTests` asserts for every other screen.
            await WaitForAsync(
                "document.documentElement.scrollWidth <= window.innerWidth",
                "The selection screen scrolls the page sideways in a 1024-pixel window.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }

    /// <summary>
    /// A refusal from the datastore fits inside its cell, at both window sizes.
    /// </summary>
    /// <remarks>
    /// <b>Cut mid-word at 1440 as well as at 1024, which is why this is a defect and not a small
    /// screen's problem.</b> The message was <c>42701: column "fid_aecom_arch_previously…</c> and
    /// stopped; the rest was reachable only by scrolling a table with no affordance saying it could
    /// scroll. One unbroken 62-character identifier in a monospace cell did it. The refusal is the most
    /// operator-relevant text on the screen and it was the one text being hidden.
    /// </remarks>
    [Theory]
    [InlineData(1024, 768)]
    [InlineData(1440, 900)]
    public async Task A_refusal_fits_its_cell(int width, int height)
    {
        (string token, _) = await SignInAsync();

        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new
        {
            width,
            height,
            deviceScaleFactor = 1,
            mobile = false,
        });

        try
        {
            await OpenAsync("/studio/#/content", token);

            await Browser.EvaluateAsync<bool>(
                $$"""
                  (() => {
                    openAddItem();
                    publishing = {
                      opened: { job: "22222222-2222-2222-2222-222222222222",
                                watch: "/admin/jobs/2222" },
                      service: "Project_Information",
                      since: Date.now() - 41000, status: "failed", error: null,
                      job: { status: "failed", failure: "2 of 4 layers were published.",
                             detail: {{JsonSerializer.Serialize(Published)}} },
                    };
                    itemStep = "publish";
                    drawAddItem();
                    return true;
                  })()
                  """);

            await WaitForAsync(
                "document.querySelector('.gdbreport tbody tr') !== null",
                "The report screen never drew.");

            await WaitForAsync(
                """
                (() => {
                  const cell = [...document.querySelectorAll('.gdbreport td')]
                    .find(c => c.textContent.includes('42701'));
                  if (!cell) return false;
                  const box = cell.closest('.widetable');
                  return cell.scrollWidth <= cell.clientWidth
                      && box.scrollWidth <= box.clientWidth
                      && document.documentElement.scrollWidth <= window.innerWidth;
                })()
                """,
                $"At {width}×{height} the datastore's refusal does not fit its cell, so it is cut "
                + "mid-word and the rest is behind a scroller with nothing to say it is there. A long "
                + "unbroken identifier needs `overflow-wrap: anywhere`, which `.routetable` already "
                + "does for a long path.");

            // <b>The footer stays reachable</b>, because a report nobody can dismiss without scrolling
            // past a table is the shape of defect this dialog already fixed once.
            await WaitForAsync(
                """
                (() => {
                  const foot = document.getElementById('addItemFoot').getBoundingClientRect();
                  return foot.height > 0 && foot.bottom <= window.innerHeight;
                })()
                """,
                "The report's footer is below the fold.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }
}
