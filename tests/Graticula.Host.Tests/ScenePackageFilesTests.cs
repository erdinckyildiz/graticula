using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-274: a scene layer package above the document limit is a file named by its hash in the server directories, a
/// recorded name never leads outside the folder, and an upload that died is swept.
/// </summary>
public sealed class ScenePackageFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "graticula-spf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_package_is_kept_under_its_hash_and_the_same_bytes_share_one_file()
    {
        ScenePackageFiles files = new(_root);
        byte[] bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        (string recorded, string hash, long size) = await files.KeepAsync(new MemoryStream(bytes), default);

        Assert.Equal($"scene-package-files/{sha}.slpk", recorded);
        Assert.Equal(sha, hash);
        Assert.Equal(bytes.Length, size);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(files.PathOf(recorded)!));

        (string again, _, _) = await files.KeepAsync(new MemoryStream(bytes), default);
        Assert.Equal(recorded, again);
        Assert.Single(Directory.GetFiles(files.Directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("scene-package-files/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("scene-package-files/abc.slpk")]
    [InlineData("imagery/0000000000000000000000000000000000000000000000000000000000000000.slpk")]
    [InlineData("scene-package-files/0000000000000000000000000000000000000000000000000000000000000000.slpk/../x")]
    public void A_recorded_name_that_is_not_a_hash_in_the_folder_leads_nowhere(string? recorded)
    {
        Assert.Null(new ScenePackageFiles(_root).PathOf(recorded));
    }

    [Fact]
    public void An_upload_that_died_is_swept_and_a_kept_package_is_not()
    {
        ScenePackageFiles files = new(_root);
        Directory.CreateDirectory(files.Directory);
        string old = Path.Combine(files.Directory, "dead.part");
        string fresh = Path.Combine(files.Directory, "running.part");
        string kept = Path.Combine(files.Directory, new string('a', 64) + ".slpk");
        foreach (string f in new[] { old, fresh, kept })
        {
            File.WriteAllText(f, "x");
        }

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(-30));

        Assert.Equal(1, files.SweepPartials(TimeSpan.FromDays(1)));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void A_file_nothing_names_is_removed_once_a_day_old_and_a_named_one_never()
    {
        ScenePackageFiles files = new(_root);
        Directory.CreateDirectory(files.Directory);
        string named = new string('a', 64) + ".slpk", orphan = new string('b', 64) + ".slpk", young = new string('c', 64) + ".slpk";
        foreach (string f in new[] { named, orphan, young })
        {
            File.WriteAllText(Path.Combine(files.Directory, f), "x");
        }

        File.SetLastWriteTimeUtc(Path.Combine(files.Directory, named), DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(Path.Combine(files.Directory, orphan), DateTime.UtcNow.AddDays(-2));

        int removed = files.SweepOrphans(new System.Collections.Generic.HashSet<string> { $"scene-package-files/{named}" }, TimeSpan.FromDays(1));

        Assert.Equal(1, removed);
        Assert.True(File.Exists(Path.Combine(files.Directory, named)), "A file a row names was removed.");
        Assert.False(File.Exists(Path.Combine(files.Directory, orphan)), "A day-old file nothing names was kept.");
        Assert.True(File.Exists(Path.Combine(files.Directory, young)), "A file kept moments ago, its row not yet written, was removed.");
    }
}
