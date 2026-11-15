using System;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>ADR-288 §11a, D-405: the Quartz cron a map area's <c>packageRefreshSchedule</c> is written in.</summary>
public sealed class QuartzCronTests
{
    private static QuartzCron Parse(string text)
    {
        Assert.True(QuartzCron.TryParse(text, out QuartzCron cron, out string? why), why);
        return cron;
    }

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public void The_schedule_read_from_an_enterprise_portal_runs_on_tuesdays_at_the_time_it_names()
    {
        // 2026-11-15 is a Sunday; the next Tuesday is the 17th.
        QuartzCron cron = Parse("41 2 15 ? * TUE");

        Assert.Equal(At("2026-11-17T15:02:41Z"), cron.NextAfter(At("2026-11-15T10:00:00Z")));
        Assert.Equal(At("2026-11-24T15:02:41Z"), cron.NextAfter(At("2026-11-17T15:02:41Z")));
    }

    [Fact]
    public void A_daily_schedule_a_list_a_range_and_a_step_are_found_in_order()
    {
        Assert.Equal(At("2026-11-16T02:30:00Z"), Parse("0 30 2 * * ?").NextAfter(At("2026-11-15T02:30:00Z")));
        Assert.Equal(At("2026-11-15T10:20:00Z"), Parse("0 0,20,40 * * * ?").NextAfter(At("2026-11-15T10:10:00Z")));
        Assert.Equal(At("2026-11-16T09:00:00Z"), Parse("0 0 9-17 ? * MON-FRI").NextAfter(At("2026-11-15T20:00:00Z")));
        Assert.Equal(At("2026-11-15T10:15:00Z"), Parse("0 */15 * * * ?").NextAfter(At("2026-11-15T10:10:00Z")));
        Assert.Equal(At("2027-01-01T00:00:00Z"), Parse("0 0 0 1 JAN ?").NextAfter(At("2026-11-15T00:00:00Z")));
    }

    [Fact]
    public void A_day_of_month_that_does_not_exist_in_a_month_is_skipped()
    {
        Assert.Equal(At("2026-12-31T00:00:00Z"), Parse("0 0 0 31 * ?").NextAfter(At("2026-11-15T00:00:00Z")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0 0 * * *")]
    [InlineData("0 0 0 * * *")]
    [InlineData("0 0 0 ? * ?")]
    [InlineData("0 61 0 * * ?")]
    [InlineData("0 0 0 L * ?")]
    [InlineData("0 0 0 ? * 6#3")]
    [InlineData("0 0 0 15W * ?")]
    [InlineData("0 0 25 * * ?")]
    [InlineData("? 0 0 * * ?")]
    [InlineData("0 0 0 ? * FUNDAY")]
    public void An_expression_this_server_does_not_run_is_refused_with_its_reason(string text)
    {
        Assert.False(QuartzCron.TryParse(text, out _, out string? why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void The_weekly_parameters_count_monday_as_one_and_the_cron_sunday_as_one()
    {
        // Read 2026-11-15: "41 2 15 ? * TUE" with {type weekly, dayOfWeek 2, nthDay 1}.
        DateTimeOffset start = At("2024-05-07T15:02:41.043Z");
        Assert.Equal("41 2 15 ? * TUE", QuartzCron.FromWeekly(start, 2));
        Assert.Equal("41 2 15 ? * MON", QuartzCron.FromWeekly(start, 1));
        Assert.Equal("41 2 15 ? * SUN", QuartzCron.FromWeekly(start, 7));

        Assert.Equal(
            """{"startDate":"2024-05-07T15:02:41.000Z","type":"weekly","nthDay":1,"dayOfWeek":2}""",
            System.Text.Json.JsonSerializer.Serialize(Parse("41 2 15 ? * TUE").WeeklyParams(At("2024-05-07T15:02:41Z"))));
        Assert.Equal(7, ((dynamic)Parse("0 0 1 ? * SUN").WeeklyParams(start)!).dayOfWeek);

        // A shape the portal's client could not have drawn has no weekly parameters.
        Assert.Null(Parse("0 0 1 ? * MON,FRI").WeeklyParams(start));
        Assert.Null(Parse("0 0 1 * * ?").WeeklyParams(start));
    }
}
