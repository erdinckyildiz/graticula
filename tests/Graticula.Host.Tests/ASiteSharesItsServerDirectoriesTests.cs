using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>Two nodes of one site that mount one path as their server directories — <see cref="TwoNodeSite"/>.</summary>
public sealed class TwoSharingNodes : IAsyncLifetime
{
    /// <summary>The site.</summary>
    public TwoNodeSite Site { get; } = new();

    /// <summary>The path both mount.</summary>
    public string Shared { get; } = Path.Combine(Path.GetTempPath(), "graticula-shared-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>The first node.</summary>
    public TwoNodeSite.Node A { get; private set; } = null!;

    /// <summary>The second.</summary>
    public TwoNodeSite.Node B { get; private set; } = null!;

    /// <summary>What every node of this site is given.</summary>
    public Dictionary<string, string> Settings => new()
    {
        ["Graticula__Nodes"] = "2",
        ["Graticula__ServerDirectories"] = Shared,
    };

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Shared);
        await Site.InitializeAsync();
        A = await Site.StartAsync("share-a", Settings);
        B = await Site.StartAsync("share-b", Settings);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await Site.DisposeAsync();

        try
        {
            Directory.Delete(Shared, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that outlives a run costs nothing worth failing over.
        }
    }
}

/// <summary>
/// ADR-012 §5 phase 1 — server directories under one shared path, checked from every node — against two processes
/// (condition 3).
/// </summary>
/// <remarks>
/// <b>Each fails on the build before phase 1</b>, where uploads, exports and the tile cache are under each node's own state
/// directory and nothing checks that the nodes see one another's files: condition 2's prototype observed the download
/// of an image uploaded on one node refused by the other.
/// </remarks>
[Trait("Needs", "TwoNodes")]
public sealed class ASiteSharesItsServerDirectoriesTests(TwoSharingNodes nodes) : IClassFixture<TwoSharingNodes>
{
    private static string Corpus()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && at.GetFiles("*.sln").Length == 0)
        {
            at = at.Parent;
        }

        Assert.False(at is null, "This test could not find the repository root from its own path.");
        return Path.Combine(at!.FullName, "tests", "Graticula.Raster.Tiff.Tests", "corpus", "gray-byte-deflate.tif");
    }

    /// <summary>D-282's shape: an image uploaded through one node is handed out by the other, from the one shared copy.</summary>
    [Fact]
    public async Task An_image_uploaded_through_one_node_is_downloaded_from_the_other()
    {
        string token = await nodes.A.SignInAsync();
        string name = "zz_shared_" + Guid.NewGuid().ToString("N")[..6];

        using MultipartFormDataContent form = new();
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent("hosted"), "folder");
        ByteArrayContent file = new(await File.ReadAllBytesAsync(Corpus()));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "shared.tif");

        using HttpRequestMessage upload = new(HttpMethod.Post, "/admin/coverages/upload") { Content = form };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage uploaded = await nodes.A.Http.SendAsync(upload);
        Assert.True(uploaded.IsSuccessStatusCode, $"upload answered {(int)uploaded.StatusCode}: {await uploaded.Content.ReadAsStringAsync()}");

        Assert.True(Directory.EnumerateFiles(Path.Combine(nodes.Shared, "imagery"), "*", SearchOption.AllDirectories).Any(),
            "The upload is not under the shared server directories.");

        (int fromB, string said) = await nodes.B.SendAsync(HttpMethod.Get, $"/admin/coverages/{name}/file?folder=hosted", await nodes.B.SignInAsync());
        Assert.True(fromB == 200, $"node share-b answered {fromB} for the file uploaded through share-a: {said}");
    }

    /// <summary>Phase 1's check from every node: each sees the other's check file, and the Machines page says so.</summary>
    [Fact]
    public async Task Each_machine_sees_the_others_check_file()
    {
        foreach (TwoNodeSite.Node on in (TwoNodeSite.Node[])[nodes.A, nodes.B])
        {
            (int status, string body) = await on.SendAsync(HttpMethod.Get, "/admin/site/machines", await on.SignInAsync());
            Assert.Equal(200, status);
            JsonElement directories = JsonDocument.Parse(body).RootElement.GetProperty("directories");
            Assert.True(directories.GetProperty("shared").GetBoolean());
            Assert.Empty(directories.GetProperty("unseen").EnumerateArray());
            Assert.Equal(2, directories.GetProperty("seen").GetArrayLength());
        }
    }

    /// <summary>A node that keeps its own directories is refused by a site that shares them, and told what to set.</summary>
    [Fact]
    public async Task A_node_keeping_its_own_directories_is_refused()
    {
        (int? exit, string said) = await nodes.Site.StartRefusedAsync("share-own", new Dictionary<string, string> { ["Graticula__Nodes"] = "2" });

        Assert.True(exit is not null and not 0, $"a node with its own directories joined:\n{said}");
        Assert.Contains("shares its server directories", said, StringComparison.Ordinal);
    }

    /// <summary>A node that mounts some other path is refused: it would write uploads the others cannot read.</summary>
    [Fact]
    public async Task A_node_mounting_another_path_is_refused()
    {
        string elsewhere = Path.Combine(Path.GetTempPath(), "graticula-elsewhere-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(elsewhere);

        (int? exit, string said) = await nodes.Site.StartRefusedAsync("share-elsewhere", new Dictionary<string, string>
        {
            ["Graticula__Nodes"] = "2",
            ["Graticula__ServerDirectories"] = elsewhere,
        });

        Assert.True(exit is not null and not 0, $"a node mounting another path joined:\n{said}");
        Assert.Contains("are not the ones node", said, StringComparison.Ordinal);
    }

    private static async Task<(int Status, string Body)> FormAsync(
        TwoNodeSite.Node on, string token, string path, Dictionary<string, string> fields, (string Name, byte[] Bytes)? file = null)
    {
        using MultipartFormDataContent form = new();

        foreach ((string key, string value) in fields)
        {
            form.Add(new StringContent(value), key);
        }

        if (file is { } attached)
        {
            ByteArrayContent bytes = new(attached.Bytes);
            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(bytes, "file", attached.Name);
        }

        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage answer = await on.Http.SendAsync(request);
        return ((int)answer.StatusCode, await answer.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Phase 2, Pro's staging: a service definition added through one node takes its part through the other. Before it,
    /// condition 2's prototype saw <c>addPart</c> on the second node answer 400 <i>Item does not exist</i>.
    /// </summary>
    [Fact]
    public async Task A_service_definition_added_on_one_node_takes_its_part_on_the_other()
    {
        string ta = await nodes.A.SignInAsync();
        string tb = await nodes.B.SignInAsync();
        string user = TwoNodeSite.Administrator;

        (int added, string item) = await FormAsync(nodes.A, ta, $"/sharing/rest/content/users/{user}/addItem", new()
        {
            ["type"] = "Service Definition", ["title"] = "split", ["multipart"] = "true", ["filename"] = "split.sd", ["f"] = "json",
        });
        Assert.True(added == 200, $"addItem answered {added}: {item}");
        string id = JsonDocument.Parse(item).RootElement.GetProperty("id").GetString()!;

        (int part, string said) = await FormAsync(nodes.B, tb, $"/sharing/rest/content/users/{user}/items/{id}/addPart",
            new() { ["partNum"] = "1", ["f"] = "json" }, ("split.sd", new byte[64]));
        Assert.True(part == 200 && said.Contains("\"success\":true", StringComparison.Ordinal),
            $"addPart on the other node answered {part}: {said}");
    }

    /// <summary>Phase 2, OGC Processes: a job started on one node is polled to its end, and its results read, on the other.</summary>
    [Fact]
    public async Task A_processes_job_started_on_one_node_is_followed_on_the_other()
    {
        string ta = await nodes.A.SignInAsync();
        string tb = await nodes.B.SignInAsync();

        using HttpRequestMessage execute = new(HttpMethod.Post, "/ogc/processes/v1/processes/echo/execution")
        {
            Content = new StringContent("""{"inputs":{"stringInput":"later"},"response":"document"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        execute.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ta);
        execute.Headers.Add("Prefer", "respond-async");
        using HttpResponseMessage started = await nodes.A.Http.SendAsync(execute);
        string body = await started.Content.ReadAsStringAsync();
        Assert.True((int)started.StatusCode == 201, $"execution answered {(int)started.StatusCode}: {body}");
        string job = JsonDocument.Parse(body).RootElement.GetProperty("jobID").GetString()!;

        await TwoNodeSite.Node.UntilAsync(
            async () =>
            {
                (int status, string info) = await nodes.B.SendAsync(HttpMethod.Get, $"/ogc/processes/v1/jobs/{job}", tb);
                return status == 200 ? JsonDocument.Parse(info).RootElement.GetProperty("status").GetString() : $"{status} {info}";
            },
            state => state == "successful", TimeSpan.FromSeconds(10), "the other node seeing the job finish");

        (int results, string answer) = await nodes.B.SendAsync(HttpMethod.Get, $"/ogc/processes/v1/jobs/{job}/results", tb);
        Assert.True(results == 200, $"results on the other node answered {results}: {answer}");
    }

    /// <summary>Phase 2, the export pictures: the address one node answered an export with is fetched from the other.</summary>
    [Fact]
    public async Task An_exported_picture_is_fetched_from_the_other_node()
    {
        string ta = await nodes.A.SignInAsync();
        string name = "zz_pic_" + Guid.NewGuid().ToString("N")[..6];

        (int made, string said) = await nodes.A.SendAsync(HttpMethod.Post, "/admin/hosted/define", ta, new
        {
            name,
            geometryType = "Point",
            fields = new[] { new { name = "label", type = "Text" } },
            sharing = "public",
        });
        Assert.True(made is 200 or 201, $"defining {name} answered {made}: {said}");

        (int exported, string json) = await nodes.A.SendAsync(HttpMethod.Get,
            $"/rest/services/hosted/{name}/MapServer/export?bbox=-180,-85,180,85&bboxSR=4326&size=64,64&format=png&f=json", ta);
        Assert.True(exported == 200, $"export answered {exported}: {json}");
        string href = JsonDocument.Parse(json).RootElement.GetProperty("href").GetString()!;

        (int fetched, string picture) = await nodes.B.SendAsync(HttpMethod.Get, new Uri(href).PathAndQuery, null);
        Assert.True(fetched == 200, $"the picture's address on the other node answered {fetched}: {picture}");
    }
}
