using System;
using Graticula.Platform.Jobs;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// An output published without a name the caller gave keeps its run's first digits whatever its parameter is called —
/// a long parameter name used to cut them off at 40 characters, and every run after the first collided with the first
/// (security review, 2026-10-30).
/// </summary>
public sealed class AToolsOutputNameKeepsItsRunTests
{
    [Fact]
    public void A_long_parameter_name_is_shortened_and_the_run_kept()
    {
        JobRecord first = Job(Guid.Parse("11111111-0000-0000-0000-000000000000"));
        JobRecord second = Job(Guid.Parse("22222222-0000-0000-0000-000000000000"));
        string parameter = "a_parameter_name_that_is_far_longer_than_forty_characters";

        string one = ToolRunner.OutputName(parameter, first);
        string two = ToolRunner.OutputName(parameter, second);

        Assert.Equal("a_parameter_name_that_is_far_lo_11111111", one);
        Assert.True(one.Length <= 40);
        Assert.NotEqual(one, two);
    }

    [Fact]
    public void A_short_parameter_name_is_kept_whole()
    {
        Assert.Equal("out_raster_11111111", ToolRunner.OutputName("Out_Raster", Job(Guid.Parse("11111111-0000-0000-0000-000000000000"))));
    }

    private static JobRecord Job(Guid id) =>
        new(id, JobKind.Tool, JobStatus.Running, 0, Guid.NewGuid(), null, null, null, DateTimeOffset.UnixEpoch, null, null);
}
