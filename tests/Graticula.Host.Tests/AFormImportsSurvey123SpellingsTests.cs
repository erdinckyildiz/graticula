using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.19, 2026-11-15: what a real Survey123 form says that the first version refused — <c>or_other</c>, a group
/// or repeat that is <c>minimal</c> or <c>compact</c>, <c>autocomplete</c>, duplicate choice names, an input mask, a
/// <c>spinner</c> and a <c>week-number</c> — is now checked, planned and sent to the client, and what is still wrong
/// with them is refused at its row and column.
/// </summary>
public sealed class AFormImportsSurvey123SpellingsTests
{
    private static readonly string[] SurveyHeader = ["type", "name", "label", "appearance", "relevant", "bind::esri:fieldLength", "body::esri:inputMask"];

    private static readonly string[][] Colours = [["renk", "kirmizi", "Kırmızı"], ["renk", "mavi", "Mavi"]];

    private static FormModel Form(string[][] survey, string[][]? choices = null, string[][]? settings = null, string[]? header = null, string[]? choiceHeader = null) =>
        new([
            new ModelSheet("survey", header ?? SurveyHeader, survey.Select(r => (IReadOnlyList<string>)r).ToList()),
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

    // ------------------------------------------------------------------------------------------------------ or_other

    [Theory]
    [InlineData("select_one renk or_other")]
    [InlineData("select_one renk or other")]
    [InlineData("select one renk or_other")]
    public void A_select_one_with_or_other_gets_an_other_choice_and_a_text_question_after_it(string type)
    {
        FormDefinition form = Accepted(Checked([type, "renk", "Renk", "", "", "", ""], ["integer", "adet", "Adet", "", "", "", ""]));

        Assert.Equal(["renk", "renk_other", "adet"], form.Root.Questions.Select(q => q.Name));
        FormQuestion select = form.Root.Questions[0];
        FormQuestion other = form.Root.Questions[1];

        // The list itself gains the choice, so the domain a republish keeps up to date is the one the field already has.
        Assert.Equal("renk", select.Argument);
        Assert.Equal(["kirmizi", "mavi", "other"], form.ChoiceLists["renk"].Select(c => c.Name));
        Assert.True(form.ChoiceLists["renk"][2].Automatic);

        Assert.Equal(("text", "selected(${renk}, 'other')", true), (other.Type, other.Relevant, other.Automatic));
        Assert.Equal("Specify other", other.Labels[""]);
        Assert.Equal("Other", form.ChoiceLists["renk"][2].Labels[""]);
    }

    [Fact]
    public void A_select_multiple_with_or_other_works_the_same_way_and_every_question_over_the_list_offers_the_choice()
    {
        FormDefinition form = Accepted(Checked(
            ["select_multiple renk or_other", "a", "A", "", "", "", ""],
            ["select_multiple renk or_other", "b", "B", "", "", "", ""],
            ["select_one renk", "c", "C", "", "", "", ""]));

        Assert.Equal(["a", "a_other", "b", "b_other", "c"], form.Root.Questions.Select(q => q.Name));
        Assert.Equal(["renk", "renk", "renk"], form.Root.Questions.Where(q => q.Type.StartsWith("select_", StringComparison.Ordinal)).Select(q => q.Argument));
        Assert.Equal(["kirmizi", "mavi", "other"], form.ChoiceLists["renk"].Select(c => c.Name));
    }

    [Fact]
    public void The_other_choice_and_question_are_labelled_in_each_of_the_forms_languages()
    {
        FormModel model = new([
            new ModelSheet("survey", ["type", "name", "label::Türkçe (tr)", "label::English (en)"], [["select_one renk or_other", "renk", "Renk", "Colour"]]),
            new ModelSheet("choices", ["list_name", "name", "label::Türkçe (tr)", "label::English (en)"], [["renk", "kirmizi", "Kırmızı", "Red"]]),
        ]);
        FormDefinition form = Accepted(FormDefinition.Check(model));

        Assert.Equal("Diğerini belirtin", form.Root.Questions[1].Labels["Türkçe (tr)"]);
        Assert.Equal("Specify other", form.Root.Questions[1].Labels["English (en)"]);
        Assert.Equal("Diğer", form.ChoiceLists["renk"][1].Labels["Türkçe (tr)"]);
        Assert.Equal("Other", form.ChoiceLists["renk"][1].Labels["English (en)"]);
    }

    [Fact]
    public void A_list_that_already_has_an_other_choice_is_used_as_it_is()
    {
        FormDefinition form = Accepted(FormDefinition.Check(Form([["select_one renk or_other", "renk", "Renk", "", "", "", ""]],
            [["renk", "kirmizi", "Kırmızı"], ["renk", "other", "Başka"]])));

        Assert.Equal("renk", form.Root.Questions[0].Argument);
        Assert.Equal(["renk", "renk_other"], form.Root.Questions.Select(q => q.Name));
        Assert.False(form.ChoiceLists["renk"][1].Automatic);
    }

    [Fact]
    public void Or_other_is_refused_off_a_select_and_where_its_text_question_would_take_a_name_that_is_taken()
    {
        RefusedAt(Checked(["text or_other", "t", "T", "", "", "", ""]), "survey", 2, "type", "belongs after the choice list");
        RefusedAt(Checked(["select_one_from_file yerler.csv or_other", "t", "T", "", "", "", ""]), "survey", 2, "type", "belongs after the choice list");
        RefusedAt(Checked(["text", "renk_other", "Var", "", "", "", ""], ["select_one renk or_other", "renk", "Renk", "", "", "", ""]), "survey", 3, "type", "already taken");
        RefusedAt(Checked(["select_one renk or_other", "renk", "Renk", "", "", "", ""], ["text", "renk_other", "Sonra", "", "", "", ""]), "survey", 3, "name", "already the name");
    }

    [Fact]
    public void The_layer_plan_makes_a_text_field_for_the_other_question_and_a_domain_with_the_other_code()
    {
        FormDefinition form = Accepted(Checked(["select_one renk or_other", "renk", "Renk", "", "", "", ""]));
        FormLayerPlan plan = FormLayerPlan.For(form, "Form");

        Assert.True(plan.Accepted, string.Join("; ", plan.Refusals.Select(r => r.Message)));
        PlannedLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(["renk", "renk_other"], layer.Fields.Select(f => f.Column));
        Assert.Equal(Graticula.Features.FieldType.Text, layer.Fields[1].Type);
        Assert.Equal(255, layer.Fields[1].Length);
        PlannedDomain domain = Assert.Single(plan.Domains);
        Assert.Equal("renk", domain.List);
        Assert.Equal(["Kırmızı", "Mavi", "Other"], domain.Codes.Select(c => c.Name));
    }

    [Fact]
    public void An_or_other_form_survives_the_workbook_and_the_model_unchanged_and_the_client_is_sent_the_automatic_choice()
    {
        FormModel model = Form([["select_one renk or_other", "renk", "Renk", "", "", "", ""]]);

        using MemoryStream written = new();
        model.WriteWorkbook(written);
        written.Position = 0;
        FormModel again = FormModel.ReadWorkbook(written);

        // Trailing empty cells are not kept by either spelling, so the model is compared with itself read back from JSON.
        Assert.True(FormModel.FromJson(model.ToJson()).SameAs(again));
        Assert.Equal("select_one renk or_other", again.Sheet("survey")!.Rows[0][0]);

        JsonObject client = FormCompiled.Json(Accepted(FormDefinition.Check(again)));
        JsonArray choices = client["choices"]!["renk"]!.AsArray();
        Assert.Equal("other", (string?)choices[2]!["name"]);
        Assert.True((bool?)choices[2]!["auto"]);
        Assert.Null(choices[0]!["auto"]);
        JsonArray body = client["root"]!["body"]!.AsArray();
        Assert.Equal("renk", (string?)body[0]!["list"]);
        Assert.Equal("renk_other", (string?)body[1]!["name"]);
    }

    // ------------------------------------------------------------------------------------- minimal and compact groups

    [Theory]
    [InlineData("begin group", "end group", "minimal")]
    [InlineData("begin group", "end group", "compact")]
    [InlineData("begin repeat", "end repeat", "minimal")]
    [InlineData("begin repeat", "end repeat", "compact")]
    [InlineData("begin repeat", "end repeat", "minimal compact")]
    public void A_group_or_repeat_may_be_minimal_or_compact_and_the_client_is_told(string begin, string end, string appearance)
    {
        FormCheck check = Checked([begin, "g", "Grup", appearance, "", "", ""], ["text", "t", "T", "", "", "", ""], [end, "", "", "", "", "", ""]);
        FormDefinition form = Accepted(check);

        Assert.DoesNotContain(check.Notices, n => n.Column == "appearance");
        JsonObject node = (JsonObject)FormCompiled.Json(form)["root"]!["body"]!.AsArray()[0]!;
        Assert.Equal(appearance.Split(' '), node["appearance"]!.AsArray().Select(a => (string)a!));
    }

    [Fact]
    public void Minimal_and_compact_off_their_own_types_are_drawn_the_default_way_with_a_notice_and_never_refused()
    {
        // The corpus's R9SignsSurvey puts minimal on a text, an integer, a date, an image and a hidden question.
        FormCheck check = Checked(
            ["text", "a", "A", "minimal", "", "", ""],
            ["integer", "b", "B", "minimal", "", "", ""],
            ["select_one renk", "c", "C", "compact", "", "", ""]);

        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => r.Message)));
        Assert.Equal(3, check.Notices.Count(n => n.Column == "appearance"));
        Assert.Contains(check.Notices, n => n.Row == 2 && n.Message.Contains("Survey123 ignores it", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------ autocomplete

    [Fact]
    public void Autocomplete_is_supported_on_select_one_and_refused_on_select_multiple_with_the_reason()
    {
        FormDefinition form = Accepted(Checked(["select_one renk", "a", "A", "autocomplete", "", "", ""]));
        Assert.Equal(["autocomplete"], form.Root.Questions[0].Appearance);

        RefusedAt(Checked(["select_multiple renk", "a", "A", "autocomplete", "", "", ""]), "survey", 2, "appearance", "Mobile app only");
        RefusedAt(Checked(["text", "a", "A", "autocomplete", "", "", ""]), "survey", 2, "appearance", "applies to select_one");
    }

    [Fact]
    public void Autocomplete_over_choices_with_pictures_is_refused_because_a_type_ahead_list_cannot_show_them()
    {
        FormCheck check = FormDefinition.Check(Form([["select_one renk", "renk", "Renk", "autocomplete", "", "", ""]],
            [["renk", "kirmizi", "Kırmızı", "k.png"]], choiceHeader: ["list_name", "name", "label", "media::image"]));

        RefusedAt(check, "survey", 2, "appearance", "type-ahead");
    }

    // ------------------------------------------------------------------------------------------- duplicate choices

    [Fact]
    public void A_duplicate_choice_in_a_select_one_list_is_accepted_with_a_notice_as_survey123_does_and_the_domain_keeps_the_first()
    {
        string[][] choices = [["renk", "kirmizi", "Kırmızı"], ["renk", "mavi", "Mavi"], ["renk", "kirmizi", "Koyu kırmızı"]];
        FormCheck check = FormDefinition.Check(Form([["select_one renk", "renk", "Renk", "", "", "", ""]], choices));
        FormDefinition form = Accepted(check);

        FormIssue notice = Assert.Single(check.Notices, n => n.Sheet == "choices");
        Assert.Equal((4, "name"), (notice.Row, notice.Column));
        Assert.Contains("first is on row 2", notice.Message, StringComparison.Ordinal);
        Assert.Equal(3, form.ChoiceLists["renk"].Count);

        PlannedDomain domain = Assert.Single(FormLayerPlan.For(form, "Form").Domains);
        Assert.Equal(["Kırmızı", "Mavi"], domain.Codes.Select(c => c.Name));
    }

    [Fact]
    public void A_duplicate_choice_is_refused_where_survey123_does_not_support_it_unless_the_settings_say_otherwise()
    {
        string[][] choices = [["renk", "kirmizi", "Kırmızı"], ["renk", "kirmizi", "Başka"]];

        RefusedAt(FormDefinition.Check(Form([["select_multiple renk", "renk", "Renk", "", "", "", ""]], choices)), "choices", 3, "name", "select_multiple");

        FormModel multilingual = new([
            new ModelSheet("survey", ["type", "name", "label::Türkçe (tr)", "label::English (en)"], [["select_one renk", "renk", "Renk", "Colour"]]),
            new ModelSheet("choices", ["list_name", "name", "label::Türkçe (tr)", "label::English (en)"], [["renk", "a", "A", "A"], ["renk", "a", "B", "B"]]),
        ]);
        RefusedAt(FormDefinition.Check(multilingual), "choices", 3, "name", "more than one language");

        Assert.True(FormDefinition.Check(Form([["select_multiple renk", "renk", "Renk", "", "", "", ""]], choices,
            [["allow_choice_duplicates"], ["yes"]])).Accepted);
    }

    // ------------------------------------------------------------------------------------------------- input masks

    [Fact]
    public void An_input_mask_on_a_text_question_is_kept_and_sent_to_the_client()
    {
        FormDefinition form = Accepted(Checked(["text", "tel", "Telefon", "", "", "", "(999) 999-9999"]));

        Assert.Equal("(999) 999-9999", form.Root.Questions[0].InputMask);
        Assert.Equal("(999) 999-9999", (string?)FormCompiled.Json(form)["root"]!["body"]![0]!["inputMask"]);
    }

    [Fact]
    public void An_input_mask_that_cannot_work_is_refused_at_its_row_and_column()
    {
        RefusedAt(Checked(["integer", "n", "N", "", "", "", "999"]), "survey", 2, "body::esri:inputMask", "text question");
        RefusedAt(Checked(["text", "t", "T", "", "", "", "---"]), "survey", 2, "body::esri:inputMask", "no mask character");
        RefusedAt(Checked(["text", "t", "T", "", "", "", "99\\"]), "survey", 2, "body::esri:inputMask", "backslash");
        RefusedAt(Checked(["text", "t", "T", "", "", "5", "999999"]), "survey", 2, "body::esri:inputMask", "holds 5");
    }

    [Theory]
    [InlineData("999-99-9999", ' ', 9, 9, 11)]
    [InlineData("AAA-AAA-AAA;_", '_', 9, 9, 11)]
    [InlineData("900 kg", ' ', 3, 1, 6)]
    [InlineData(">A<xxxxxxxxxxxx", ' ', 13, 1, 13)]
    [InlineData("\\>A", ' ', 1, 1, 2)]
    [InlineData("99\\;99", ' ', 4, 4, 5)]
    [InlineData("(999) 999-9999;#", '#', 10, 10, 14)]
    public void The_masks_in_survey123s_documentation_are_read(string mask, char placeholder, int slots, int required, int longest)
    {
        Assert.True(FormInputMask.TryParse(mask, out FormInputMask? read, out string? problem), problem);
        Assert.Equal((placeholder, slots, required, longest), (read!.Placeholder, read.Slots, read.RequiredSlots, read.MaxLength));
    }

    // ----------------------------------------------------------------------------------------- spinner, week-number

    [Fact]
    public void A_spinner_belongs_on_integer_and_decimal_and_a_week_number_on_date()
    {
        FormDefinition form = Accepted(Checked(
            ["integer", "a", "A", "spinner", "", "", ""],
            ["decimal", "b", "B", "spinner", "", "", ""],
            ["date", "c", "C", "week-number", "", "", ""]));

        Assert.Equal([["spinner"], ["spinner"], ["week-number"]], form.Root.Questions.Select(q => q.Appearance.ToArray()));
        RefusedAt(Checked(["text", "a", "A", "spinner", "", "", ""]), "survey", 2, "appearance", "applies to decimal, integer");
        RefusedAt(Checked(["dateTime", "a", "A", "week-number", "", "", ""]), "survey", 2, "appearance", "applies to date");
    }

    [Fact]
    public void Nothing_in_this_list_is_still_refused_by_name()
    {
        string[] gone = ["autocomplete", "spinner", "week-number"];
        Assert.DoesNotContain(gone, FormCatalog.RefusedAppearances.ContainsKey);
        Assert.DoesNotContain("body::esri:inputMask", FormCatalog.RefusedSurveyColumns.Keys);
        Assert.Contains("body::esri:inputMask", (IEnumerable<string>)FormCatalog.SurveyColumns);
    }
}
