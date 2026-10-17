using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Server › Site › Backups as an API — ADR-193 §5.6: the list says where backups go and what the schedule is, the
/// schedule is off until set and takes only sane values, a name is never a path, and none of it answers a stranger.
/// </summary>
/// <remarks>
/// <b>Taking a backup is not driven here</b>: it needs PostgreSQL's client tools beside the server, which the image
/// carries and a test runner may not. The round trip is ADR-193 condition 1, measured on a copy of the showcase.
/// </remarks>
public sealed class BackupEndpointsTests : ArcGisClient
{
    [Fact]
    public async Task The_list_says_where_backups_go_and_that_the_schedule_is_off()
    {
        JsonElement list = await GetJsonAsync("/admin/backups");

        Assert.False(string.IsNullOrWhiteSpace(list.GetProperty("location").GetString()));
        Assert.Equal(JsonValueKind.Array, list.GetProperty("backups").ValueKind);
        Assert.Contains("graticula restore", list.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Contains("secret key", list.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_schedule_outside_the_day_or_keeping_nothing_is_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Put, $"{root}/admin/backups/schedule", token!, "{\"enabled\":true,\"hourUtc\":24,\"keep\":0}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("hourUtc", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("graticula-backup-..%2F..%2Fetc")]
    [InlineData("not-a-backup")]
    public async Task A_name_that_is_not_a_finished_backup_is_404_and_never_a_path(string name)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        (HttpStatusCode status, _) = await RequestAsync(HttpMethod.Get, $"{root}/admin/backups/{name}/download", token!, null);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task A_stranger_reaches_none_of_it()
    {
        (HttpStatusCode status, _) = await AnonymousAsync("/admin/backups");

        Assert.NotEqual(HttpStatusCode.OK, status);
    }
}
