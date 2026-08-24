using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// `/admin/services/{name}` addresses one service, and the folder is what says which.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-39](../../docs/architecture-debt.md): the same address meant two different things.</b>
/// <c>…/sharing</c>, <c>…/start</c>, <c>…/stop</c> and <c>…/limits</c> reached a *system* service;
/// <c>…/groups</c> and <c>…/style</c> reached a *published* one. A published service named
/// <c>Geometry</c> would have made a sharing change land on the geometry server. Latent, because
/// no published service has that name — which is what a latent collision is, until somebody
/// publishes a layer and names it after a utility.
/// </para>
/// <para>
/// <b>The repair is not the route rename the row prescribed.</b> A rename moves eighteen console
/// call sites and thirteen test ones to fix a collision whose cause is smaller: the lookup ignored
/// a parameter it already had. Every one of these routes carries <c>?folder=</c>, because the
/// published services they share a path with need it, and a system service lives in a folder —
/// <c>Utilities/Geometry</c>. Comparing them makes the two addresses distinct.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class OneAddressOneServiceTests : ArcGisClient
{
    /// <summary>
    /// The system service answers on its own folder.
    /// </summary>
    /// <remarks>
    /// <b>First, because the rest of this file is about refusals</b> and a refusal test that
    /// passes because nothing works at all asserts nothing.
    /// </remarks>
    [Fact]
    public async Task A_system_service_answers_when_its_own_folder_is_named()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        using HttpRequestMessage ask = new(
            HttpMethod.Get, $"{root}/admin/services/Geometry/limits?folder=Utilities");

        ask.Headers.Add("Authorization", $"Bearer {token}");

        using HttpResponseMessage answered = await Http.SendAsync(ask);

        Assert.True(
            answered.IsSuccessStatusCode,
            $"The geometry server's own address answered {(int)answered.StatusCode}. Either the "
            + "system service moved, or the folder comparison is wrong in the other direction.");
    }

    /// <summary>
    /// The same name in a different folder does not reach the system service.
    /// </summary>
    /// <remarks>
    /// <b>This is the whole of D-39.</b> Before the repair, the lookup matched on the name alone,
    /// so <c>hosted/Geometry</c> and <c>Utilities/Geometry</c> were the same address to every one
    /// of these routes. The published service does not need to exist for the check to be
    /// meaningful: what is asserted is that the system service is not what answers.
    /// </remarks>
    [Theory]
    [InlineData("hosted")]
    [InlineData("")]
    public async Task The_same_name_in_another_folder_does_not_reach_the_system_service(string folder)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        using HttpRequestMessage ask = new(
            HttpMethod.Get,
            $"{root}/admin/services/Geometry/limits?folder={Uri.EscapeDataString(folder)}");

        ask.Headers.Add("Authorization", $"Bearer {token}");

        using HttpResponseMessage answered = await Http.SendAsync(ask);

        string body = await answered.Content.ReadAsStringAsync();

        Assert.True(
            body.Contains("\"error\"", StringComparison.Ordinal),
            $"`/admin/services/Geometry/limits?folder={folder}` answered the *system* geometry "
            + "server's bounds. That is D-39: one address, two services, and a caller who meant "
            + $"the published one in '{folder}' has just read — or written — the system one.");

        Assert.Equal(
            404,
            JsonDocument.Parse(body).RootElement
                .GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>
    /// Stopping is held to the same rule, and it is the one that changes something.
    /// </summary>
    /// <remarks>
    /// <b>Checked before the write rather than after.</b> <c>SetStatusAsync</c> matches on the
    /// name alone; without the folder asked for first, a caller who meant a published service
    /// would have stopped the geometry server and been told it worked.
    /// </remarks>
    [Fact]
    public async Task Stopping_the_wrong_folders_service_does_not_stop_the_system_one()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        using HttpRequestMessage stop = new(
            HttpMethod.Post, $"{root}/admin/services/Geometry/stop?folder=hosted");

        stop.Headers.Add("Authorization", $"Bearer {token}");

        using HttpResponseMessage answered = await Http.SendAsync(stop);

        Assert.Contains(
            "\"error\"",
            await answered.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // And it is still running, which is the assertion the refusal is for.
        using HttpRequestMessage ask = new(
            HttpMethod.Get, $"{root}/admin/services/Geometry/limits?folder=Utilities");

        ask.Headers.Add("Authorization", $"Bearer {token}");

        using HttpResponseMessage state = await Http.SendAsync(ask);

        Assert.True(
            state.IsSuccessStatusCode,
            "The geometry server stopped answering after a request that named another folder.");
    }
}
