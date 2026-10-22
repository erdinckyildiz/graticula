using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-204 §5e item 14, owner decision 2026-10-22: a service's name is unique within its organisation, so two
/// organisations each have `hosted/&lt;name&gt;`, and each host answers its own at the same address.
/// </summary>
[Collection("server settings")]
public sealed class AServiceNameIsItsOrganisationsTests : ArcGisClient
{
    private string _root = string.Empty;

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, string? host = null, string? token = null, object? json = null, HttpContent? form = null)
    {
        using HttpRequestMessage request = new(method, $"{_root}{path}");

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = form ?? (json is null ? null : JsonContent.Create(json));

        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string> SettleAsync(string name, string issued, string host)
    {
        (_, JsonElement first) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, json: new { name, password = issued });
        string own = "Own-" + Guid.NewGuid().ToString("N");
        await SendAsync(HttpMethod.Post, "/rest/auth/password", host, first.GetProperty("token").GetString(),
            new { currentPassword = issued, newPassword = own });
        (_, JsonElement again) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, json: new { name, password = own });
        return again.GetProperty("token").GetString()!;
    }

    private async Task DefineAsync(string token, string? host, string name, string who, int points)
    {
        (HttpStatusCode made, JsonElement body) = await SendAsync(HttpMethod.Post, "/admin/hosted/define", host, token, new
        {
            name, geometryType = "point", fields = new[] { new { name = "who", type = "Text" } }, sharing = "public",
        });
        Assert.True(made == HttpStatusCode.Created, $"Defining '{name}' at {host ?? "the main host"} answered {(int)made}: {body}");

        string features = JsonSerializer.Serialize(Enumerable.Range(0, points).Select(i => new
        {
            geometry = new { x = 30 + i, y = 39, spatialReference = new { wkid = 4326 } },
            attributes = new { who },
        }));
        using FormUrlEncodedContent add = new([new("f", "json"), new("features", features)]);
        await SendAsync(HttpMethod.Post, $"/rest/services/hosted/{name}/FeatureServer/0/addFeatures", host, token, form: add);
    }

    [Fact]
    public async Task Two_organisations_each_have_a_service_of_the_same_name_and_each_host_answers_its_own()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"same{run}";
        string host = $"{key}.localhost";
        string name = $"zz_same_{run}";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key, name = $"Same {run}", hosts = new[] { host }, administrator = $"sameadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            // ADR-204 condition 9: a store whose role the floor cannot bind refuses a second organisation.
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}: {tenant}");

        try
        {
            string theirs = await SettleAsync($"sameadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host);

            await DefineAsync(admin, null, name, "main", 1);
            await DefineAsync(theirs, host, name, key, 2);

            foreach ((string? at, string who, int points) in new[] { ((string?)null, "main", 1), (host, key, 2) })
            {
                (HttpStatusCode status, JsonElement answer) = await SendAsync(HttpMethod.Get,
                    $"/rest/services/hosted/{name}/FeatureServer/0/query?where=1%3D1&outFields=who&f=json", at);
                Assert.True(status == HttpStatusCode.OK, $"{at ?? "The main host"} answered {(int)status}: {answer}");

                string[] whos = [.. answer.GetProperty("features").EnumerateArray()
                    .Select(f => f.GetProperty("attributes").GetProperty("who").GetString()!)];
                Assert.Equal(points, whos.Length);
                Assert.All(whos, w => Assert.Equal(who, w));
            }

            // The name is still unique within one organisation.
            (HttpStatusCode again, _) = await SendAsync(HttpMethod.Post, "/admin/hosted/define", host, theirs, new
            {
                name, geometryType = "point", fields = new[] { new { name = "who", type = "Text" } }, sharing = "private",
            });
            Assert.Equal(HttpStatusCode.Conflict, again);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true", token: admin);
        }
    }

    [Fact]
    public async Task Groups_domains_and_categories_are_named_within_an_organisation_and_the_datastore_name_is_the_servers()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"names{run}";
        string host = $"{key}.localhost";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key, name = $"Names {run}", hosts = new[] { host }, administrator = $"namesadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}: {tenant}");
        System.Collections.Generic.List<(string Path, string Id)> mainMade = [];

        try
        {
            string theirs = await SettleAsync($"namesadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host);
            object Domain() => new
            {
                domain = new
                {
                    type = "codedValue", name = $"zz_status_{run}", fieldType = "esriFieldTypeString",
                    codedValues = new[] { new { name = "A", code = "a" } },
                },
            };

            foreach ((string path, Func<object> body) in new (string, Func<object>)[]
                     {
                         ("/admin/groups", () => new { name = $"zz crews {run}" }),
                         ("/admin/domains", Domain),
                         ("/admin/item-categories", () => new { name = $"zz_roads_{run}" }),
                         ("/admin/member-categories", () => new { name = $"zz_surveyors_{run}" }),
                     })
            {
                (HttpStatusCode main, JsonElement mine) = await SendAsync(HttpMethod.Post, path, token: admin, json: body());
                Assert.True(main is HttpStatusCode.Created or HttpStatusCode.OK, $"{path} in the main organisation answered {(int)main}: {mine}");
                // A group is addressed by its name; the rest by id.
                mainMade.Add((path, path == "/admin/groups"
                    ? Uri.EscapeDataString(mine.GetProperty("name").GetString()!)
                    : mine.GetProperty("id").GetString()!));

                (HttpStatusCode other, JsonElement answer) = await SendAsync(HttpMethod.Post, path, host, theirs, body());
                Assert.True(other is HttpStatusCode.Created or HttpStatusCode.OK,
                    $"{path} of the same name in a second organisation answered {(int)other}: {answer}");

                (HttpStatusCode again, _) = await SendAsync(HttpMethod.Post, path, host, theirs, body());
                Assert.Equal(HttpStatusCode.Conflict, again);
            }

            // One datastore on the server: its name is no organisation's to give its own database.
            (HttpStatusCode reserved, JsonElement said) = await SendAsync(HttpMethod.Post, "/admin/datasources", host, theirs,
                new { name = "Datastore", connectionString = "Host=127.0.0.1;Port=1;Database=none" });
            Assert.True(reserved is HttpStatusCode.Conflict or HttpStatusCode.BadRequest, $"Naming a source 'Datastore' answered {(int)reserved}: {said}");
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);

            foreach ((string path, string id) in mainMade)
            {
                await SendAsync(HttpMethod.Delete, $"{path}/{id}", token: admin);
            }
        }
    }
}
