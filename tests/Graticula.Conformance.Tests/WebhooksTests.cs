using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-266, the ArcGIS veteran's review of v1.0.499 (#15): an organisation's webhook and a feature service's are made,
/// fire on an item add and on a feature edit, post to their receiver with ArcGIS's signature, refuse a caller who is not
/// an administrator or the service's owner, and deactivate after the failures their policy allows.
/// </summary>
/// <remarks>
/// <para>
/// <b>The receiver is a listener inside the test, on 127.0.0.1</b> — and a webhook refuses a loopback address unless the
/// deployment allows its own network. So the server these run against is started with
/// <c>Graticula__WebhooksAllowPrivate=true</c>: CI's <i>start the server</i> step sets it, and a local fixture sets it
/// in its <c>start</c>. Without it every create here is refused with a sentence that names the setting.
/// </para>
/// <para>
/// <b>Filtered by what each test made</b>: other suites add items to the same server while these run, and an
/// organisation's webhook on <c>/items/add</c> hears them too.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class WebhooksTests : ArcGisClient
{
    private const string Stranger = "zz_webhook_stranger";
    private const string StrangerPassword = "zz-webhook-stranger-pw-1";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set; this test signs in as that user.");

    [Fact]
    public async Task An_organizations_webhook_posts_an_item_add_signed_with_its_secret()
    {
        using Receiver receiver = new(HttpStatusCode.OK);
        string secret = "zz-secret-" + Guid.NewGuid().ToString("N")[..8];
        string? hook = null, item = null;

        try
        {
            (JsonElement made, string raw) = await PortalAsync("/sharing/rest/portals/self/webhooks/createWebhook", new()
            {
                ["name"] = "zz item adds",
                ["url"] = receiver.Url,
                ["secret"] = secret,
                ["changes"] = "manualChanges",
                ["events"] = "/items/add",
                ["config"] = """{"deactivationPolicy":{"numberOfFailures":5,"daysInPast":5}}""",
            });
            Assert.True(made.TryGetProperty("webhook", out JsonElement webhook), raw);
            hook = webhook.GetProperty("id").GetString();

            // The secret is taken and never given back.
            Assert.Equal(string.Empty, webhook.GetProperty("secret").GetString());
            Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
            Assert.True(webhook.GetProperty("isActive").GetBoolean());

            (JsonElement listed, _) = await PortalAsync("/sharing/rest/portals/self/webhooks?f=json", null);
            Assert.Contains(listed.GetProperty("webhooks").EnumerateArray(), w => w.GetProperty("id").GetString() == hook);

            (JsonElement added, string addedRaw) = await PortalAsync($"/sharing/rest/content/users/{User}/addItem", new()
            {
                ["type"] = "Web Mapping Application", ["title"] = "zz webhook probe", ["text"] = "{}",
            });
            Assert.True(added.TryGetProperty("id", out JsonElement id), addedRaw);
            item = id.GetString();

            Delivery delivery = await receiver.WaitForAsync(
                d => d.Json.TryGetProperty("events", out JsonElement events)
                     && events.EnumerateArray().Any(e => e.GetProperty("id").GetString() == item),
                Patience);

            JsonElement info = delivery.Json.GetProperty("info");
            Assert.Equal(hook, info.GetProperty("webhookId").GetString());
            JsonElement raised = delivery.Json.GetProperty("events")[0];
            Assert.Equal("add", raised.GetProperty("operation").GetString());
            Assert.Equal("item", raised.GetProperty("source").GetString());
            Assert.Equal(User, raised.GetProperty("username").GetString());

            Assert.Equal(Signature(secret, delivery.Body), delivery.Header("x-esriHook-Signature"));
            Assert.Equal("application/json", delivery.Header("Content-Type")?.Split(';')[0]);

            (JsonElement status, _) = await PortalAsync($"/sharing/rest/portals/self/webhooks/{hook}/notificationStatus?f=json", null);
            Assert.Contains(status.GetProperty("notifications").EnumerateArray(), n => n.GetProperty("status").GetString() == "delivered");
        }
        finally
        {
            if (hook is not null)
            {
                await PortalAsync($"/sharing/rest/portals/self/webhooks/{hook}/delete", new());
            }

            if (item is not null)
            {
                await PortalAsync($"/sharing/rest/content/users/{User}/items/{item}/delete", new() { ["permanentDelete"] = "true" });
            }
        }
    }

    [Fact]
    public async Task A_feature_services_webhook_posts_an_edit_with_its_object_ids_and_signature()
    {
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(qualified), "GRATICULA_TEST_EDITABLE is not set, so there is no hosted layer to edit.");

        using Receiver receiver = new(HttpStatusCode.OK);
        string admin = $"/rest/admin/services/{qualified}/FeatureServer/webhooks";
        string key = "zz-key-" + Guid.NewGuid().ToString("N")[..8];
        int? hook = null;
        long? added = null;

        try
        {
            (JsonElement made, string raw) = await PortalAsync($"{admin}/create", new()
            {
                ["name"] = "zz features created",
                ["hookUrl"] = receiver.Url,
                ["changeTypes"] = """["FeaturesCreated"]""",
                ["signatureKey"] = key,
                ["active"] = "true",
            });
            Assert.True(made.TryGetProperty("id", out JsonElement number), raw);
            hook = number.GetInt32();
            Assert.Equal(string.Empty, made.GetProperty("signatureKey").GetString());
            Assert.DoesNotContain(key, raw, StringComparison.Ordinal);

            (int code, string body) = await AdminAsync(HttpMethod.Post,
                $"/rest/services/{qualified}/FeatureServer/0/addFeatures?f=json&features="
                + Uri.EscapeDataString("[{\"attributes\":{},\"geometry\":{\"x\":29.01,\"y\":41.01,\"spatialReference\":{\"wkid\":4326}}}]"));
            Assert.True(code == 200, $"addFeatures answered {code}: {body}");
            JsonElement result = JsonDocument.Parse(body).RootElement.GetProperty("addResults")[0];
            Assert.True(result.GetProperty("success").GetBoolean(), body);
            added = result.GetProperty("objectId").GetInt64();
            long oid = added.Value;

            Delivery delivery = await receiver.WaitForAsync(
                d => d.Json.ValueKind == JsonValueKind.Array
                     && d.Json[0].TryGetProperty("objectIds", out JsonElement ids)
                     && ids.GetProperty("adds").EnumerateArray().Any(a => a.GetInt64() == oid),
                Patience);

            JsonElement one = delivery.Json[0];
            Assert.Equal("zz features created", one.GetProperty("name").GetString());
            Assert.Equal(0, one.GetProperty("layerId").GetInt32());
            Assert.Contains("FeaturesCreated", one.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
            Assert.Contains($"objectIds={oid}", one.GetProperty("queryUrl").GetString(), StringComparison.Ordinal);
            Assert.Equal(Signature(key, delivery.Body), delivery.Header("x-esriHook-Signature"));

            // The delete is a change type this webhook did not ask for, and is not sent.
            await AdminAsync(HttpMethod.Post, $"/rest/services/{qualified}/FeatureServer/0/deleteFeatures?f=json&objectIds={oid}");
            added = null;
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.DoesNotContain(receiver.Received, d => d.Json.ValueKind == JsonValueKind.Array
                && d.Json[0].GetProperty("events").EnumerateArray().Any(e => e.GetString() == "FeaturesDeleted"));
        }
        finally
        {
            if (added is { } left)
            {
                await AdminAsync(HttpMethod.Post, $"/rest/services/{qualified}/FeatureServer/0/deleteFeatures?f=json&objectIds={left}");
            }

            if (hook is not null)
            {
                await PortalAsync($"{admin}/{hook}/delete", new());
            }
        }
    }

    [Fact]
    public async Task A_webhook_deactivates_after_the_failures_its_policy_allows()
    {
        using Receiver receiver = new(HttpStatusCode.InternalServerError);
        string? hook = null, item = null;

        try
        {
            (JsonElement made, string raw) = await PortalAsync("/sharing/rest/portals/self/webhooks/createWebhook", new()
            {
                ["name"] = "zz failing",
                ["url"] = receiver.Url,
                ["events"] = "/items/add",
                ["config"] = """{"deactivationPolicy":{"numberOfFailures":1,"daysInPast":1}}""",
            });
            Assert.True(made.TryGetProperty("webhook", out JsonElement webhook), raw);
            hook = webhook.GetProperty("id").GetString();

            (JsonElement added, string addedRaw) = await PortalAsync($"/sharing/rest/content/users/{User}/addItem", new()
            {
                ["type"] = "Web Mapping Application", ["title"] = "zz webhook failing probe", ["text"] = "{}",
            });
            Assert.True(added.TryGetProperty("id", out JsonElement id), addedRaw);
            item = id.GetString();

            DateTimeOffset until = DateTimeOffset.UtcNow + Patience;
            JsonElement now = default;

            while (DateTimeOffset.UtcNow < until)
            {
                (now, _) = await PortalAsync($"/sharing/rest/portals/self/webhooks/{hook}?f=json", null);

                if (!now.GetProperty("isActive").GetBoolean())
                {
                    break;
                }

                await Task.Delay(500);
            }

            Assert.False(now.GetProperty("isActive").GetBoolean(), $"The webhook was still active after {Patience}: {now}");
            Assert.Contains("failed deliveries", now.GetProperty("deactivatedReason").GetString(), StringComparison.Ordinal);
            Assert.NotEmpty(receiver.Received);

            (JsonElement status, _) = await PortalAsync($"/sharing/rest/portals/self/webhooks/{hook}/notificationStatus?f=json", null);
            Assert.Contains(status.GetProperty("notifications").EnumerateArray(), n => n.GetProperty("statusCode").ValueKind == JsonValueKind.Number
                && n.GetProperty("statusCode").GetInt32() == 500);

            // Activated again by hand, it fires again.
            (JsonElement activated, string activatedRaw) = await PortalAsync($"/sharing/rest/portals/self/webhooks/{hook}/activate", new());
            Assert.True(activated.GetProperty("success").GetBoolean(), activatedRaw);
            (JsonElement back, _) = await PortalAsync($"/sharing/rest/portals/self/webhooks/{hook}?f=json", null);
            Assert.True(back.GetProperty("isActive").GetBoolean());
            Assert.Equal(JsonValueKind.Null, back.GetProperty("deactivatedReason").ValueKind);
        }
        finally
        {
            if (hook is not null)
            {
                await PortalAsync($"/sharing/rest/portals/self/webhooks/{hook}/delete", new());
            }

            if (item is not null)
            {
                await PortalAsync($"/sharing/rest/content/users/{User}/items/{item}/delete", new() { ["permanentDelete"] = "true" });
            }
        }
    }

    [Fact]
    public async Task A_member_who_is_not_an_administrator_or_the_owner_is_refused()
    {
        string root = await RequireServerAsync();
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(qualified), "GRATICULA_TEST_EDITABLE is not set.");

        await AdminAsync(HttpMethod.Delete, $"/admin/members/{Stranger}?deleteOwned=true");
        (int created, string createdBody) = await AdminAsync(HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(new { name = Stranger, role = "publisher", userType = "creator" }));
        Assert.True(created is 200 or 201, $"Could not create the probe member: {created} {createdBody}");
        string issued = JsonDocument.Parse(createdBody).RootElement.GetProperty("password").GetString()!;

        try
        {
            string first = await SignInAsync(root, Stranger, issued);
            using (HttpRequestMessage change = new(HttpMethod.Post, $"{root}/rest/auth/password"))
            {
                change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first);
                change.Content = new StringContent(
                    JsonSerializer.Serialize(new { currentPassword = issued, newPassword = StrangerPassword }), Encoding.UTF8, "application/json");
                using HttpResponseMessage changed = await Http.SendAsync(change);
                Assert.True(changed.IsSuccessStatusCode, $"Changing the probe member's password answered {(int)changed.StatusCode}.");
            }

            string token = await SignInAsync(root, Stranger, StrangerPassword);

            // A publisher is not an administrator: the organisation's webhooks are not theirs to list or make.
            (JsonElement listed, string listedRaw) = await PortalAsync("/sharing/rest/portals/self/webhooks?f=json", null, token);
            Assert.True(listed.TryGetProperty("error", out JsonElement listError), listedRaw);
            Assert.Equal(403, listError.GetProperty("code").GetInt32());

            (JsonElement made, string raw) = await PortalAsync("/sharing/rest/portals/self/webhooks/createWebhook", new()
            {
                ["name"] = "zz not mine", ["url"] = "https://example.com/hook", ["changes"] = "allChanges",
            }, token);
            Assert.True(made.TryGetProperty("error", out JsonElement error), raw);
            Assert.Equal(403, error.GetProperty("code").GetInt32());

            // And a service they do not own is not theirs to watch.
            using HttpRequestMessage watch = new(HttpMethod.Post, $"{root}/rest/admin/services/{qualified}/FeatureServer/webhooks/create")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["name"] = "zz not mine", ["hookUrl"] = "https://example.com/hook", ["f"] = "json",
                }),
            };
            watch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage watched = await Http.SendAsync(watch);
            string watchedBody = await watched.Content.ReadAsStringAsync();
            Assert.True((int)watched.StatusCode is 401 or 403 or 404 || watchedBody.Contains("\"error\"", StringComparison.Ordinal),
                $"A stranger's webhook on '{qualified}' answered {(int)watched.StatusCode}: {watchedBody}");

            (JsonElement mine, _) = await PortalAsync($"/rest/admin/services/{qualified}/FeatureServer/webhooks?f=json", null);
            Assert.DoesNotContain(mine.EnumerateArray(), w => w.GetProperty("name").GetString() == "zz not mine");
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Stranger}?deleteOwned=true");
        }
    }

    private async Task<string> SignInAsync(string root, string name, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/rest/auth/login")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Signing in as '{name}' answered {(int)response.StatusCode}.");
        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    private async Task<(JsonElement Json, string Text)> PortalAsync(string path, Dictionary<string, string>? form, string? token = null)
    {
        string root = await RequireServerAsync();
        token ??= await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");

        using HttpRequestMessage request = new(form is null ? HttpMethod.Get : HttpMethod.Post, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (form is not null)
        {
            form["f"] = "json";
            request.Content = new FormUrlEncodedContent(form);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        JsonElement json = text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
        return (json, text);
    }

    private static string Signature(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>One request the receiver took.</summary>
    private sealed record Delivery(IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        public JsonElement Json { get; } = JsonDocument.Parse(Body).RootElement.Clone();

        public string? Header(string name) =>
            Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>
    /// The smallest HTTP/1.1 receiver that will do: one request per connection, a body by its Content-Length, and the
    /// status it was told to answer.
    /// </summary>
    private sealed class Receiver : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly HttpStatusCode _answer;
        private readonly ConcurrentQueue<Delivery> _received = new();

        public Receiver(HttpStatusCode answer)
        {
            _answer = answer;
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/hook";

        public IReadOnlyList<Delivery> Received => [.. _received];

        public async Task<Delivery> WaitForAsync(Func<Delivery, bool> wanted, TimeSpan patience)
        {
            DateTimeOffset until = DateTimeOffset.UtcNow + patience;

            while (DateTimeOffset.UtcNow < until)
            {
                if (_received.FirstOrDefault(d => Safe(wanted, d)) is { } found)
                {
                    return found;
                }

                await Task.Delay(200);
            }

            Assert.Fail($"No delivery arrived within {patience}; the receiver took {_received.Count}: "
                + string.Join(" | ", _received.Select(d => Encoding.UTF8.GetString(d.Body)[..Math.Min(300, d.Body.Length)])));
            return null!;
        }

        private static bool Safe(Func<Delivery, bool> wanted, Delivery d)
        {
            try
            {
                return wanted(d);
            }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
            {
                return false;
            }
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    List<byte> head = [];
                    byte[] one = new byte[1];

                    while (head.Count < 65536)
                    {
                        if (await stream.ReadAsync(one) == 0)
                        {
                            return;
                        }

                        head.Add(one[0]);

                        if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
                        {
                            break;
                        }
                    }

                    string[] lines = Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

                    foreach (string line in lines.Skip(1))
                    {
                        int colon = line.IndexOf(':');
                        if (colon > 0)
                        {
                            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                        }
                    }

                    int length = headers.TryGetValue("Content-Length", out string? said) ? int.Parse(said) : 0;
                    byte[] body = new byte[length];
                    int read = 0;

                    while (read < length)
                    {
                        int got = await stream.ReadAsync(body.AsMemory(read));
                        if (got == 0)
                        {
                            break;
                        }

                        read += got;
                    }

                    _received.Enqueue(new Delivery(headers, body));

                    byte[] answer = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {(int)_answer} {_answer}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(answer);
                }
                catch (Exception e) when (e is System.IO.IOException or SocketException or ObjectDisposedException)
                {
                    // The sender went away.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
