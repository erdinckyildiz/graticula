using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A public group is found by a signed-out caller through the portal's group search, and an organization group is not
/// — ADR-199, reversing Q-119 by owner decision of 2026-10-18.
/// </summary>
public sealed class APublicGroupIsFoundByAnybodyTests : ArcGisClient
{
    private const string Public = "zz_public_group";
    private const string Organization = "zz_org_group";

    private static string Settings(string visibility) => JsonSerializer.Serialize(new
    {
        title = (string?)null,
        summary = (string?)null,
        description = (string?)null,
        visibility,
        joinPolicy = "invitation",
        contribute = "managers",
        deleteLocked = false,
    });

    [Fact]
    public async Task A_signed_out_caller_finds_the_public_group_and_not_the_organizations()
    {
        await RequireServerAsync();

        try
        {
            foreach ((string name, string visibility) in new[] { (Public, "public"), (Organization, "organization") })
            {
                (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/groups", JsonSerializer.Serialize(new { name }));
                Assert.True(made is 200 or 201, $"making {name}: {made} {madeBody}");
                (int set, string setBody) = await AdminAsync(HttpMethod.Put, $"/admin/groups/{name}/settings", Settings(visibility));
                Assert.True(set == 200, $"setting {name} to {visibility}: {set} {setBody}");
            }

            (HttpStatusCode status, string body) = await AnonymousAsync("/sharing/rest/community/groups");
            Assert.Equal(HttpStatusCode.OK, status);

            JsonElement[] results = [.. JsonDocument.Parse(body).RootElement.GetProperty("results").EnumerateArray()];
            JsonElement found = Assert.Single(results, g => g.GetProperty("title").GetString() == Public);
            Assert.Equal("public", found.GetProperty("access").GetString());
            Assert.DoesNotContain(results, g => g.GetProperty("title").GetString() == Organization);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Public}");
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Organization}");
        }
    }
}
