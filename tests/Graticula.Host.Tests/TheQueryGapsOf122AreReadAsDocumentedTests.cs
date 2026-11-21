using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The pure halves of ADR-309: the attachments filter, which <c>gdbVersion</c> is the default, and what a
/// <c>queryBins</c> request is read as before any database is asked.
/// </summary>
/// <remarks>
/// Written 2026-11-21. The conformance suite makes each of these requests against a real layer; these are the cases that
/// need no database and are cheapest to keep exact.
/// </remarks>
public sealed class TheQueryGapsOf122AreReadAsDocumentedTests
{
    private static AttachmentInfo Attachment(int id, string name, string type, long size, string keywords = "") =>
        new(id, 1, name, type, null, size, DateTimeOffset.UnixEpoch, null, keywords);

    private static bool Matches(string expression, AttachmentInfo attachment)
    {
        Assert.True(AttachmentFilter.TryParse(expression, out AttachmentFilter? filter, out string? error), error);
        return filter!.Matches(attachment);
    }

    [Theory]
    [InlineData("ATT_NAME = 'photo.png'", true)]
    [InlineData("att_name = 'PHOTO.PNG'", true)]
    [InlineData("name like 'ph%'", true)]
    [InlineData("ATT_NAME NOT LIKE 'ph%'", false)]
    [InlineData("DATA_SIZE > 100 AND DATA_SIZE <= 200", true)]
    [InlineData("size BETWEEN 1 AND 99", false)]
    [InlineData("CONTENT_TYPE IN ('image/png', 'image/jpeg')", true)]
    [InlineData("keywords like '%roof%'", true)]
    [InlineData("keywords IS NULL", false)]
    [InlineData("NOT (size < 100) AND (ATT_NAME = 'x' OR ATTACHMENTID = 7)", true)]
    [InlineData("ATT_NAME = 'it''s'", false)]
    public void An_attachments_expression_is_evaluated_over_the_properties_an_attachment_has(string expression, bool expected)
    {
        Assert.Equal(expected, Matches(expression, Attachment(7, "photo.png", "image/png", 150, "roof;wall")));
    }

    [Fact]
    public void An_attachment_with_no_keywords_is_null_for_them()
    {
        Assert.True(Matches("KEYWORDS IS NULL", Attachment(1, "a", "image/png", 1)));
        Assert.False(Matches("KEYWORDS = 'x'", Attachment(1, "a", "image/png", 1)));
    }

    [Theory]
    [InlineData("nosuchfield = 1")]
    [InlineData("ATT_NAME = ")]
    [InlineData("ATT_NAME = 'a'; drop table t")]
    [InlineData("ATT_NAME = 'unclosed")]
    [InlineData("(ATT_NAME = 'a'")]
    [InlineData("ATT_NAME LIKE 5")]
    [InlineData("size IN 1, 2")]
    [InlineData("")]
    public void An_expression_that_is_not_in_the_grammar_is_refused_with_the_fields_named(string expression)
    {
        Assert.False(AttachmentFilter.TryParse(expression, out _, out string? error));
        Assert.Contains("ATT_NAME", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("sde.DEFAULT", true)]
    [InlineData("SDE.Default", true)]
    [InlineData("DEFAULT", true)]
    [InlineData("Edits", false)]
    [InlineData("jane.Working", false)]
    [InlineData("sde.DEFAULT2", false)]
    public void Only_the_default_version_is_a_version_this_server_has(string? version, bool isDefault)
    {
        Assert.Equal(isDefault, FeatureServerQueryParameters.GdbVersionRefusal(version) is null);
    }

    [Fact]
    public void A_query_names_the_version_it_refuses_and_an_edit_says_nothing_was_applied()
    {
        string? query = FeatureServerQueryParameters.GdbVersionRefusal("Edits");
        Assert.Contains("gdbVersion=Edits", query, StringComparison.Ordinal);

        Dictionary<string, StringValues> values = new() { ["gdbVersion"] = "Edits" };
        string? edit = Program.EditParameterRefusal(new FormCollection(values), new DefaultHttpContext());
        Assert.Contains("Nothing was applied", edit, StringComparison.Ordinal);

        values["gdbVersion"] = "sde.DEFAULT";
        Assert.Null(Program.EditParameterRefusal(new FormCollection(values), new DefaultHttpContext()));
    }

    // ---------- queryBins ----------

    private static readonly FieldDescription[] Fields =
    [
        new("objectid", FieldType.BigInteger, false, null),
        new("amount", FieldType.Integer, true, null),
        new("when", FieldType.Date, true, null),
        new("label", FieldType.Text, true, 20),
    ];

    private static (bool Read, BinRequest? Request, IQueryCollection? ForQuery, string? Error) Read(params (string Key, string Value)[] parameters)
    {
        Dictionary<string, StringValues> values = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string value) in parameters)
        {
            values[key] = value;
        }

        bool read = QueryBins.TryRead(new QueryCollection(values), Fields, "objectid", out BinRequest? request, out IQueryCollection? forQuery, out string? error);
        return (read, request, forQuery, error);
    }

    [Fact]
    public void A_bin_request_is_read_and_the_default_statistic_is_a_frequency_count_of_the_identity()
    {
        (bool read, BinRequest? request, IQueryCollection? forQuery, string? error) = Read(
            ("bin", """{"type":"fixedIntervalBin","onField":"AMOUNT","parameters":{"interval":50,"start":0,"end":200}}"""),
            ("where", "1=1"),
            ("binOrder", "desc"));

        Assert.True(read, error);
        Assert.Equal(BinKind.FixedInterval, request!.Kind);
        Assert.Equal("amount", request.Column);
        Assert.True(request.Descending);
        Assert.Equal(50, request.Interval);
        Assert.Equal("1=1", forQuery!["where"].ToString());
        Assert.Contains("\"frequency\"", forQuery["outStatistics"].ToString(), StringComparison.Ordinal);
        Assert.Contains("\"objectid\"", forQuery["outStatistics"].ToString(), StringComparison.Ordinal);
        Assert.False(forQuery.ContainsKey("bin"), "the layer query is not given the bin");
    }

    [Fact]
    public void A_date_bin_reads_its_unit_offset_and_week_start()
    {
        (bool read, BinRequest? request, _, string? error) = Read(
            ("bin", """{"type":"dateBin","onField":"when","parameters":{"unit":"week","number":2,"offset":{"number":-8,"unit":"hour"},"snapToData":"first","returnFullIntervalBin":true},"firstDayOfWeek":1}"""));

        Assert.True(read, error);
        Assert.Equal("week", request!.Unit);
        Assert.Equal(2, request.Number);
        Assert.Equal(-8 * 3_600_000L, request.OffsetMilliseconds);
        Assert.Equal(1, request.FirstDayOfWeek);
        Assert.True(request.SnapToFirst);
        Assert.True(request.FullIntervalBin);
    }

    [Theory]
    [InlineData("""{"type":"lodBin","onField":"amount","parameters":{}}""", "lodBin")]
    [InlineData("""{"type":"classificationBin","onField":"amount","parameters":{}}""", "classificationBin")]
    [InlineData("""{"type":"fixedIntervalBin","onField":"amount","parameters":{"interval":5},"splitBy":"label"}""", "splitBy")]
    [InlineData("""{"type":"fixedIntervalBin","onField":"amount","parameters":{"interval":5},"stackBy":["label"]}""", "stackBy")]
    [InlineData("""{"type":"fixedIntervalBin","onField":"amount","parameters":{"interval":5},"transformation":"squareRoot"}""", "transformation")]
    [InlineData("""{"type":"dateBin","onField":"when","parameters":{"unit":"day","snapToData":"last"}}""", "last")]
    [InlineData("""{"type":"dateBin","onField":"amount","parameters":{"unit":"day"}}""", "date field")]
    [InlineData("""{"type":"fixedIntervalBin","onField":"when","parameters":{"interval":5}}""", "numeric field")]
    [InlineData("""{"type":"fixedIntervalBin","onField":"label","parameters":{"interval":5}}""", "numeric field")]
    [InlineData("""{"type":"fixedIntervalBin","onField":"nosuch","parameters":{"interval":5}}""", "nosuch")]
    [InlineData("""{"type":"fixedIntervalBin","onField":"amount","parameters":{"interval":-1}}""", "interval")]
    [InlineData("""{"type":"fixedIntervalBin","onField":"amount","parameters":{"interval":1,"unit":"day"}}""", "unit")]
    [InlineData("""{"type":"fixedBoundariesBin","onField":"amount","parameters":{"boundaries":[1]}}""", "boundaries")]
    [InlineData("""{"type":"fixedBoundariesBin","onField":"amount","parameters":{"boundaries":[3,2,1]}}""", "ascending")]
    [InlineData("""{"type":"autoIntervalBin","onField":"amount","parameters":{"numberOfBins":2.5}}""", "numberOfBins")]
    [InlineData("""{"type":"autoIntervalBin","onField":"amount","parameters":{"numberOfBins":3,"start":9,"end":1}}""", "'end'")]
    [InlineData("""{"type":"dateBin","onField":"when","parameters":{"unit":"fortnight"}}""", "unit")]
    [InlineData("""{"type":"dateBin","onField":"when","parameters":{"unit":"day","offset":5}}""", "offset")]
    [InlineData("""{"type":"dateBin","onField":"when","parameters":{"unit":"day","offset":{"number":1,"unit":"year"}}}""", "offset")]
    [InlineData("not json", "JSON")]
    [InlineData("[]", "JSON object")]
    public void What_queryBins_does_not_do_is_refused_with_the_reason(string bin, string reason)
    {
        (bool read, _, _, string? error) = Read(("bin", bin));

        Assert.False(read);
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("async", "true")]
    [InlineData("returnCentroid", "true")]
    [InlineData("quantizationParameters", "{}")]
    [InlineData("orderByFields", "amount")]
    [InlineData("groupByFieldsForStatistics", "label")]
    [InlineData("havingClause", "1=1")]
    [InlineData("resultRecordCount", "5")]
    [InlineData("f", "pbf")]
    [InlineData("binOrder", "sideways")]
    [InlineData("lowerBoundaryAlias", "no good")]
    public void A_parameter_queryBins_does_not_read_is_refused_not_ignored(string key, string value)
    {
        (bool read, _, _, string? error) = Read(
            ("bin", """{"type":"fixedIntervalBin","onField":"amount","parameters":{"interval":5}}"""),
            (key, value));

        Assert.False(read, key);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_bin_is_required_and_an_asynchronous_false_is_not_a_request_for_async()
    {
        Assert.False(Read().Read);
        Assert.True(Read(
            ("bin", """{"type":"fixedIntervalBin","onField":"amount","parameters":{"interval":5}}"""),
            ("async", "false"),
            ("f", "json")).Read);
    }

    [Fact]
    public void The_outStatistics_the_caller_sends_replace_the_default()
    {
        const string Statistics = """[{"statisticType":"max","onStatisticField":"amount","outStatisticFieldName":"top"}]""";

        (bool read, _, IQueryCollection? forQuery, string? error) = Read(
            ("bin", """{"type":"fixedIntervalBin","onField":"amount","parameters":{"interval":5}}"""),
            ("outStatistics", Statistics));

        Assert.True(read, error);
        Assert.Equal(Statistics, forQuery!["outStatistics"].ToString());
        Assert.DoesNotContain("bin", forQuery.Keys, StringComparer.OrdinalIgnoreCase);
    }
}
