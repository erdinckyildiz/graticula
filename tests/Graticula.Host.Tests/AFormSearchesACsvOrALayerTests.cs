using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.23: a select's <c>search()</c> appearance, over a CSV uploaded with the form or a layer of this server, and
/// <c>pulldata("@layer")</c> are accepted as xlsform.org and Survey123 document them, sent to the client read, and
/// refused at their row and column when they are malformed or name another server's layer.
/// </summary>
public sealed class AFormSearchesACsvOrALayerTests
{
    private static readonly string[] SurveyHeader = ["type", "name", "label", "appearance", "calculation"];

    private const string Layer = "https://forms.example/rest/services/hosted/Cabinets/FeatureServer/2";

    private static FormModel Form(string[][] survey, string[][]? choices = null) =>
        new([
            new ModelSheet("survey", SurveyHeader, survey.Select(r => (IReadOnlyList<string>)r).ToList()),
            new ModelSheet("choices", ["list_name", "name", "label"], (choices ?? [["places", "place_id", "place_name"], ["kinds", "a", "A"]]).Select(r => (IReadOnlyList<string>)r).ToList()),
        ]);

    private static FormCheck Checked(params string[][] survey) => FormDefinition.Check(Form(survey));

    private static FormCheck Searching(string appearance, string type = "select_one places") =>
        Checked(["text", "ulke", "Ülke"], [type, "yer", "Yer", appearance]);

    private static FormCheck Calculating(string expression) =>
        Checked(["geopoint", "konum", "Konum"], ["text", "ulke", "Ülke"], ["text", "c", "C", "", expression]);

    private static FormDefinition Accepted(FormCheck check)
    {
        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => $"{r.Sheet} {r.Row} {r.Column}: {r.Message}")));
        return check.Definition!;
    }

    private static void RefusedAt(FormCheck check, int row, string column, string words)
    {
        Assert.False(check.Accepted);
        Assert.Contains(check.Refusals, r => r.Sheet == "survey" && r.Row == row && string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase)
            && r.Message.Contains(words, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------------------------------------- search(): CSV

    [Theory]
    [InlineData("search('places')", null, null, null, null)]
    [InlineData("search('places.csv')", null, null, null, null)]
    [InlineData("quick search('places', 'matches', 'country_id', ${ulke})", "matches", "country_id", "${ulke}", null)]
    [InlineData("search('places', 'contains', 'place_name', ${ulke}, 'district_id', 'K1')", "contains", "place_name", "${ulke}", "district_id")]
    [InlineData("search('places', 'startswith', 'place_name,alt_name', ${ulke})", "startswith", "place_name,alt_name", "${ulke}", null)]
    [InlineData("minimal search(\"places\", \"endswith\", \"place_name\", 'x')", "endswith", "place_name", "'x'", null)]
    public void The_spellings_xlsform_org_documents_for_a_CSV_are_accepted_and_sent_to_the_client_read(
        string appearance, string? type, string? column, string? text, string? filterColumn)
    {
        FormDefinition form = Accepted(Searching(appearance));
        FormQuestion q = form.Root.Questions[1];

        Assert.NotNull(q.Search);
        Assert.Equal("places.csv", q.Search!.File);
        Assert.Null(q.Search.Url);
        Assert.Equal(type, q.Search.SearchType);
        Assert.Equal(column, q.Search.SearchColumn);
        Assert.Equal(text, q.Search.SearchText);
        Assert.Equal(filterColumn, q.Search.FilterColumn);

        // The search is the question's, not an appearance: what is left is drawn as before.
        Assert.DoesNotContain(q.Appearance, a => a.Contains("search", StringComparison.OrdinalIgnoreCase));
        JsonObject client = (JsonObject)FormCompiled.Json(form)["root"]!["body"]![1]!;
        Assert.Equal("places.csv", (string?)client["search"]!["file"]);
        Assert.Equal(type, (string?)client["search"]!["type"]);
        Assert.Empty(form.QueriedLayers);
    }

    [Fact]
    public void The_corpus_spellings_are_accepted_on_select_one_and_select_multiple()
    {
        // stats4sd forms, as written: a trailing space, two spaces between arguments, and a select_multiple with a filter.
        Accepted(Searching("quick search('Farmer_CSV',  'matches',  'Location', ${ulke}) "));
        Accepted(Searching("search('AE_practices', 'matches', 'district_id', ${ulke})", "select_multiple places"));
        Accepted(Checked(["select_multiple places", "a", "A", "search('eg')"], ["select_multiple places", "b", "B", "search('eg')"]));
    }

    [Fact]
    public void A_search_list_makes_no_domain_because_its_row_names_columns_not_answers()
    {
        FormDefinition form = Accepted(Searching("search('places')"));
        FormLayerPlan plan = FormLayerPlan.For(form, "Form");

        Assert.True(plan.Accepted);
        Assert.Empty(plan.Domains);
    }

    [Theory]
    [InlineData("search('places', 'matches')", "takes 1, 4 or 6 arguments")]
    [InlineData("search('places', 'matches', 'a', ${ulke}, 'b')", "takes 1, 4 or 6 arguments")]
    [InlineData("search(places)", "first argument is the CSV's name or the layer's")]
    [InlineData("search('places', 'like', 'a', ${ulke})", "'like' is not a kind of search")]
    [InlineData("search('places', 'intersects', 'a', ${konum})", "spatial search, and a CSV has no geometry")]
    [InlineData("search('places', matches, 'a', ${ulke})", "second argument is the kind of search")]
    [InlineData("search('places', 'matches', a, ${ulke})", "third argument is the column")]
    [InlineData("search('media/places')", "not the name of a CSV")]
    [InlineData("search('places'", "never closed")]
    [InlineData("search('places') search('x')", "one search()")]
    [InlineData("search('places', 'matches', 'a', ${yok})", "${yok} names no question")]
    public void A_malformed_search_is_refused_at_its_row_with_the_reason(string appearance, string words) =>
        RefusedAt(Searching(appearance), 3, "appearance", words);

    [Theory]
    [InlineData("text")]
    [InlineData("integer")]
    public void Search_on_a_question_without_choices_is_refused(string type) =>
        RefusedAt(Checked([type, "x", "X", "search('places')"]), 2, "appearance", "has none");

    [Fact]
    public void Search_on_a_select_from_file_is_refused_because_the_file_already_gives_the_choices() =>
        RefusedAt(Checked(["select_one_from_file places.csv", "x", "X", "search('places')"]), 2, "appearance", "already takes its choices from its file");

    [Fact]
    public void A_search_list_with_several_rows_reads_the_first_and_says_so()
    {
        FormCheck check = FormDefinition.Check(Form([["select_one places", "x", "X", "search('places')"]],
            [["places", "place_id", "place_name"], ["places", "other_id", "other_name"]]));

        Accepted(check);
        Assert.Contains(check.Notices, n => n.Row == 2 && n.Column == "appearance" && n.Message.Contains("first row", StringComparison.Ordinal));
    }

    [Fact]
    public void Or_other_on_a_search_is_refused_because_its_choices_come_from_the_file() =>
        RefusedAt(Checked(["select_one places or_other", "x", "X", "search('places')"]), 2, "type", "take or_other off");

    // ----------------------------------------------------------------------------------------------- search(): layer

    [Fact]
    public void Survey123s_layer_spelling_is_accepted_with_its_parameters_and_named_for_the_publish_check()
    {
        FormDefinition form = Accepted(Searching(
            $"autocomplete search(\"location?url={Layer}&where=STATE_NAME NOT IN('Alabama', 'Alaska')&resultRecordCount=10\")"));
        FormSearch search = form.Root.Questions[1].Search!;

        Assert.Equal(Layer, search.Url);
        Assert.Null(search.File);
        Assert.Equal("STATE_NAME NOT IN('Alabama', 'Alaska')", search.Query["where"]);
        Assert.Equal("10", search.Query["resultRecordCount"]);
        Assert.Equal(["autocomplete"], form.Root.Questions[1].Appearance);

        QueriedLayer queried = Assert.Single(form.QueriedLayers);
        Assert.Equal((3, "appearance", "Cabinets", 2), (queried.Row, queried.Column, queried.Address.Service, queried.Address.Layer));

        JsonObject client = (JsonObject)FormCompiled.Json(form)["root"]!["body"]![1]!;
        Assert.Equal(Layer, (string?)client["search"]!["url"]);
        Assert.Equal("10", (string?)client["search"]!["query"]!["resultRecordCount"]);
    }

    [Fact]
    public void A_percent_encoded_parameter_is_read_decoded_as_Survey123_asks_for_its_web_app()
    {
        FormSearch search = Accepted(Searching($"search(\"p?url={Layer}&where=NAME%20LIKE%20'A%25'\")")).Root.Questions[1].Search!;

        Assert.Equal("NAME LIKE 'A%'", search.Query["where"]);
    }

    [Theory]
    [InlineData("search(\"p?url=https://forms.example/rest/services/hosted/Cabinets/FeatureServer/2\", 'intersects', '', ${konum})")]
    [InlineData("search(\"p?url=/rest/services/hosted/Cabinets/FeatureServer/0\", 'within', 'shape', ${konum})")]
    [InlineData("search(\"p?url=/rest/services/hosted/Cabinets/FeatureServer/0\", 'index_intercepts', 'shape', ${konum})")]
    public void A_spatial_search_over_a_layer_is_accepted(string appearance) =>
        Accepted(Checked(["geopoint", "konum", "Konum"], ["select_one places", "yer", "Yer", appearance]));

    [Theory]
    [InlineData("search(\"?url=" + Layer + "\")", "names no table")]
    [InlineData("search(\"p?where=1=1\")", "names it with url=")]
    [InlineData("search(\"p?url=https://forms.example/rest/services/hosted/Cabinets/FeatureServer\")", "not one of its layers")]
    [InlineData("search(\"p?url=https://forms.example/sharing/rest/content/items/abc\")", "not the address of a feature layer")]
    [InlineData("search(\"p?url=" + Layer + "&token=abc\")", "credential is never written into a form")]
    [InlineData("search(\"p?url=" + Layer + "&outSR=3857\")", "'outSR' is refused")]
    [InlineData("search(\"p?url=" + Layer + "&foo=1\")", "not a query parameter a form may add")]
    [InlineData("search(\"p?url=" + Layer + "&oops\")", "is not a parameter")]
    public void A_malformed_layer_search_is_refused_at_its_row(string appearance, string words) =>
        RefusedAt(Searching(appearance), 3, "appearance", words);

    // ----------------------------------------------------------------------------------------- pulldata("@layer")

    [Theory]
    [InlineData("pulldata(\"@layer\", \"getRecordAt\", \"" + Layer + "\", ${konum})")]
    [InlineData("pulldata(\"@layer\", \"getValueAt\", \"attributes.ZONE\", \"" + Layer + "\", ${konum})")]
    [InlineData("pulldata(\"@layer\", \"getRecordAt\", \"" + Layer + "\", ${konum}, \"COUNTRY='Canada'\")")]
    [InlineData("pulldata(\"@layer\", \"getValue\", \"attributes.NAME\", \"" + Layer + "?orderByFields=POPULATION DESC&resultOffset=9\", \"STATE_NAME = 'California'\")")]
    [InlineData("pulldata(\"@layer\", \"getValue\", \"attributes.TotalCount\", concat(\"" + Layer + "\", '?outStatistics=[{\"statisticType\": \"count\",\"onStatisticField\": \"objectId\",\"outStatisticFieldName\": \"TotalCount\"}]'), concat(\"STATE_NAME = '\", ${ulke}, \"'\"))")]
    [InlineData("pulldata(\"@layer\", \"getRecord\", \"" + Layer + "\")")]
    [InlineData("pulldata(\"@layer\", \"getValue\", \"geometry\", '/rest/services/hosted/Cabinets/FeatureServer/0', concat(\"Location = '\",${ulke},\"'\"))")]
    public void Survey123s_documented_calls_are_accepted(string expression)
    {
        FormDefinition form = Accepted(Calculating(expression));

        QueriedLayer queried = Assert.Single(form.QueriedLayers);
        Assert.Equal((4, "calculation"), (queried.Row, queried.Column));
    }

    [Fact]
    public void An_address_worked_out_at_run_time_is_accepted_and_left_to_the_client()
    {
        FormDefinition form = Accepted(Calculating("pulldata(\"@layer\", \"getValue\", \"attributes.NAME\", ${ulke})"));

        Assert.Empty(form.QueriedLayers);
    }

    [Theory]
    [InlineData("pulldata(\"@layer\")", "names its operation")]
    [InlineData("pulldata(\"@layer\", \"getFeature\", \"" + Layer + "\")", "'getFeature' is not an operation")]
    [InlineData("pulldata(\"@layer\", \"getValue\", \"" + Layer + "\")", "takes 2 to 3 arguments after the operation, not 1")]
    [InlineData("pulldata(\"@layer\", \"getRecordAt\", \"" + Layer + "\")", "takes 2 to 3 arguments")]
    [InlineData("pulldata(\"@layer\", \"getValue\", \"NAME\", \"" + Layer + "\")", "'NAME' is not a property of a feature")]
    [InlineData("pulldata(\"@layer\", \"getValueAt\", \"attributes.ZONE\", \"" + Layer + "\", ${ulke})", "is a 'text', not a geopoint")]
    [InlineData("pulldata(\"@layer\", \"getRecordAt\", \"" + Layer + "\", '41 29')", "asks for a geopoint question there")]
    [InlineData("pulldata(\"@layer\", \"getRecord\", \"" + Layer + "?outFields=*\")", "'outFields' is refused")]
    [InlineData("pulldata(\"@layer\", \"getRecord\", \"" + Layer + "?resultRecordCount=5\")", "gives the first record")]
    [InlineData("pulldata(\"@layer\", \"getRecord\", \"" + Layer + "?token=x\")", "credential")]
    [InlineData("pulldata(\"@layer\", \"getRecord\", \"https://forms.example/rest/services/hosted/Cabinets/FeatureServer\")", "not one of its layers")]
    public void A_malformed_call_is_refused_at_its_row_with_the_reason(string expression, string words) =>
        RefusedAt(Calculating(expression), 4, "calculation", words);

    // ---------------------------------------------------------------------------------------------- this server only

    [Fact]
    public void A_layer_of_another_server_is_refused_in_words_and_this_servers_is_not()
    {
        // The Apopka forms' own cell, as written in the corpus.
        const string Apopka = "https://services.arcgis.com/syn8rfJ2eTAK0T6k/arcgis/rest/services/Apopka_Cabinet_And_Signal_Inspections/FeatureServer/2";
        FormDefinition form = Accepted(Searching($"autocomplete search(\"location?url={Apopka}\")"));
        QueriedLayer queried = Assert.Single(form.QueriedLayers);

        string? foreign = FormLayerQuery.Foreign(queried.Address, queried.Url, "forms.example", "https");
        Assert.NotNull(foreign);
        Assert.Contains("services.arcgis.com", foreign, StringComparison.Ordinal);
        Assert.Contains("this server answers at forms.example", foreign, StringComparison.Ordinal);

        QueriedLayer own = Assert.Single(Accepted(Searching($"search(\"p?url={Layer}\")")).QueriedLayers);
        Assert.Null(FormLayerQuery.Foreign(own.Address, own.Url, "forms.example", "https"));
        Assert.NotNull(FormLayerQuery.Foreign(own.Address, own.Url, "forms.example:8443", "https"));

        QueriedLayer relative = Assert.Single(Accepted(Searching("search(\"p?url=/rest/services/hosted/Cabinets/FeatureServer/0\")")).QueriedLayers);
        Assert.Null(FormLayerQuery.Foreign(relative.Address, relative.Url, "anything.example", "https"));
    }

    [Fact]
    public void The_layer_source_is_no_longer_refused_and_the_submission_url_property_says_why_it_still_is()
    {
        Assert.False(FormCatalog.RefusedPulldataSources.ContainsKey("@layer"));
        Assert.Contains("§5.23", FormCatalog.RefusedProperties["submissionUrl"], StringComparison.Ordinal);
    }
}
