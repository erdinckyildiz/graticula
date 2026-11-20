using System;
using System.Collections.Generic;
using System.Text.Json;
using Graticula.Features;
using Graticula.Formats;
using Xunit;

namespace Graticula.Core.Tests.Formats;

/// <summary>
/// A date field the import reader's header declares is created as a date when every value reads as one — found
/// 2026-11-20 when a file geodatabase date Pro published landed as text (ADR-083 condition 2's check).
/// </summary>
public sealed class DeclaredDatesTests
{
    [Theory]
    [InlineData("2026/11/20 08:48:03+00", "2026-11-20T08:48:03+00:00")]
    [InlineData("2026/11/20 08:48:03", "2026-11-20T08:48:03+00:00")]
    [InlineData("2026/11/20 08:48:03.250+00", "2026-11-20T08:48:03.25+00:00")]
    [InlineData("2026/11/20 11:48:03+03", "2026-11-20T08:48:03+00:00")]
    [InlineData("2026/11/20 03:18:03-0530", "2026-11-20T08:48:03+00:00")]
    [InlineData("2026/11/20 14:18:03+05:30", "2026-11-20T08:48:03+00:00")]
    [InlineData("2026/09/03", "2026-09-03T00:00:00+00:00")]
    public void OGR_s_text_reads_as_the_instant_it_names(string text, string instant)
    {
        Assert.True(OgrDateText.TryParse(text, out DateTimeOffset value));
        Assert.Equal(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-11-20")]
    [InlineData("2026/13/40")]
    [InlineData("2026/11/20 08:48:03+1")]
    [InlineData("day 0")]
    public void Anything_else_is_not_a_date(string? text)
    {
        Assert.False(OgrDateText.TryParse(text, out _));
    }

    private static InferredColumn Column(string name, params string?[] values)
    {
        InferredColumn column = new() { Name = name };

        foreach (string? value in values)
        {
            column.Observe(JsonSerializer.SerializeToElement(value));
        }

        return column;
    }

    private static JsonElement Header(string name, string type) =>
        JsonSerializer.SerializeToElement(new { fields = new[] { new { name, type } } });

    [Fact]
    public void A_declared_date_whose_values_all_read_is_a_date()
    {
        InferredColumn seen = Column("seen", "2026/11/20 08:48:03+00", null, "2026/11/19 08:48:03+00");

        DeclaredDates.DeclareDates(Header("seen", "DateTime"), new Dictionary<string, InferredColumn> { ["seen"] = seen });

        Assert.Equal(FieldType.Date, seen.Type);
    }

    [Fact]
    public void An_undeclared_column_of_date_text_stays_text()
    {
        // A GeoJSON or CSV column holding the same text was never declared and is not reinterpreted.
        InferredColumn text = Column("seen", "2026/11/20 08:48:03+00");

        Assert.Equal(FieldType.Text, text.Type);
    }

    [Fact]
    public void A_declared_date_with_one_value_that_does_not_read_stays_text()
    {
        InferredColumn mixed = Column("seen", "2026/11/20 08:48:03+00", "soon");

        DeclaredDates.DeclareDates(Header("seen", "Date"), new Dictionary<string, InferredColumn> { ["seen"] = mixed });

        Assert.Equal(FieldType.Text, mixed.Type);
    }

    [Fact]
    public void A_field_declared_as_something_else_is_left_alone()
    {
        InferredColumn label = Column("label", "2026/11/20");

        DeclaredDates.DeclareDates(Header("label", "String"), new Dictionary<string, InferredColumn> { ["label"] = label });

        Assert.Equal(FieldType.Text, label.Type);
    }
}
