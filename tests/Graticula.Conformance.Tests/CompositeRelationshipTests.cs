using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A guarantee this server does not honour is refused rather than stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-26](../../docs/architecture-debt.md).</b> ADR-013 §3 says a composite relationship
/// cascades on delete. It is not implemented. Accepting the flag would put it in the layer
/// document, where an administrator reads it and concludes that deleting a parcel removes its
/// owners — and the orphaned rows that follow are silent: they accumulate, count against
/// nothing, and surface as rows nobody can explain.
/// </para>
/// <para>
/// <b>The refusal is in the endpoint and was not tested.</b> A refusal nothing exercises is one
/// somebody removes while tidying, and the way it would be found is the way the row describes —
/// by orphans, months later. 501 rather than 400, because the request is not wrong: it asks for
/// something this server has not built.
/// </para>
/// </remarks>
public sealed class CompositeRelationshipTests : ArcGisClient
{
    /// <summary>Declaring a composite relationship is refused, and the refusal says why.</summary>
    /// <remarks>
    /// <b>The layers are named as they would be, and are not reached.</b> The flag is checked
    /// before either layer is looked up, so this needs no relationship and leaves nothing behind
    /// whichever way it goes.
    /// </remarks>
    [Fact]
    public async Task Declaring_a_composite_relationship_is_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "this test needs an administrator's token");

        (HttpStatusCode status, string body) = await AdminAsync(
            root, token!, "/admin/relationships",
            JsonSerializer.Serialize(new
            {
                name = "zz_d26_composite",
                originLayer = "zz_d26_origin",
                relatedLayer = "zz_d26_related",
                originKey = "id",
                relatedKey = "origin_id",
                cardinality = "OneToMany",
                composite = true,
            }));

        Assert.True(
            status == HttpStatusCode.NotImplemented,
            $"declaring a composite relationship answered {(int)status}: {body}. Storing the flag "
            + "would report a cascade this server does not perform.");

        // The refusal has to say what to do instead, because the caller's intent is legitimate.
        Assert.Contains("not implemented", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON DELETE CASCADE", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without the flag, the request gets as far as looking for its layers.
    /// </summary>
    /// <remarks>
    /// <b>The control.</b> A server that refused every relationship declaration would pass the
    /// test above and mean nothing. This one names layers that do not exist, so it fails — and
    /// what is asserted is that it fails for the missing layer rather than for the flag.
    /// </remarks>
    [Fact]
    public async Task Without_the_flag_the_refusal_is_about_something_else()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "this test needs an administrator's token");

        (HttpStatusCode status, string body) = await AdminAsync(
            root, token!, "/admin/relationships",
            JsonSerializer.Serialize(new
            {
                name = "zz_d26_plain",
                originLayer = "zz_d26_origin",
                relatedLayer = "zz_d26_related",
                originKey = "id",
                relatedKey = "origin_id",
                cardinality = "OneToMany",
            }));

        Assert.True(
            status != HttpStatusCode.NotImplemented,
            $"a relationship without the composite flag was refused as unimplemented: {body}");

        Assert.DoesNotContain("'composite'", body, StringComparison.Ordinal);
    }

    private async Task<(HttpStatusCode Status, string Body)> AdminAsync(
        string root, string token, string path, string body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
