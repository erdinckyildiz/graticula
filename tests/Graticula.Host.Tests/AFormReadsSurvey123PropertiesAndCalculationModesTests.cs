using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.20: <c>pulldata("@property")</c>, <c>property()</c>, <c>pulldata("@geopoint")</c>,
/// <c>bind::esri:parameters</c>'s <c>calculationMode</c>, <c>date-time()</c> and the <c>draw</c> appearance are
/// supported with their full meaning, and what a browser cannot give is refused by name, never accepted and ignored.
/// </summary>
public sealed class AFormReadsSurvey123PropertiesAndCalculationModesTests
{
    private static readonly string[] Header = ["type", "name", "label", "relevant", "calculation", "appearance", "bind::esri:parameters"];

    private static FormCheck Checked(params string[][] survey) =>
        FormDefinition.Check(new FormModel([
            new ModelSheet("survey", Header, survey.Select(r => (IReadOnlyList<string>)r).ToList()),
            new ModelSheet("choices", ["list_name", "name", "label"], []),
        ]));

    private static FormCheck WithCalculation(string expression) =>
        Checked(["geopoint", "konum", "Konum"], ["calculate", "c", "", "", expression]);

    private static void RefusedAt(FormCheck check, int row, string column, string words)
    {
        Assert.False(check.Accepted);
        Assert.Contains(check.Refusals, r => r.Row == row && string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase)
            && r.Message.Contains(words, StringComparison.OrdinalIgnoreCase));
    }

    private static void Accepted(FormCheck check) => Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => r.Message)));

    [Theory]
    [InlineData("online")]
    [InlineData("utcOffset")]
    [InlineData("timeZone")]
    [InlineData("deviceId")]
    [InlineData("userAgent")]
    [InlineData("email")]
    [InlineData("username")]
    [InlineData("userFullName")]
    [InlineData("userFirstName")]
    [InlineData("userLastName")]
    [InlineData("portalUrl")]
    [InlineData("language")]
    [InlineData("surveyName")]
    [InlineData("mode")]
    [InlineData("status")]
    public void A_property_a_browser_can_give_is_accepted(string name) => Accepted(WithCalculation($"pulldata(\"@property\", '{name}')"));

    [Theory]
    [InlineData("deviceModel", "model of the device")]
    [InlineData("OS", "operating system")]
    [InlineData("appVersion", "build number")]
    [InlineData("portalInfo", "JSON object")]
    [InlineData("token", "credential")]
    [InlineData("submissionUrl", "@layer")]
    [InlineData("locale", "@javascript")]
    [InlineData("localeInfo", "@javascript")]
    public void A_property_a_browser_cannot_give_is_refused_by_name_with_its_reason(string name, string reason)
    {
        FormCheck check = WithCalculation($"pulldata('@property', '{name}')");

        RefusedAt(check, 3, "calculation", $"'{name}' is refused");
        RefusedAt(check, 3, "calculation", reason);
    }

    [Fact]
    public void An_undocumented_property_or_a_wrong_number_of_arguments_is_refused()
    {
        RefusedAt(WithCalculation("pulldata('@property', 'favouriteColour')"), 3, "calculation", "not a property Survey123 documents");
        RefusedAt(WithCalculation("pulldata('@property')"), 3, "calculation", "takes the name of one property");
        RefusedAt(WithCalculation("pulldata('@property', 'username', 'x')"), 3, "calculation", "takes the name of one property");
        // A name worked out at run time cannot be checked here; the client answers it or says it cannot.
        Accepted(WithCalculation("pulldata('@property', concat('user', 'name'))"));
    }

    [Theory]
    [InlineData("property('username')")]
    [InlineData("property('uri:username')")]
    [InlineData("property('email')")]
    [InlineData("property('uri:email')")]
    [InlineData("property('deviceid')")]
    [InlineData("property('uri:deviceid')")]
    [InlineData("property('userFullName')")]
    [InlineData("property('uri:online')")]
    public void ODKs_property_is_accepted_for_the_three_a_browser_knows(string expression) => Accepted(WithCalculation(expression));

    [Theory]
    [InlineData("property('subscriberid')", "subscriber id")]
    [InlineData("property('uri:simserial')", "serial number")]
    [InlineData("property('phonenumber')", "phone number")]
    [InlineData("property('shoe_size')", "not a property ODK or Survey123 documents")]
    [InlineData("property('token')", "credential")]
    [InlineData("property('deviceModel')", "model of the device")]
    [InlineData("property()", "takes the name of one property")]
    public void ODKs_property_is_refused_for_what_a_browser_cannot_read(string expression, string reason) =>
        RefusedAt(WithCalculation(expression), 3, "calculation", reason);

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("z")]
    [InlineData("horizontalAccuracy")]
    [InlineData("accuracy")]
    [InlineData("positionSourceType")]
    [InlineData("DMS")]
    [InlineData("DMS.latitudeMinutes")]
    [InlineData("DMS.text")]
    // Documented, and not recorded by a browser: accepted, and the client answers empty.
    [InlineData("verticalAccuracy")]
    [InlineData("speed")]
    [InlineData("verticalSpeed")]
    [InlineData("direction")]
    [InlineData("magneticVariation")]
    [InlineData("positionSourceInfo.deviceName")]
    public void A_geopoint_part_is_accepted(string part) => Accepted(WithCalculation($"pulldata(\"@geopoint\", ${{konum}}, \"{part}\")"));

    [Theory]
    [InlineData("UTM.easting")]
    [InlineData("MGRS")]
    public void A_geopoint_part_that_is_not_built_is_refused_by_name(string part) =>
        RefusedAt(WithCalculation($"pulldata('@geopoint', ${{konum}}, '{part}')"), 3, "calculation", $"'{part}' is refused");

    [Fact]
    public void An_undocumented_geopoint_part_or_the_wrong_arguments_are_refused()
    {
        RefusedAt(WithCalculation("pulldata('@geopoint', ${konum}, 'bogus')"), 3, "calculation", "not a part of a geopoint");
        RefusedAt(WithCalculation("pulldata('@geopoint', ${konum})"), 3, "calculation", "takes a geopoint question and the name of a part");
        RefusedAt(WithCalculation("pulldata('@geopoint', ${konum}, 'x', 0.01)"), 3, "calculation", "takes a geopoint question and the name of a part");
    }

    [Fact]
    public void The_other_pulldata_sources_stay_refused_with_their_reasons()
    {
        RefusedAt(WithCalculation("pulldata('@layer', 'a', 'b', 'c')"), 3, "calculation", "copy of that layer");
        RefusedAt(WithCalculation("pulldata('@json', 'a', 'b')"), 3, "calculation", "JSON");
        RefusedAt(WithCalculation("pulldata('@javascript', 'f.js', 'run')"), 3, "calculation", "script");
        RefusedAt(WithCalculation("pulldata('@exif', ${konum}, 'x')"), 3, "calculation", "metadata of a photo");
        RefusedAt(WithCalculation("pulldata('@mystery', 'a')"), 3, "calculation", "not a source Survey123 documents");
        Accepted(WithCalculation("pulldata('prices', 'price', 'code', 'A1')"));
    }

    [Fact]
    public void A_date_time_is_accepted()
    {
        Accepted(WithCalculation("date-time('2017-05-28T04:39:02+10:00')"));
        Accepted(WithCalculation("format-date-time(date-time(decimal-date-time(now()) + 1), '%Y')"));
    }

    [Theory]
    [InlineData("calculationMode=auto", "auto")]
    [InlineData("calculationMode=always", "always")]
    [InlineData("calculationMode=whenEmpty", "whenEmpty")]
    [InlineData("calculationMode=whenempty", "whenEmpty")]
    [InlineData(" calculationMode=manual ", "manual")]
    public void A_calculation_mode_is_accepted_and_reaches_the_client(string written, string mode)
    {
        FormCheck check = Checked(["text", "toplam", "Toplam", "", "1 + 1", "", written]);

        Accepted(check);
        JsonObject question = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(FormCompiled.Json(check.Definition!)["root"]!["body"])[0]);
        Assert.Equal(mode, (string?)question["calculationMode"]);
    }

    [Fact]
    public void A_question_with_no_calculation_mode_sends_none_which_is_auto()
    {
        FormCheck check = Checked(["text", "toplam", "Toplam", "", "1 + 1"]);

        JsonObject question = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(FormCompiled.Json(check.Definition!)["root"]!["body"])[0]);
        Assert.Null(question["calculationMode"]);
    }

    [Fact]
    public void Any_other_parameter_or_value_of_that_column_is_refused_by_name()
    {
        RefusedAt(Checked(["text", "t", "T", "", "1", "", "calculationMode=manual; saveIncomplete=true"]), 2, "bind::esri:parameters", "'saveIncomplete' is refused");
        RefusedAt(Checked(["text", "t", "T", "", "1", "", "calculationMode=sometimes"]), 2, "bind::esri:parameters", "'sometimes' is not a calculationMode");
        RefusedAt(Checked(["text", "t", "T", "", "1", "", "frequency=daily"]), 2, "bind::esri:parameters", "'frequency' is refused");
    }

    [Fact]
    public void A_calculation_mode_needs_a_calculation_and_a_type_that_allows_it()
    {
        RefusedAt(Checked(["text", "t", "T", "", "", "", "calculationMode=auto"]), 2, "bind::esri:parameters", "has none");
        RefusedAt(Checked(["range", "r", "R", "", "1", "", "calculationMode=auto"]), 2, "bind::esri:parameters", "not on 'range'");
        Accepted(Checked(["calculate", "c", "", "", "1", "", "calculationMode=always"]));
        Accepted(Checked(["calculate", "c", "", "", "1", "", "calculationMode=whenEmpty"]));
    }

    [Fact]
    public void Manual_needs_a_question_the_person_sees_because_the_button_is_on_the_question()
    {
        RefusedAt(Checked(["calculate", "c", "", "", "1", "", "calculationMode=manual"]), 2, "bind::esri:parameters", "Calculate button");
        RefusedAt(Checked(["hidden", "h", "", "", "1", "", "calculationMode=manual"]), 2, "bind::esri:parameters", "Calculate button");
        RefusedAt(Checked(["text", "t", "T", "", "1", "hidden", "calculationMode=manual"]), 2, "bind::esri:parameters", "Calculate button");
        Accepted(Checked(["text", "t", "T", "", "1", "", "calculationMode=manual"]));
    }

    [Fact]
    public void A_repeat_takes_allowAdds_and_allowUpdates_and_says_them_to_the_client()
    {
        // ADR-300 §5.7: Survey123's repeat parameters, read for the Inbox; absent, adds are allowed and updates are not.
        FormCheck check = Checked(
            ["begin repeat", "katlar", "Katlar", "", "", "", "allowAdds=false allowUpdates=true"], ["integer", "kat", "Kat"], ["end repeat"],
            ["begin repeat", "odalar", "Odalar"], ["text", "oda", "Oda"], ["end repeat"]);

        Accepted(check);
        JsonArray body = Assert.IsType<JsonArray>(FormCompiled.Json(check.Definition!)["root"]!["body"]);
        Assert.False((bool)body[0]!["parameters"]!["allowAdds"]!);
        Assert.True((bool)body[0]!["parameters"]!["allowUpdates"]!);
        Assert.True((bool)body[1]!["parameters"]!["allowAdds"]!);
        Assert.False((bool)body[1]!["parameters"]!["allowUpdates"]!);
    }

    [Fact]
    public void A_repeats_query_or_another_parameter_or_value_is_refused_by_name()
    {
        RefusedAt(Checked(["begin repeat", "r", "R", "", "", "", "query=kat > 1"], ["text", "t", "T"], ["end repeat"]), 2, "bind::esri:parameters", "query is refused");
        RefusedAt(Checked(["begin repeat", "r", "R", "", "", "", "allowDeletes=true"], ["text", "t", "T"], ["end repeat"]), 2, "bind::esri:parameters", "'allowDeletes' is refused");
        RefusedAt(Checked(["begin repeat", "r", "R", "", "", "", "allowAdds=sometimes"], ["text", "t", "T"], ["end repeat"]), 2, "bind::esri:parameters", "true or false");
    }

    [Fact]
    public void The_draw_appearance_is_supported_on_an_image_only()
    {
        FormCheck check = Checked(["image", "kroki", "Kroki", "", "", "draw"]);

        Accepted(check);
        JsonObject question = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(FormCompiled.Json(check.Definition!)["root"]!["body"])[0]);
        Assert.Equal("draw", (string?)Assert.IsType<JsonArray>(question["appearance"])[0]);
        RefusedAt(Checked(["text", "t", "T", "", "", "draw"]), 2, "appearance", "applies to image");
        RefusedAt(Checked(["audio", "a", "A", "", "", "draw"]), 2, "appearance", "applies to image");
    }

    [Fact]
    public void The_refusal_lists_agree_with_what_a_form_gets()
    {
        // Every refused name is refused, and no supported name is also refused.
        Assert.Empty(FormCatalog.Properties.Intersect(FormCatalog.RefusedProperties.Keys));
        Assert.Empty(FormCatalog.GeopointParts.Intersect(FormCatalog.GeopointPartsAnsweredEmpty));
        Assert.Empty(FormCatalog.GeopointParts.Concat(FormCatalog.GeopointPartsAnsweredEmpty).Intersect(FormCatalog.RefusedGeopointParts.Keys));
        Assert.DoesNotContain("property", FormCatalog.RefusedFunctions.Keys);
        Assert.Contains("property", (IEnumerable<string>)FormCatalog.Functions);
        Assert.Contains("date-time", (IEnumerable<string>)FormCatalog.Functions);
    }
}
