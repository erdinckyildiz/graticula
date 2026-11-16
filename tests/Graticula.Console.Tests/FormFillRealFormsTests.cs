using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's side of ADR-287 §5.26: recording an audio answer, recordings in a label and beside a choice, the
/// distress scale, a warning that lets the answer go, the camera a photo is taken with, and a grid of a group's questions.
/// The server is the stand-in of <see cref="FormFillClientTests.Site"/>; what the real server sends for each is tested in
/// <c>AFormTakesWhatRealFormsStillCarryTests</c>.
/// </summary>
public sealed class FormFillRealFormsTests : IClassFixture<FormFillClientTests.Site>
{
    private readonly FormFillClientTests.Site _site;

    /// <summary>Creates the tests over the site.</summary>
    /// <param name="site">The stand-in server and the browser, opened once for the class.</param>
    public FormFillRealFormsTests(FormFillClientTests.Site site) => _site = site;

    private static JsonObject Q(string name, string type, string label, params string[] appearance) =>
        FormFillClientTests.Form.Question(name, type, label, null, null, null, null, null, appearance);

    private static JsonObject With(JsonObject node, string key, JsonNode? value)
    {
        node[key] = value;
        return node;
    }

    private Task<bool> TypeAsync(string selector, string value) => _site.EvalAsync<bool>($$"""
        (function () {
          var n = document.querySelector('{{selector}}');
          n.focus();
          n.value = {{System.Text.Json.JsonSerializer.Serialize(value)}};
          n.dispatchEvent(new Event('input', { bubbles: true }));
          return true;
        })()
        """);

    // A microphone and a recorder that need no device: getUserMedia gives a stream whose track records being stopped, and
    // MediaRecorder hands over one chunk of audio/webm when it is stopped.
    private const string FakeMicrophone = """
        (function () {
          window.__tracksStopped = 0;
          navigator.mediaDevices.getUserMedia = function () {
            return Promise.resolve({ getTracks: function () { return [{ stop: function () { window.__tracksStopped++; } }]; } });
          };
          window.MediaRecorder = function () { this.state = 'inactive'; this.mimeType = 'audio/webm;codecs=opus'; };
          window.MediaRecorder.prototype.start = function () { this.state = 'recording'; };
          window.MediaRecorder.prototype.stop = function () {
            var self = this;
            self.state = 'inactive';
            setTimeout(function () {
              self.ondataavailable({ data: new Blob([new Uint8Array([26, 69, 223, 163, 1, 2, 3])], { type: 'audio/webm' }) });
              self.onstop();
            }, 10);
          };
          return true;
        })()
        """;

    // ------------------------------------------------------------------------------------------------- recording

    [Fact]
    public async Task An_audio_question_records_with_a_labelled_button_and_the_recording_is_its_answer_kept_in_the_draft()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [With(Q("ses", "audio", "Sesli not"), "maxDuration", 120)]),
        });
        await _site.OpenAsync(id);
        await _site.EvalAsync<bool>(FakeMicrophone);

        // A file can still be chosen, and the Record button says what it records and how long it may be.
        Assert.True(await _site.EvalAsync<bool>("!!document.querySelector('[data-name=ses] input[type=file]')"));
        Assert.Equal("Record", await _site.EvalAsync<string>("document.querySelector('[data-name=ses] [data-focus=record]').textContent"));
        Assert.Contains("2:00", await _site.EvalAsync<string>("document.querySelector('[data-name=ses] .recorder').textContent"));
        string described = await _site.EvalAsync<string>("document.querySelector('[data-name=ses] [data-focus=record]').getAttribute('aria-describedby')") ?? "";
        Assert.Contains("-label", described, StringComparison.Ordinal);

        await _site.EvalAsync<bool>("document.querySelector('[data-name=ses] [data-focus=record]').click(); true");
        await _site.WaitAsync("document.querySelector('[data-name=ses] [data-focus=record]').textContent === 'Stop recording'", "the recording to start");
        Assert.Equal("true", await _site.EvalAsync<string>("document.querySelector('[data-name=ses] [data-focus=record]').getAttribute('aria-pressed')"));
        await _site.WaitAsync("document.querySelector('[data-name=ses] [aria-live=polite]').textContent.indexOf('Recording.') === 0", "the recording to be announced as started");

        await _site.EvalAsync<bool>("document.querySelector('[data-name=ses] [data-focus=record]').click(); true");
        await _site.WaitAsync("document.querySelectorAll('[data-name=ses] .files li audio').length === 1", "the recording to be listed with a player");
        await _site.WaitAsync("document.querySelector('[data-name=ses] [aria-live=polite]').textContent === 'Recording saved.'", "the recording to be announced");
        Assert.Equal(1, await _site.EvalAsync<int>("window.__tracksStopped"));
        Assert.Equal("Record", await _site.EvalAsync<string>("document.querySelector('[data-name=ses] [data-focus=record]').textContent"));
        Assert.EndsWith(".webm", await _site.EvalAsync<string>("document.querySelector('[data-name=ses] .files li span').textContent.split(' (')[0]"));

        await _site.WaitAsync($$"""
            {{FormFillClientTests.Site.IdbAll("drafts")}}.then(function (all) {
              var d = all.filter(function (x) { return x.formId === '{{id}}'; })[0];
              var f = d && d.tree.files.ses && d.tree.files.ses[0];
              return !!f && f.blob instanceof Blob && f.blob.size > 0 && f.type === 'audio/webm';
            })
            """, "the recording to be stored in the draft as a Blob, as a photo is");
    }

    [Fact]
    public async Task A_recording_stops_by_itself_at_max_duration_and_says_so()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [With(Q("ses", "audio", "Kısa not"), "maxDuration", 1)]),
        });
        await _site.OpenAsync(id);
        await _site.EvalAsync<bool>(FakeMicrophone);

        await _site.EvalAsync<bool>("document.querySelector('[data-name=ses] [data-focus=record]').click(); true");
        await _site.WaitAsync("document.querySelector('[data-name=ses] [aria-live=polite]').textContent === 'Recording stopped at the time limit and saved.'", "the time limit to stop the recording", seconds: 10);
        Assert.Equal(1, await _site.EvalAsync<int>("document.querySelectorAll('[data-name=ses] .files li').length"));
    }

    [Fact]
    public async Task A_refused_microphone_is_said_in_words_and_the_file_chooser_stays()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [Q("ses", "audio", "Not")]),
        });
        await _site.OpenAsync(id);
        await _site.EvalAsync<bool>("navigator.mediaDevices.getUserMedia = function () { var e = new Error('no'); e.name = 'NotAllowedError'; return Promise.reject(e); }; true");

        await _site.EvalAsync<bool>("document.querySelector('[data-name=ses] [data-focus=record]').click(); true");
        await _site.WaitAsync("document.querySelector('[data-name=ses] [aria-live=polite]').textContent.indexOf('Microphone permission was refused') === 0", "the refusal to be said");
        Assert.True(await _site.EvalAsync<bool>("!!document.querySelector('[data-name=ses] input[type=file]')"));
    }

    // ------------------------------------------------------------------------------------------------- media in labels

    [Fact]
    public async Task A_recording_in_a_label_and_beside_a_choice_is_played_by_a_labelled_player_and_a_missing_one_is_said()
    {
        JsonObject question = With(FormFillClientTests.Form.Question("kus", "select_one", "Hangi kuş?", list: "kus"), "audio", new JsonObject { [""] = "soru.mp3" });
        JsonObject crow = With(FormFillClientTests.Form.Choice("karga", "Karga"), "audio", new JsonObject { [""] = "karga.mp3" });
        JsonObject robin = With(FormFillClientTests.Form.Choice("ardic", "Ardıç"), "audio", new JsonObject { [""] = "yok.mp3" });
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [question]),
            Choices = new JsonObject { ["kus"] = new JsonArray(crow, robin) },
            Media = new Dictionary<string, byte[]> { ["media/soru.mp3"] = [73, 68, 51, 4, 0], ["media/karga.mp3"] = [73, 68, 51, 4, 1] },
        });
        await _site.OpenAsync(id);

        await _site.WaitAsync("document.querySelectorAll('[data-name=kus] audio').length === 2", "both recordings to be found");
        Assert.Equal("Recording: Hangi kuş?", await _site.EvalAsync<string>("document.querySelector('[data-name=kus] .question-sound audio').getAttribute('aria-label')"));
        Assert.Equal("Recording: Karga", await _site.EvalAsync<string>("document.querySelector('[data-name=kus] .choice-sound audio').getAttribute('aria-label')"));
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=kus] .question-sound audio').hasAttribute('controls')"));
        await _site.WaitAsync("document.querySelector('[data-name=kus]').textContent.indexOf('Recording not available: yok.mp3') >= 0", "the missing recording to be said");

        // The player is beside the choice, not inside its label, so pressing it does not choose.
        Assert.False(await _site.EvalAsync<bool>("!!document.querySelector('[data-name=kus] label audio')"));
    }

    // ------------------------------------------------------------------------------------------------- distress

    [Fact]
    public async Task Distress_is_eleven_radio_buttons_from_0_to_10_that_store_the_number_and_can_be_cleared()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [Q("aci", "integer", "Acınız ne kadar?", "distress")]),
        });
        await _site.OpenAsync(id);

        Assert.Equal(11, await _site.EvalAsync<int>("document.querySelectorAll('[data-name=aci] input[type=radio]').length"));
        Assert.Equal("radiogroup", await _site.EvalAsync<string>("document.querySelector('[data-name=aci] .distress').getAttribute('role')"));
        string labelledBy = await _site.EvalAsync<string>("document.querySelector('[data-name=aci] .distress').getAttribute('aria-labelledby')") ?? "";
        Assert.Equal("Acınız ne kadar?", await _site.EvalAsync<string>($"document.getElementById('{labelledBy}').textContent.replace('*', '').trim()"));
        Assert.Contains("10 is the most", await _site.EvalAsync<string>("document.querySelector('[data-name=aci]').textContent"));

        await _site.EvalAsync<bool>("var r = document.querySelector('[data-name=aci] input[value=\"7\"]'); r.click(); true");
        await _site.WaitAsync("document.querySelector('[data-name=aci] input[value=\"7\"]').parentNode.classList.contains('on')", "7 to be chosen");
        Assert.Equal("7", await _site.EvalAsync<string>("document.querySelector('[data-name=aci] input:checked').value"));

        await _site.EvalAsync<bool>("document.querySelector('[data-name=aci] [data-focus=clear]').click(); true");
        await _site.WaitAsync("!document.querySelector('[data-name=aci] input:checked')", "the answer to be cleared");
        Assert.Equal("opt-0", await _site.EvalAsync<string>("document.activeElement.getAttribute('data-focus')"));
    }

    // ------------------------------------------------------------------------------------------------- warning

    [Fact]
    public async Task A_warning_is_shown_beside_the_answer_it_is_about_and_the_record_is_still_sent()
    {
        JsonObject project = With(With(Q("proje", "text", "Proje adı"), "warning", ". != 'Project'"),
            "warningMessages", new JsonObject { [""] = "Bir proje adı yazın." });
        string id = _site.Publish(new FormFillClientTests.Form { Root = FormFillClientTests.Form.Repeat("", [project]) });
        await _site.OpenAsync(id);

        Assert.Equal("", await _site.EvalAsync<string>("document.querySelector('[data-name=proje] .warning').textContent"));
        await TypeAsync("[data-name=proje] input", "Project");
        await _site.WaitAsync("document.querySelector('[data-name=proje] .warning').textContent === 'Bir proje adı yazın.'", "the warning");
        Assert.Equal("status", await _site.EvalAsync<string>("document.querySelector('[data-name=proje] .warning').getAttribute('role')"));
        Assert.Contains("-warning", await _site.EvalAsync<string>("document.querySelector('[data-name=proje] input').getAttribute('aria-describedby')"));
        Assert.Null(await _site.EvalAsync<string?>("document.querySelector('[data-name=proje] input').getAttribute('aria-invalid')"));

        int before = _site.Adds.Count;
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the record to be sent despite the warning");
        Assert.True(_site.Adds.Count > before);
        Assert.Contains("Project", _site.Adds[^1], StringComparison.Ordinal);

        await _site.OpenAsync(id);
        await TypeAsync("[data-name=proje] input", "Köprü");
        await _site.WaitAsync("document.querySelector('[data-name=proje] .warning').textContent === ''", "no warning for a good answer");
    }

    // ------------------------------------------------------------------------------------------------- camera

    [Fact]
    public async Task New_front_takes_the_photo_with_the_front_camera_and_new_with_the_rear_one()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [Q("on", "image", "Ön", "new-front"), Q("arka", "image", "Arka", "new")]),
        });
        await _site.OpenAsync(id);

        Assert.Equal("user", await _site.EvalAsync<string>("document.querySelector('[data-name=on] input[type=file]').getAttribute('capture')"));
        Assert.Equal("environment", await _site.EvalAsync<string>("document.querySelector('[data-name=arka] input[type=file]').getAttribute('capture')"));
    }

    // ------------------------------------------------------------------------------------------------- grid

    [Fact]
    public async Task Under_a_grid_style_a_groups_width_makes_its_columns_and_a_questions_width_its_span()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                FormFillClientTests.Form.Group("g", "Proje", [Q("a", "text", "A", "w2"), Q("b", "text", "B"), Q("c", "text", "C", "w9")], "w3"),
                FormFillClientTests.Form.Group("h", "Düz", [Q("d", "text", "D", "w2")]),
            ]),
            Settings = """{"grid":"fixed"}""",
        });
        await _site.OpenAsync(id);
        await _site.EvalAsync<bool>("document.documentElement.style.width = '900px'; true");

        Assert.Equal("3", await _site.EvalAsync<string>("document.querySelector('[data-key=g] .grid-group').getAttribute('data-grid-columns')"));
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-key=g] .grid-group').classList.contains('grid-fixed')"));
        Assert.Equal("span 2", await _site.EvalAsync<string>("document.querySelector('[data-name=a]').style.gridColumn.replace(' / auto', '')"));
        Assert.Equal("span 1", await _site.EvalAsync<string>("document.querySelector('[data-name=b]').style.gridColumn.replace(' / auto', '')"));
        Assert.Equal("span 3", await _site.EvalAsync<string>("document.querySelector('[data-name=c]').style.gridColumn.replace(' / auto', '')"));
        // A group with no width is not a grid.
        Assert.False(await _site.EvalAsync<bool>("!!document.querySelector('[data-key=h] .grid-group')"));
        // The labels stay tied to their boxes, so the grid changes nothing a screen reader hears.
        Assert.Equal("A", await _site.EvalAsync<string>("document.querySelector('[data-name=a] input').labels[0].textContent.replace('*', '').trim()"));
    }

    [Fact]
    public async Task Without_a_grid_style_widths_change_nothing()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [FormFillClientTests.Form.Group("g", "Proje", [Q("a", "text", "A", "w2")], "w3")]),
        });
        await _site.OpenAsync(id);

        Assert.False(await _site.EvalAsync<bool>("!!document.querySelector('.grid-group')"));
        Assert.Equal("", await _site.EvalAsync<string>("document.querySelector('[data-name=a]').style.gridColumn"));
    }
}
