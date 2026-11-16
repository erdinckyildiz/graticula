using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Features;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.26, 2026-11-17: what still kept real forms out after §5.25 — a recording in a label, the distress scale, a
/// warning, a grid, the camera a photo is taken with, a field type or length an attachment or a question cannot use,
/// <c>bind::type</c>, a word after <c>begin group</c>, a range's parameters on a number, an image's field-app parameters,
/// a nameless calculating note, a question with a hint and no label, a choices row with no list, a <c>.bmp</c> picture,
/// and pyxform's other spellings of a type — is accepted with its behaviour or a notice; what Survey123 and pyxform
/// both refuse stays refused.
/// </summary>
public sealed class AFormTakesWhatRealFormsStillCarryTests
{
    private static readonly string[] Header =
    [
        "type", "name", "label", "hint", "appearance", "parameters", "calculation", "bind::esri:fieldType", "bind::esri:fieldLength",
        "bind::esri:parameters", "bind::esri:warning", "bind::esri:warning_message", "media::audio", "bind::type",
    ];

    private static readonly string[][] Colours = [["renk", "kirmizi", "Kırmızı"], ["renk", "mavi", "Mavi"]];

    /// <summary>A survey row by column name; the rest are empty.</summary>
    private static string[] Row(params (string Column, string Value)[] cells)
    {
        string[] row = new string[Header.Length];
        Array.Fill(row, string.Empty);

        foreach ((string column, string value) in cells)
        {
            row[Array.IndexOf(Header, column)] = value;
        }

        return row;
    }

    private static FormModel Form(string[][] survey, string[][]? choices = null, string[]? choiceHeader = null, string[][]? settings = null) =>
        new([
            new ModelSheet("survey", Header, survey.Select(r => (IReadOnlyList<string>)r).ToList()),
            new ModelSheet("choices", choiceHeader ?? ["list_name", "name", "label"], (choices ?? Colours).Select(r => (IReadOnlyList<string>)r).ToList()),
            .. settings is null ? Array.Empty<ModelSheet>() : [new ModelSheet("settings", settings[0], settings.Skip(1).Select(r => (IReadOnlyList<string>)r).ToList())],
        ]);

    private static FormCheck Checked(params string[][] survey) => FormDefinition.Check(Form(survey));

    private static FormDefinition Accepted(FormCheck check)
    {
        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => $"{r.Sheet} {r.Row} {r.Column}: {r.Message}")));
        return check.Definition!;
    }

    private static void RefusedAt(FormCheck check, string sheet, int row, string column, string words)
    {
        Assert.False(check.Accepted);
        Assert.Contains(check.Refusals, r => r.Sheet == sheet && r.Row == row && string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase)
            && r.Message.Contains(words, StringComparison.OrdinalIgnoreCase));
    }

    private static void NoticeAt(FormCheck check, string sheet, int row, string column, string words) =>
        Assert.Contains(check.Notices, n => n.Sheet == sheet && n.Row == row && string.Equals(n.Column, column, StringComparison.OrdinalIgnoreCase)
            && n.Message.Contains(words, StringComparison.OrdinalIgnoreCase));

    private static JsonObject Client(FormDefinition form, int index = 0) =>
        (JsonObject)FormCompiled.Json(form)["root"]!["body"]!.AsArray()[index]!;

    // ---------------------------------------------------------------------------------------------- audio in a label

    [Fact]
    public void A_recording_in_a_label_or_a_choice_is_named_for_the_client_and_must_be_among_the_forms_files()
    {
        FormModel model = Form(
            [Row(("type", "select_one kus"), ("name", "kus"), ("label", "Hangi kuş?"), ("media::audio", "soru.mp3"))],
            [["kus", "karga", "Karga", "karga.mp3"], ["kus", "serce", "Serçe", ""]],
            ["list_name", "name", "label", "media::audio"]);
        FormDefinition form = Accepted(FormDefinition.Check(model));

        Assert.Equal("soru.mp3", form.Root.Questions[0].Audio[""]);
        Assert.Equal("karga.mp3", form.ChoiceLists["kus"][0].Audio[""]);
        Assert.Empty(form.ChoiceLists["kus"][1].Audio);
        Assert.Equal("soru.mp3", (string?)Client(form)["audio"]![""]);
        Assert.Equal("karga.mp3", (string?)FormCompiled.Json(form)["choices"]!["kus"]![0]!["audio"]![""]);

        // Publishing looks for each among the item's files, as it does for pictures.
        Assert.Contains(form.MediaReferences(), m => m == ("survey", 2, "media::audio", "soru.mp3"));
        Assert.Contains(form.MediaReferences(), m => m == ("choices", 2, "media::audio", "karga.mp3"));

        // The older column 'audio' (pyxform's alias) is read the same way.
        FormCheck older = FormDefinition.Check(new FormModel([
            new ModelSheet("survey", ["type", "name", "label", "audio"], [["note", "n", "Dinleyin", "kus.wav"]]),
        ]));
        Assert.Equal("kus.wav", Accepted(older).Root.Questions[0].Audio[""]);
    }

    [Theory]
    [InlineData("soru.wma", "a recording is")]
    [InlineData("../soru.mp3", "no folder")]
    [InlineData("https://example.org/soru.mp3", "no web address")]
    public void A_recording_a_browser_cannot_play_or_that_is_not_a_file_of_the_form_is_refused(string name, string words) =>
        RefusedAt(Checked(Row(("type", "note"), ("name", "n"), ("label", "Dinleyin"), ("media::audio", name))), "survey", 2, "media::audio", words);

    [Fact]
    public void A_recording_on_a_group_is_refused_because_a_group_is_a_heading()
    {
        FormCheck check = Checked(
            Row(("type", "begin group"), ("name", "g"), ("label", "G"), ("media::audio", "a.mp3")),
            Row(("type", "text"), ("name", "t"), ("label", "T")),
            Row(("type", "end group")));
        RefusedAt(check, "survey", 2, "media::audio", "belongs on a question or a choice");
    }

    // ---------------------------------------------------------------------------------------------- audio questions

    [Fact]
    public void An_audio_question_takes_max_duration_and_records_for_ten_minutes_when_it_does_not_say()
    {
        FormDefinition form = Accepted(Checked(
            Row(("type", "audio"), ("name", "kayit"), ("label", "Kayıt"), ("bind::esri:parameters", "max-duration=90")),
            Row(("type", "audio"), ("name", "uzun"), ("label", "Uzun")),
            Row(("type", "audio"), ("name", "codec"), ("label", "Codec"), ("bind::esri:parameters", "codec=aac max-duration=5"))));

        Assert.Equal([90, 600, 5], form.Root.Questions.Select(q => q.MaxDuration ?? 0));
        Assert.Equal(90, (int)Client(form)["maxDuration"]!);
        Assert.Null(Accepted(Checked(Row(("type", "text"), ("name", "t"), ("label", "T")))).Root.Questions[0].MaxDuration);
    }

    [Theory]
    [InlineData("max-duration=0", "seconds from 1 to 86400")]
    [InlineData("max-duration=ten", "seconds from 1 to 86400")]
    [InlineData("max-size=5", "max-size")]
    public void A_wrong_max_duration_or_a_parameter_an_audio_question_does_not_take_is_refused(string parameters, string words) =>
        RefusedAt(Checked(Row(("type", "audio"), ("name", "a"), ("label", "A"), ("bind::esri:parameters", parameters))), "survey", 2, "bind::esri:parameters", words);

    [Fact]
    public void Codec_is_accepted_with_a_notice_that_the_browser_chooses_the_format() =>
        NoticeAt(Checked(Row(("type", "audio"), ("name", "a"), ("label", "A"), ("bind::esri:parameters", "codec=aac"))), "survey", 2, "bind::esri:parameters", "field app alone");

    // ---------------------------------------------------------------------------------------------- distress

    [Fact]
    public void Distress_is_supported_on_an_integer_and_refused_elsewhere()
    {
        FormDefinition form = Accepted(Checked(Row(("type", "integer"), ("name", "aci"), ("label", "Acı"), ("appearance", "distress"))));
        Assert.Equal(["distress"], Client(form)["appearance"]!.AsArray().Select(a => (string?)a));

        RefusedAt(Checked(Row(("type", "decimal"), ("name", "d"), ("label", "D"), ("appearance", "distress"))), "survey", 2, "appearance", "applies to integer");
    }

    // ---------------------------------------------------------------------------------------------- warning

    [Fact]
    public void A_warning_and_its_message_go_to_the_client_and_its_expression_is_checked()
    {
        FormDefinition form = Accepted(Checked(
            Row(("type", "text"), ("name", "proje"), ("label", "Proje"), ("bind::esri:warning", "${proje} != 'Project'"), ("bind::esri:warning_message", "Bir proje adı yazın."))));

        Assert.Equal("${proje} != 'Project'", form.Root.Questions[0].Warning);
        Assert.Equal("${proje} != 'Project'", (string?)Client(form)["warning"]);
        Assert.Equal("Bir proje adı yazın.", (string?)Client(form)["warningMessages"]![""]);

        RefusedAt(Checked(Row(("type", "text"), ("name", "a"), ("label", "A"), ("bind::esri:warning", "${yok} = 1"))), "survey", 2, "bind::esri:warning", "names no question");
        RefusedAt(Checked(Row(("type", "text"), ("name", "a"), ("label", "A"), ("bind::esri:warning_message", "Dikkat"))), "survey", 2, "bind::esri:warning_message", "has none");
        RefusedAt(Checked(Row(("type", "note"), ("name", "n"), ("label", "N"), ("bind::esri:warning", "1 = 1"))), "survey", 2, "bind::esri:warning", "a note has none");
        RefusedAt(Checked(Row(("type", "calculate"), ("name", "c"), ("calculation", "1"), ("bind::esri:warning", "1 = 1"))), "survey", 2, "bind::esri:warning", "is not shown");
    }

    // ---------------------------------------------------------------------------------------------- grid

    [Fact]
    public void A_grid_style_is_sent_to_the_client_and_widths_are_quiet_under_it_and_said_to_do_nothing_without_it()
    {
        string[][] survey =
        [
            Row(("type", "begin group"), ("name", "g"), ("label", "G"), ("appearance", "w2")),
            Row(("type", "text"), ("name", "a"), ("label", "A"), ("appearance", "w1")),
            Row(("type", "end group")),
        ];

        FormCheck gridded = FormDefinition.Check(Form(survey, settings: [["style"], ["fixed-grid"]]));
        Assert.Equal("fixed", Accepted(gridded).Settings.Grid);
        Assert.Equal("fixed", (string?)FormCompiled.Json(gridded.Definition!)["grid"]);
        Assert.DoesNotContain(gridded.Notices, n => n.Message.Contains("width", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("dynamic", Accepted(FormDefinition.Check(Form(survey, settings: [["style"], ["theme-grid"]]))).Settings.Grid);

        FormCheck plain = FormDefinition.Check(Form(survey));
        Assert.Null(Accepted(plain).Settings.Grid);
        NoticeAt(plain, "survey", 2, "appearance", "no grid style");
    }

    [Fact]
    public void Grid_written_as_an_appearance_is_ignored_in_words()
    {
        FormCheck check = Checked(
            Row(("type", "begin group"), ("name", "g"), ("label", "G"), ("appearance", "grid w2")),
            Row(("type", "text"), ("name", "a"), ("label", "A")),
            Row(("type", "end group")));
        Accepted(check);
        NoticeAt(check, "survey", 2, "appearance", "'grid' is not an appearance");
    }

    [Theory]
    [InlineData("thousands-sep", "decimal")]
    [InlineData("no-ticks", "range")]
    [InlineData("compact-2", "select_one renk")]
    public void Survey123s_display_only_appearances_are_drawn_the_default_way_with_a_notice(string appearance, string type)
    {
        FormCheck check = Checked(Row(("type", type), ("name", "q"), ("label", "Q"), ("appearance", appearance)));
        Accepted(check);
        NoticeAt(check, "survey", 2, "appearance", "drawn the default way");
    }

    // ---------------------------------------------------------------------------------------------- camera

    [Theory]
    [InlineData("image", "new")]
    [InlineData("audio", "new")]
    [InlineData("video", "new")]
    [InlineData("image", "new-front")]
    [InlineData("image", "new-rear")]
    public void The_camera_appearances_are_supported_on_the_media_they_take(string type, string appearance) =>
        Accepted(Checked(Row(("type", type), ("name", "q"), ("label", "Q"), ("appearance", appearance))));

    [Fact]
    public void New_front_on_a_file_is_refused() =>
        RefusedAt(Checked(Row(("type", "file"), ("name", "q"), ("label", "Q"), ("appearance", "new-front"))), "survey", 2, "appearance", "applies to image");

    // ---------------------------------------------------------------------------------------------- field type and length

    [Fact]
    public void An_attachment_question_given_esriFieldTypeString_or_null_is_an_attachment_and_makes_no_field()
    {
        FormCheck check = Checked(
            Row(("type", "image"), ("name", "foto"), ("label", "Foto"), ("bind::esri:fieldType", "esriFieldTypeString"), ("bind::esri:fieldLength", "255")),
            Row(("type", "audio"), ("name", "ses"), ("label", "Ses"), ("bind::esri:fieldType", "null")));
        FormDefinition form = Accepted(check);
        NoticeAt(check, "survey", 2, "bind::esri:fieldType", "stored as an attachment");
        Assert.DoesNotContain(FormLayerPlan.For(form, "Form").Layers[0].Fields, f => f.Question is "foto" or "ses");

        RefusedAt(Checked(Row(("type", "image"), ("name", "f"), ("label", "F"), ("bind::esri:fieldType", "esriFieldTypeInteger"))), "survey", 2, "bind::esri:fieldType", "attachment");
    }

    [Fact]
    public void A_field_length_of_null_keeps_the_usual_length_with_a_notice()
    {
        FormCheck check = Checked(Row(("type", "text"), ("name", "t"), ("label", "T"), ("bind::esri:fieldLength", "null")));
        FormDefinition form = Accepted(check);
        NoticeAt(check, "survey", 2, "bind::esri:fieldLength", "not a length");
        Assert.Equal(FormLayerPlan.DefaultTextLength, FormLayerPlan.For(form, "Form").Layers[0].Fields.Single(f => f.Question == "t").Length);

        RefusedAt(Checked(Row(("type", "text"), ("name", "t"), ("label", "T"), ("bind::esri:fieldLength", "abc"))), "survey", 2, "bind::esri:fieldLength", "whole number");
    }

    // ---------------------------------------------------------------------------------------------- bind::type

    [Fact]
    public void Bind_type_chooses_a_calculations_field_agrees_with_a_field_type_and_must_match_a_questions_own_type()
    {
        FormCheck check = Checked(
            Row(("type", "integer"), ("name", "erkek"), ("label", "Erkek")),
            Row(("type", "calculate"), ("name", "toplam"), ("calculation", "${erkek} + 1"), ("bind::type", "int")),
            Row(("type", "calculate"), ("name", "oran"), ("calculation", "${erkek} div 2"), ("bind::type", "decimal"), ("bind::esri:fieldType", "esriFieldTypeDouble")),
            Row(("type", "text"), ("name", "ad"), ("label", "Ad"), ("bind::type", "string")));
        FormDefinition form = Accepted(check);

        Assert.Equal("esriFieldTypeInteger", form.Root.Questions.Single(q => q.Name == "toplam").EsriFieldType);
        Assert.Equal(FieldType.Integer, FormLayerPlan.Stored(form.Root.Questions.Single(q => q.Name == "toplam")));
        NoticeAt(check, "survey", 5, "bind::type", "changes nothing");

        RefusedAt(Checked(Row(("type", "calculate"), ("name", "c"), ("calculation", "1"), ("bind::type", "int"), ("bind::esri:fieldType", "esriFieldTypeString"))), "survey", 2, "bind::type", "disagree");
        RefusedAt(Checked(Row(("type", "text"), ("name", "t"), ("label", "T"), ("bind::type", "int"))), "survey", 2, "bind::type", "another type");
        RefusedAt(Checked(Row(("type", "calculate"), ("name", "c"), ("calculation", "1"), ("bind::type", "geopoint"))), "survey", 2, "bind::type", "int, decimal and string");
    }

    // ---------------------------------------------------------------------------------------------- begin group X

    [Theory]
    [InlineData("begin group pressure", "end group")]
    [InlineData("begin repeat rows", "end repeat")]
    public void A_word_after_begin_group_or_begin_repeat_is_ignored_with_a_notice(string begin, string end)
    {
        FormCheck check = Checked(
            Row(("type", begin), ("name", "g"), ("label", "G")),
            Row(("type", "integer"), ("name", "p"), ("label", "P")),
            Row(("type", end)));
        Accepted(check);
        NoticeAt(check, "survey", 2, "type", "ignored");
    }

    // ---------------------------------------------------------------------------------------------- parameters

    [Fact]
    public void Start_end_and_step_on_an_integer_are_ignored_with_a_notice_and_other_parameters_are_still_refused()
    {
        FormCheck check = Checked(Row(("type", "integer"), ("name", "n"), ("label", "N"), ("parameters", "start=1 end=50 step=1")));
        Assert.Empty(Accepted(check).Root.Questions[0].Parameters);
        NoticeAt(check, "survey", 2, "parameters", "belongs to a range");

        RefusedAt(Checked(Row(("type", "integer"), ("name", "n"), ("label", "N"), ("parameters", "max=4"))), "survey", 2, "parameters", "takes no parameters");
    }

    [Fact]
    public void An_images_watermarks_are_read_with_their_quotes_and_accepted_as_field_app_parameters()
    {
        const string Written = "topLeftWatermark=\"@[datetime:short]&color=white&size18\" bottomLeftWatermark=\"@[latitude longitude:dms]&color=white&size24\" bottomRightWatermark=\"${n}&color=white&size24\"";
        FormCheck check = Checked(
            Row(("type", "text"), ("name", "n"), ("label", "N")),
            Row(("type", "image"), ("name", "on"), ("label", "Ön"), ("bind::esri:parameters", Written)));
        Accepted(check);
        NoticeAt(check, "survey", 3, "bind::esri:parameters", "topLeftWatermark, bottomLeftWatermark, bottomRightWatermark are applied by Survey123's field app alone");

        Assert.Equal(
            [("topLeftWatermark", "@[datetime:short]&color=white&size18"), ("calculationMode", "always")],
            FormDefinitionParameterPairs("topLeftWatermark=\"@[datetime:short]&color=white&size18\", calculationMode=always"));

        RefusedAt(Checked(Row(("type", "image"), ("name", "on"), ("label", "Ön"), ("bind::esri:parameters", "fileName=x"))), "survey", 2, "bind::esri:parameters", "'fileName' is refused");
    }

    private static List<(string, string)> FormDefinitionParameterPairs(string text) =>
        [.. FormDefinition.ParameterPairs(text).Select(p => (p.Key, p.Value))];

    // ---------------------------------------------------------------------------------------------- names and labels

    [Fact]
    public void A_note_with_no_name_that_calculates_is_named_from_its_row_and_stores_nothing()
    {
        FormCheck check = Checked(
            Row(("type", "text"), ("name", "a"), ("label", "A")),
            Row(("type", "note"), ("label", "Açıklama"), ("calculation", "concat('x', ${a})")));
        FormDefinition form = Accepted(check);
        FormQuestion note = form.Root.Questions.Single(q => q.Type == "note");

        Assert.Equal("note_row_3", note.Name);
        Assert.Equal("concat('x', ${a})", note.Calculation);
        Assert.Null(FormLayerPlan.Stored(note));

        RefusedAt(Checked(Row(("type", "note"), ("label", "N"), ("calculation", "1"), ("bind::esri:fieldType", "esriFieldTypeString"))), "survey", 2, "name", "no name");
    }

    [Fact]
    public void A_question_with_a_hint_and_no_label_shows_its_hint_as_its_label_and_one_with_neither_is_refused()
    {
        FormCheck check = Checked(
            Row(("type", "select_one renk"), ("name", "kapali"), ("hint", "Yol kapalı mı?")),
            Row(("type", "note"), ("hint", "Çevrimdışı harita için…")));
        FormDefinition form = Accepted(check);

        Assert.Equal("Yol kapalı mı?", form.Root.Questions[0].Labels[""]);
        Assert.Empty(form.Root.Questions[0].Hints);
        NoticeAt(check, "survey", 2, "label", "its hint is shown as its label");

        RefusedAt(Checked(Row(("type", "text"), ("name", "t"))), "survey", 2, "label", "no label or hint");
    }

    [Fact]
    public void A_choices_row_with_no_list_name_is_left_out_with_a_notice_and_one_with_no_name_is_refused()
    {
        FormModel model = Form(
            [Row(("type", "select_one renk"), ("name", "r"), ("label", "R"))],
            [["renk", "kirmizi", "Kırmızı", ""], ["", "", "", "Slump.bmp"], ["renk", "", "Adsız", ""]],
            ["list_name", "name", "label", "media::image"]);
        FormCheck check = FormDefinition.Check(model);

        NoticeAt(check, "choices", 3, "list_name", "left out");
        RefusedAt(check, "choices", 4, "name", "needs a list_name and a name");
    }

    [Fact]
    public void A_bmp_picture_is_accepted() =>
        Assert.Null(FormDefinition.MediaProblem("Slump.bmp"));

    // ---------------------------------------------------------------------------------------------- pyxform spellings

    [Theory]
    [InlineData("string", "text")]
    [InlineData("int", "integer")]
    [InlineData("trigger", "acknowledge")]
    [InlineData("imei", "deviceid")]
    [InlineData("uri:deviceid", "deviceid")]
    [InlineData("phone number", "text")]
    public void Pyxforms_other_spellings_of_a_type_are_read_as_the_type_with_a_notice(string written, string read)
    {
        FormCheck check = Checked(Row(("type", written), ("name", "q"), ("label", "Q")));
        Assert.Equal(read, Accepted(check).Root.Questions[0].Type);
        NoticeAt(check, "survey", 2, "type", "pyxform");
    }

    [Fact]
    public void A_phone_number_takes_digits_only_unless_the_row_says_otherwise()
    {
        FormQuestion phone = Accepted(Checked(Row(("type", "phone number"), ("name", "tel"), ("label", "Tel")))).Root.Questions[0];
        Assert.Equal(@"regex(., '^\d*$')", phone.Constraint);
        Assert.Equal("Enter numbers only.", phone.Hints[""]);
    }

    [Theory]
    [InlineData("subscriberid", "subscriber id")]
    [InlineData("simserial", "serial number")]
    [InlineData("uri:phonenumber", "phone number")]
    public void The_sims_properties_are_refused_as_types_too(string written, string words) =>
        RefusedAt(Checked(Row(("type", written), ("name", "q"))), "survey", 2, "type", words);

    [Theory]
    [InlineData("select one from renk", "select_one")]
    [InlineData("select all that apply from renk", "select_multiple")]
    [InlineData("select multiple from renk", "select_multiple")]
    [InlineData("select all that apply from renk or specify other", "select_multiple")]
    public void Pyxforms_select_from_spellings_are_read_as_selects(string written, string read)
    {
        FormDefinition form = Accepted(Checked(Row(("type", written), ("name", "q"), ("label", "Q"))));
        Assert.Equal((read, "renk"), (form.Root.Questions[0].Type, form.Root.Questions[0].Argument));
    }

    [Fact]
    public void A_default_language_written_without_its_code_is_read_as_the_language_it_names()
    {
        FormModel model = new([
            new ModelSheet("survey", ["type", "name", "label::English (en)", "label::Türkçe (tr)"], [["text", "a", "A", "A"]]),
            new ModelSheet("settings", ["default_language"], [["English"]]),
        ]);
        FormCheck check = FormDefinition.Check(model);
        Assert.Equal("English (en)", Accepted(check).Settings.DefaultLanguage);
        NoticeAt(check, "settings", 2, "default_language", "without its code");
    }

    // ---------------------------------------------------------------------------------------------- still refused

    [Fact]
    public void A_duplicate_choice_name_under_a_select_multiple_and_a_row_with_a_name_and_no_type_stay_refused()
    {
        FormModel model = Form(
            [Row(("type", "select_multiple renk"), ("name", "r"), ("label", "R"))],
            [["renk", "a", "A"], ["renk", "a", "A2"]]);
        RefusedAt(FormDefinition.Check(model), "choices", 3, "name", "select_multiple");

        RefusedAt(Checked(Row(("name", "See other options here"))), "survey", 2, "type", "no type");
    }
}
