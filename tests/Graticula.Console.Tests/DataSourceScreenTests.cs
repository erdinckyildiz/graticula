using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The Data sources screen: what it says a connection is, and what happens after an action.
/// </summary>
/// <remarks>
/// <para>
/// <b>Six findings from the design review of 2026-08-19, and every one of them was about *afterwards*.</b>
/// The screen gained Edit and Remove that day because the owner could not correct a connection string —
/// *"registered db path'ini güncelleyemiyorum sanırım"* — and the actions themselves worked. What did
/// not: a refusal with no `role="alert"` beside a form that had one, a success that said nothing, and
/// focus dropped on the floor by `innerHTML = ""` deleting the button that had just been pressed.
/// </para>
/// <para>
/// <b>Nothing here saves an edit.</b> An update seals a new secret and the old one cannot be read back,
/// so a wrong save breaks the owner's layer with no undo — the same rule the review was given. The one
/// submission is a deliberately unreachable host, which the server refuses before writing.
/// </para>
/// </remarks>
public sealed class DataSourceScreenTests : ConsoleTest
{
    private async Task OpenSourcesAsync()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/sources", token);

        await WaitForAsync(
            "document.querySelectorAll('#sources tr').length > 0",
            "The Data sources table never drew, so nothing below it means anything.");
    }

    /// <summary>
    /// Edit opens on the connection it is editing, with the password left blank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It opened empty.</b> The form asked for the whole connection string and gave nothing to
    /// start from, on the reasoning that the stored one is sealed and cannot be merged into —
    /// which is true of the password and of nothing else. The host, the port, the database and
    /// the user are the thing being corrected; retyping them from memory is how one correction
    /// becomes two mistakes. Owner, 2026-09-05: *edit dediğimde boş oluyor.*
    /// </para>
    /// <para>
    /// <b>The password stays out, and that is not an oversight to be tidied later.</b> Returning
    /// it would hand a credential to anybody with the privilege to register a source; keeping it
    /// server-side and merging would let them repoint the source at a listener of their own and
    /// have this server deliver it. Typing it again is what makes saving require knowing it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Editing_a_source_opens_on_its_connection_without_the_password()
    {
        await OpenSourcesAsync();

        await WaitForAsync(
            "document.querySelectorAll('#sources button[data-source-edit]').length > 0",
            "No source offers an Edit button, so there is nothing to open.");

        await ClickAsync("#sources button[data-source-edit]");

        await WaitForAsync(
            "document.getElementById('dcHost') !== null",
            "The connection dialog did not open.");

        // <b>Fields now, not a string — owner request 2026-09-05.</b> This waited on one box
        // holding an Npgsql string; the dialog asks for the host, the port, the database and the
        // user separately, and the server hands them over already parsed so the browser never
        // takes a connection string apart.
        await WaitForAsync(
            "(document.getElementById('dcHost')?.value || '').length > 0"
            + " && (document.getElementById('dcDatabase')?.value || '').length > 0",
            "The dialog is still empty after opening Edit. It is a correction form: the thing "
            + "being corrected has to be in it.");

        Assert.False(
            string.IsNullOrWhiteSpace(await Browser.EvaluateAsync<string>(
                "document.getElementById('dcUser').value")),
            "The user is empty, so it is one more thing being retyped from memory.");

        // <b>And the password is the one field that stays empty, which is the design.</b>
        Assert.Equal(
            string.Empty,
            await Browser.EvaluateAsync<string>(
                "document.getElementById('dcPassword').value"));

        // <b>Masked, because somebody is going to type it with a colleague at the desk.</b>
        Assert.Equal(
            "password",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('dcPassword').type"));
    }

    /// <summary>
    /// Choosing a database asks the server what is on it, and offering none says why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner, 2026-09-05, describing ArcGIS Pro's dialog: *user pass girdikten sonra comboya
    /// basınca eğer ki her şey doğruysa, bağlanacağı şemayı seçer*.</b> The database is the one
    /// field on this form the server can fill in itself, and it can only do it once the host, the
    /// port and the credential are all right — so filling the list is the test, and it happens at
    /// the moment somebody was going to type the value anyway.
    /// </para>
    /// <para>
    /// <b>A datalist rather than a select, and that is asserted rather than assumed.</b> A
    /// database this account may connect to is not always one it may see in `pg_database`; a
    /// `&lt;select&gt;` would be a control that refuses the correct answer on a server that hides
    /// them. What the server knows has to help without blocking what it does not.
    /// </para>
    /// <para>
    /// <b>Stubbed, because the harness traps every non-GET.</b> This is a POST — it carries a
    /// password and must not be a URL — so `ConsoleTest` answers it and the real endpoint is
    /// covered over HTTP in `DataSourceLifecycleConformanceTests`. What is under test here is
    /// that the page asks, and does something with the answer.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_database_list_is_filled_from_the_server_and_a_refusal_is_said()
    {
        await OpenSourcesAsync();

        await ClickAsync("#sAdd");

        await WaitForAsync(
            "document.getElementById('dcDatabase')?.offsetParent !== null",
            "The connection dialog did not open, or it has no database field.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch;
          window.__answer = {
            outcome: "Usable", message: "3 databases on localhost:5432.",
            databases: ["gis", "miami_dade", "postgres"],
          };
          window.fetch = async (input, init) => {
            const where = typeof input === "string" ? input : (input && input.url) || "";
            if (!where.includes("/admin/datasources/databases")) return real(input, init);
            return new Response(JSON.stringify(window.__answer), {
              status: 200, headers: { "Content-Type": "application/json" },
            });
          };
          return true;
        })();
        """);

        // <b>The host and the user first, because the list cannot be asked for without them —</b>
        // and the dialog says so rather than sending a request that could only fail.
        //
        // <b>Clicked, not focused, and the difference cost a run.</b> `ClickAsync` sends
        // `element.click()`, which fires `click` and neither `mousedown` nor `focus` — so a
        // control listening only for the two a real mouse sends is a control this suite cannot
        // press. The page listens for all three now; this is the one that proves it.
        await ClickAsync("#dcDatabase");

        await WaitForAsync(
            "(document.getElementById('dcResult')?.innerText || '').includes('host')",
            "Pressing the database field with no host in the form did not say why there is "
            + "nothing to list. A control that does nothing and explains nothing reads as broken.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          document.getElementById("dcHost").value = "localhost";
          document.getElementById("dcUser").value = "gis";
          return true;
        })();
        """);

        await ClickAsync("#dcDatabase");

        await WaitForAsync(
            "document.querySelectorAll('#dcDatabases option').length === 3",
            "The database list did not fill from the server's answer.");

        Assert.Equal(
            "gis | miami_dade | postgres",
            await Browser.EvaluateAsync<string>(
                "[...document.querySelectorAll('#dcDatabases option')]"
                + ".map(o => o.value).join(' | ')"));

        // <b>And the field is a text input with a list, not a select.</b>
        Assert.Equal(
            "INPUT",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('dcDatabase').tagName"));

        // <b>The other end: a server that refuses says which of the four things went wrong.</b>
        //
        // <b>The password changes, which is what makes this a second question.</b> The dialog
        // remembers what it last asked about and does not ask again for the same host, port,
        // user and password — three events reach that control for one press of it, and a
        // request per event is three.
        await Browser.EvaluateAsync<bool>("""
        (window.__answer = {
          outcome: "CannotConnect",
          message: "The password was rejected by the server.",
          databases: [],
        }, document.getElementById("dcPassword").value = "wrong", true);
        """);

        await ClickAsync("#dcDatabase");

        await WaitForAsync(
            "document.getElementById('dcResult')?.classList.contains('alert')",
            "A rejected password did not come back red on the dialog.");

        Assert.Contains(
            "password was rejected",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('dcResult').innerText") ?? string.Empty,
            StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The datastore is not offered an Edit button, because editing it would not hold.
    /// </summary>
    /// <remarks>
    /// <b>`EnsureDatastoreAsync` rewrites that row from `Graticula:PlatformStore` on every
    /// start.</b> A change made here worked, said so, and was undone by the next restart — which
    /// is worse than a refusal in every way a control can be. Removal has refused the datastore
    /// since ADR-019 gave it a reserved name; the update path did not, and the button was drawn
    /// for both. ADR-034: a control is not drawn for a feature that does not exist.
    /// </remarks>
    [Fact]
    public async Task The_datastore_row_offers_no_edit()
    {
        await OpenSourcesAsync();

        await WaitForAsync(
            "document.querySelectorAll('#sources tr').length > 0",
            "No sources were listed.");

        int offered = await Browser.EvaluateAsync<int>("""
        [...document.querySelectorAll('#sources tr')]
          .filter(row => (row.querySelector('td.name')?.textContent || '').trim().startsWith('datastore'))
          .filter(row => row.querySelector('button[data-source-edit]'))
          .length
        """);

        Assert.Equal(0, offered);

        // <b>And the row says where the value actually lives.</b> Taking the button away without
        // saying why leaves somebody looking for it.
        string said = await Browser.EvaluateAsync<string>("""
        ([...document.querySelectorAll('#sources tr')]
          .find(row => (row.querySelector('td.name')?.textContent || '').trim().startsWith('datastore'))
          ?.innerText) || ''
        """) ?? string.Empty;

        Assert.Contains("PlatformStore", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The register form stands beside the table rather than under it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The 1c handoff: *table left, register form right with the test result inline*.</b> The
    /// form was below the table and the probe's report below that, so registering a source meant
    /// scrolling past everything already registered to reach the first field, and the answer to
    /// *test this* arrived somewhere the eye was not.
    /// </para>
    /// <para>
    /// <b>Measured, and `offsetParent` is the half that keeps being the defect.</b> Three times
    /// this console has shipped a control that was in the markup and could not be seen, and each
    /// time the suite was green on the element existing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_register_form_stands_beside_the_table()
    {
        await OpenSourcesAsync();

        await WaitForAsync(
            "document.querySelectorAll('#sources tr').length > 0",
            "No sources were listed, so there is no layout to measure.");

        await WaitForAsync(
            "document.getElementById('sourceForm')?.offsetParent !== null",
            "The register form is not on screen.");

        int[] box = await Browser.EvaluateAsync<int[]>("""
        (() => {
          const table = document.getElementById('sources').closest('.panel').getBoundingClientRect();
          const form = document.getElementById('sourceForm').getBoundingClientRect();
          return [
            Math.round(table.right), Math.round(form.left),
            Math.round(table.top), Math.round(form.top),
          ];
        })()
        """) ?? [];

        Assert.True(box.Length == 4, "The layout could not be measured.");

        Assert.True(
            box[1] >= box[0],
            $"The form starts at {box[1]} and the table ends at {box[0]}, so it is under the "
            + "table rather than beside it.");

        Assert.True(
            System.Math.Abs(box[3] - box[2]) < 40,
            $"The table starts at {box[2]} and the form at {box[3]}, "
            + $"{System.Math.Abs(box[3] - box[2])} pixels apart. Side by side means they begin "
            + "together.");
    }

    /// <summary>
    /// A connection test that fails to connect is a red box, not a neutral one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The handoff asks for green, amber and red, and the distinction is what somebody acts
    /// on.</b> `CannotConnect` means nothing about the database is known and the next move is a
    /// host, a port or a password; `InsufficientPrivilege` and `UnusableGeometry` mean the
    /// connection worked and what was found is the problem, which is a different afternoon.
    /// Painting all three failures alike sends an administrator to check a network that is fine.
    /// </para>
    /// <para>
    /// <b>The answer is trapped, because the harness never lets a write reach the server.</b> A
    /// real probe would need a real database to fail against and a real one to succeed against;
    /// what is under test here is the mapping from an outcome to a tone, which is the console's
    /// half. The server's half is <c>ProbeOutcome</c> and its own suites.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_connection_that_cannot_be_reached_is_red_and_one_that_works_is_green()
    {
        await OpenSourcesAsync();

        await WaitForAsync(
            "document.getElementById('sAdd') !== null",
            "The sources screen offers no way to add a connection.");

        // <b>The test button lives in the dialog now — owner request 2026-09-05.</b> It was
        // beside a single connection-string box on the screen itself; the box is gone and the
        // fields are in a modal, so the button that reports on them is in there with them.
        await ClickAsync("#sAdd");

        await WaitForAsync(
            "document.getElementById('dcTest')?.offsetParent !== null",
            "The connection dialog did not open, or it opened without its Test button.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch;
          window.__answer = { outcome: "CannotConnect", message: "No route to that host." };
          window.fetch = async (input, init) => {
            const method = ((init && init.method) || "GET").toUpperCase();
            if (method !== "POST") return real(input, init);
            return new Response(JSON.stringify(window.__answer), {
              status: 200, headers: { "Content-Type": "application/json" },
            });
          };
          return true;
        })();
        """);

        await Browser.EvaluateAsync<bool>("""
        (() => {
          document.getElementById("dcHost").value = "nowhere";
          document.getElementById("dcUser").value = "somebody";
          return true;
        })();
        """);

        await ClickAsync("#dcTest");

        await WaitForAsync(
            "document.getElementById('dcResult')?.classList.contains('alert')",
            "A connection that could not be reached did not come back red. Somebody reading a "
            + "neutral box has no reason to look at the network before the privileges.");

        Assert.Contains(
            "Cannot connect",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('dcResult').innerText") ?? string.Empty,
            System.StringComparison.Ordinal);

        // <b>And the other end, so the box is not simply always red.</b> A single tone that
        // happens to be the one asserted is a test that passes against a screen with no mapping
        // in it at all.
        await Browser.EvaluateAsync<bool>(
            """(window.__answer = { outcome: "Usable", message: "14 tables are publishable." }, true)""");

        await ClickAsync("#dcTest");

        await WaitForAsync(
            "document.getElementById('dcResult')?.classList.contains('ok')",
            "A usable connection did not come back green.");

        // <b>Amber is its own answer.</b> Reached and wrong is not the same as not reached, and
        // the two failures were one colour before this.
        await Browser.EvaluateAsync<bool>(
            """(window.__answer = { outcome: "InsufficientPrivilege", message: "No read on public." }, true)""");

        await ClickAsync("#dcTest");

        await WaitForAsync(
            "document.getElementById('dcResult')?.classList.contains('warn')",
            "A connection that worked but lacked rights came back in the same colour as one that "
            + "could not be reached at all.");
    }

    /// <summary>
    /// Each row says which database it is, and the datastore says why it has no Remove.
    /// </summary>
    /// <remarks>
    /// <b>The column that replaced the id.</b> An id is a string nobody types — every action is a
    /// button — while *which database is this* was on no screen at all, which is how two sources called
    /// the same thing on two hosts became indistinguishable. `summary` is host, port and database;
    /// never the credential, which `DataSourceLifecycleConformanceTests` asserts from the other side.
    /// </remarks>
    [Fact]
    public async Task A_row_says_which_database_it_is()
    {
        await OpenSourcesAsync();

        await WaitForAsync(
            """
            (() => {
              const rows = [...document.querySelectorAll('#sources tr')];
              const datastore = rows.find(r => r.textContent.includes('datastore'));
              if (!datastore) return false;

              // host:port/database, in the second column.
              const SHAPE = /^\S+:[0-9]+\/\S+$/;
              const summary = datastore.children[1].textContent.trim();
              // <b>The setting's name rather than a phrase.</b> This asked for the words
              // *cannot be removed* and broke on 2026-09-05 when the row grew a second thing to
              // explain — Edit went too, because that connection is rewritten from configuration
              // on every start. A test that pins a sentence fails when the sentence improves;
              // `PlatformStore` is the fact the row exists to carry and cannot be reworded away.
              return SHAPE.test(summary)
                  && datastore.textContent.includes('PlatformStore')
                  && !datastore.querySelector('[data-source-remove]');
            })()
            """,
            "The datastore's row does not name its database in `host:port/database` form, or it offers a "
            + "Remove button, or it does not say where its connection comes from. The absence of a "
            + "button reads as a missing button unless the row says otherwise — and the actions are "
            + "right-aligned as a group, so a short row looks like a long one shifted over.");

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }

    /// <summary>
    /// A probe of a database with many tables pages like every other list here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-103](../../docs/architecture-debt.md): this table had no cap, where every other list
    /// in this console pages at ten.</b> The measured case was a source with 77 publishable
    /// tables rendering all 77 inline and taking the page past five thousand pixels; the dev
    /// server's own datastore answers with 78, so the case is not hypothetical and needs no
    /// staging.
    /// </para>
    /// <para>
    /// <b>The count above the table is asserted with the page below it.</b> Paging a list without
    /// saying how long it is replaces one problem with a worse one — a reader who cannot see
    /// the end of a list and is not told where it is.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_probe_of_a_large_database_pages_its_tables()
    {
        await OpenSourcesAsync();

        await ClickAsync("#sources [data-probe]");

        await WaitForAsync(
            "document.getElementById('probeRows')?.querySelectorAll('tr').length > 0",
            "The probe never rendered a table of what it found.");

        int all = await Browser.EvaluateAsync<int>(
            "Number((document.getElementById('probeCount')?.textContent || '0')"
            + ".match(/[0-9]+/)?.[0] || 0)");

        if (all <= 10)
        {
            // A short list is not this row's case, and a pager on one would be exactly the
            // furniture this console deliberately does not draw.
            Assert.Equal(
                0,
                await Browser.EvaluateAsync<int>(
                    "document.getElementById('probePager')"
                    + "?.querySelectorAll('[data-page]').length ?? 0"));

            return;
        }

        int rows = await Browser.EvaluateAsync<int>(
            "document.getElementById('probeRows').querySelectorAll('tr').length");

        Assert.True(
            rows <= 10,
            $"The probe found {all} tables and rendered {rows} of them at once. Every other list "
            + "on this console pages at ten, and this one grew the page without bound.");

        // The strip says which rows, not only which page — the same claim `pagerFor` makes
        // for every list that uses it.
        string strip = await Browser.EvaluateAsync<string>(
            "document.getElementById('probePager').textContent") ?? string.Empty;

        Assert.Contains(
            all.ToString(System.Globalization.CultureInfo.InvariantCulture),
            strip,
            StringComparison.Ordinal);

        string first = await Browser.EvaluateAsync<string>(
            "document.getElementById('probeRows').textContent") ?? string.Empty;

        await ClickAsync("#probePager [data-page-to='1']");

        await WaitForAsync(
            "document.getElementById('probeRows').textContent !== "
            + System.Text.Json.JsonSerializer.Serialize(first),
            "Turning to the second page of the probe left the first page on the screen. A pager "
            + "whose arrows do nothing is a defect this console has already recorded once, on a "
            + "group's members.");

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }

    /// <summary>
    /// Filtering the probe narrows it, and the box is not replaced under the cursor.
    /// </summary>
    /// <remarks>
    /// <b>Paging seventy-eight tables without a filter is eight page turns to find one.</b> So
    /// the cap comes with a way to reach past it, built the way every other filtered list here is
    /// built: the box lives outside the part that is redrawn. The one place this console got that
    /// wrong — the share dialog's search — is recorded in `console.js` as a defect
    /// twice over, and the symptom is a box that loses what was typed on every keystroke.
    /// </remarks>
    [Fact]
    public async Task The_probe_filter_narrows_the_table_without_replacing_itself()
    {
        await OpenSourcesAsync();

        await ClickAsync("#sources [data-probe]");

        await WaitForAsync(
            "document.getElementById('probeFilter') !== null",
            "The probe rendered no filter, so a reader with seventy-eight tables has only "
            + "arrows.");

        // <b>Marked, so the assertion is about this element and not about an element with the
        // same id.</b> A redraw replaces the node; the mark does not survive it.
        await Browser.EvaluateAsync<bool>(
            "(() => { document.getElementById('probeFilter').dataset.mark = 'kept'; "
            + "return true; })()");

        // A table the probe actually found, so the filter is asked for something that exists
        // rather than for a string this test invented.
        string one = await Browser.EvaluateAsync<string>(
            "document.getElementById('probeRows').querySelector('tr td.name')?.textContent || ''")
            ?? string.Empty;

        Assert.False(one.Length == 0, "the probe's first row names no table");

        await FilterAsync("probeFilter", one);

        await WaitForAsync(
            "document.getElementById('probeCount').textContent.includes('match')",
            "Typing in the probe's filter did not narrow anything.");

        Assert.Equal(
            "kept",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('probeFilter')?.dataset.mark || ''"));

        Assert.Equal(
            one,
            await Browser.EvaluateAsync<string>(
                "document.getElementById('probeFilter').value"));

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }

    /// <summary>
    /// Refusing to remove a source that has layers is announced, not only coloured.
    /// </summary>
    /// <remarks>
    /// <b>`role="alert"`, because the form two functions away had it and this did not.</b> A sighted
    /// reader saw red text appear; a screen-reader user heard nothing at all. The inconsistency is what
    /// makes it a defect rather than a limitation — the same screen answered the same kind of question
    /// two different ways.
    /// </remarks>
    [Fact]
    public async Task The_refusal_to_remove_a_source_in_use_is_announced()
    {
        await OpenSourcesAsync();

        // <b>The row that has layers on it, chosen by its layer count rather than by
        // its position — 2026-08-25.</b> This clicked `tr:not(:first-child)`, which is
        // "the second row" and was the datastore on the machine this was written on. In
        // CI the second row was a registration with nothing published from it, so the
        // removal succeeded, nothing was refused, and the test reported that the console
        // had not announced a refusal it was never asked to make.
        //
        // <b>And the datastore can never be the answer</b>: `console.js` omits its
        // Remove button by name, so a selector that lands on it matches nothing at all.
        // The button carries `data-source-layers`, which is the fact this test is about.
        await ClickAsync(
            "#sources [data-source-remove][data-source-layers]:not([data-source-layers=\"0\"])");

        await WaitForAsync(
            """
            (() => {
              const said = document.querySelector('#probe [role=alert]');
              return said !== null
                  && said.offsetParent !== null
                  && /layer/i.test(said.textContent)
                  && /unpublish/i.test(said.textContent);
            })()
            """,
            "Removing a source that still has layers on it did not produce an announced, visible "
            + "message naming what to do. The server refuses this too, but the console refuses it "
            + "first — and a message a screen reader never hears is half a refusal.");

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }

    /// <summary>
    /// Cancelling the edit form leaves focus on the screen rather than on the body.
    /// </summary>
    /// <remarks>
    /// <b>Because `innerHTML = ""` deletes whatever had focus.</b> Measured by the review:
    /// `document.activeElement` was `BODY` after Cancel, so a keyboard reader lost their place and had
    /// to tab from the top of the page. The same clearing happens after a successful removal, and for
    /// the same reason.
    ///
    /// <b>A `&lt;dialog&gt;` gives this away for free, and the test is kept anyway.</b> Since
    /// 2026-09-05 the form is a modal, and closing one returns focus to whatever opened it — so
    /// this now asserts a property of the element rather than of code that was written for it.
    /// It stays because the property is what a keyboard reader depends on, and the next person
    /// to replace the dialog with a panel will find out here rather than from the owner.
    /// </remarks>
    [Fact]
    public async Task Cancelling_the_edit_form_keeps_focus_on_the_screen()
    {
        await OpenSourcesAsync();

        await ClickAsync("#sources [data-source-edit]");

        await WaitForAsync(
            "document.getElementById('dcHost') !== null",
            "The connection dialog never drew.");

        // Opening it puts focus inside it — `showModal` does that, and the prefill then moves it
        // to the password, which is the one field that has to be typed again.
        await WaitForAsync(
            "document.activeElement?.closest('#dbconn') !== null",
            "Nothing inside the dialog has focus, so a keyboard reader is still on the page "
            + "behind it.");

        await ClickAsync("#dcCancel");

        await WaitForAsync(
            """
            (() => {
              const now = document.activeElement;
              return now !== null
                  && now !== document.body
                  && now.closest('#sources') !== null
                  && now.offsetParent !== null;
            })()
            """,
            "After Cancel, focus is not on a visible control inside the sources table — most likely it "
            + "is on `<body>`, because clearing the panel deleted the button that had focus.");

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }

    /// <summary>
    /// The dialog writes to its own source, and its refusal is in view at 1024×768.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this suite can see, which is not what the first version of this test assumed.</b> It
    /// submitted a deliberately unreachable host and waited for the server's refusal — and passed
    /// nothing, because <see cref="ConsoleTest"/> replaces <c>fetch</c> for every method that is not
    /// <c>GET</c> or <c>HEAD</c> and answers <c>{}</c> with a 200. So the console rendered *Saved* while
    /// no request had left the page, the server's audit log had no `datasource.update` in it at all, and
    /// the test was measuring the harness. The server-side refusal is covered where it can be —
    /// <c>DataSourceLifecycleConformanceTests</c>, over real HTTP.
    /// </para>
    /// <para>
    /// <b>So the two claims here are the ones this harness is built to make.</b> First: the recorded
    /// write is a `PUT` to this source's own address — which is the assertion that matters most on this
    /// screen, because the form's contract is that it sends the **whole** string and the field starts
    /// empty by design. Second: the error slot exists, is announced, and lands **in view** at 1024×768,
    /// where the row's three action buttons wrap to two lines and push the form down — measured by the
    /// design review at `top: 724` in a 768-pixel viewport before any browser chrome.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_form_writes_to_its_own_source_and_shows_a_refusal_in_view()
    {
        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new
        {
            width = 1024,
            height = 768,
            deviceScaleFactor = 1,
            mobile = false,
        });

        try
        {
            await OpenSourcesAsync();

            string id = await Browser.EvaluateAsync<string>(
                "document.querySelector('#sources [data-source-edit]')"
                + "?.getAttribute('data-source-edit') || ''") ?? string.Empty;

            Assert.False(string.IsNullOrWhiteSpace(id), "No row offered an Edit button.");

            await ClickAsync("#sources [data-source-edit]");

            await WaitForAsync(
                "document.getElementById('dcHost') !== null", "The connection dialog never drew.");

            // <b>The whole connection, still, and now as fields.</b> The dialog fills every one
            // of them but the password, so what is typed here is the half that has to be.
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  document.getElementById('dcHost').value = 'elsewhere.example';
                  document.getElementById('dcPassword').value = 'not the real one';
                  document.getElementById('dcSave').click();
                  return true;
                })()
                """);

            await WaitForAsync(
                $"(window.__writes || []).some(w => w.startsWith('PUT') && w.includes('{id}'))",
                "The dialog did not write a PUT to this source's own address. The recorded writes "
                + "were: " + string.Join(" | ", await WritesAsync()));

            // <b>The dialog has to be reopened, and finding out why cost a run when this was a
            // panel.</b> The harness answers every write with `{}` and a 200, so the save above
            // took the *success* path and closed it; populating a slot in a closed dialog does
            // nothing, silently. What is being exercised here is the page, and the page has
            // already moved on.
            await ClickAsync("#sources [data-source-edit]");

            await WaitForAsync(
                "document.getElementById('dcResult') !== null", "The dialog did not reopen.");

            // <b>And the slot the refusal lands in is in view at this size.</b> The harness
            // cannot produce a server refusal, so the slot is populated the way the catch block
            // populates it; what is under test is the *position*, which is what the review
            // measured. A modal is the repair for that measurement rather than a way around it:
            // it is centred in the viewport whatever the row behind it has done.
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const said = document.getElementById('dcResult');
                  if (!said) return false;
                  said.className = 'testresult alert';
                  said.innerHTML = '<b>Refused. </b>No host by that name. Check the spelling and, '
                                 + 'if it is a container name, that this server is on the same '
                                 + 'network as the database.';
                  said.scrollIntoView({ block: 'center', behavior: 'instant' });
                  return true;
                })()
                """);

            await WaitForAsync(
                """
                (() => {
                  const said = document.getElementById('dcResult');
                  if (!said || said.offsetParent === null) return false;

                  const box = said.getBoundingClientRect();

                  return box.height > 0 && box.top >= 0 && box.bottom <= window.innerHeight;
                })()
                """,
                "The refusal is missing, hidden, or off screen at 1024×768. A message an operator "
                + "has to scroll to find is a message that arrives after they have started "
                + "guessing.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }

    /// <summary>
    /// The closed drawer holds nothing a keyboard can reach, and the open one does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found immediately downstream of this screen's new buttons</b>, by tabbing past them:
    /// `#drawerClose` was focusable while its `aria-hidden` container sat translated off-canvas —
    /// measured at x=1986 in a 1440-pixel window. `offsetParent` is non-null there, so the check this
    /// repository has relied on three times does not catch it, and a focusable descendant of
    /// `aria-hidden` is a contradiction in itself: the reader can reach something they have been told is
    /// not there.
    /// </para>
    /// <para>
    /// <b>Both directions, because `inert` is a lock and a lock left on is worse.</b> The drawer is how
    /// every settings page opens; asserting only that it is inert when closed would pass a build where
    /// it is inert always.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_closed_drawer_is_inert_and_the_open_one_is_not()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/services", token);

        await WaitForAsync(
            "document.getElementById('drawer') !== null", "The shell never drew.");

        await WaitForAsync(
            """
            (() => {
              const drawer = document.getElementById('drawer');
              const close = document.getElementById('drawerClose');
              if (!drawer || !close) return false;

              return drawer.inert === true
                  && drawer.getAttribute('aria-hidden') === 'true'
                  && close.getBoundingClientRect().left >= window.innerWidth;
            })()
            """,
            "The closed drawer is not inert, so its Close button is a tab stop inside an `aria-hidden` "
            + "container that CSS has moved off screen.");

        // And it comes back. `openDrawer` is reached by any settings page; the services screen's own
        // action is the shortest route to it.
        bool opened = await ClickIfPresentAsync("#newService") || await ClickIfPresentAsync("#newLayer");

        if (opened)
        {
            await WaitForAsync(
                """
                (() => {
                  const drawer = document.getElementById('drawer');
                  return drawer.classList.contains('on')
                      && drawer.inert === false
                      && drawer.getAttribute('aria-hidden') === 'false';
                })()
                """,
                "The drawer opened and stayed inert, which makes every settings page unusable by "
                + "keyboard — the failure mode of adding a lock without the matching release.");
        }

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }
}
