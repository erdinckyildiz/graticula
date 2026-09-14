using System;
using System.IO;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>ADR-071: a thumbnail is drawn once, survives a restart, and is drawn again only when asked.</summary>
public sealed class ThumbnailsAreKeptUntilRedrawnTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "thumbs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_kept_picture_is_found_by_a_new_process_and_never_expires()
    {
        Guid layer = Guid.NewGuid();
        string key = ServiceThumbnails.KeyFor(layer, 336, 224);
        byte[] png = [1, 2, 3, 4];

        ServiceThumbnails.Held kept = new ServiceThumbnails(_directory).Keep(key, png, DateTimeOffset.UtcNow.AddDays(-30));

        // A new instance stands in for a restart: nothing in memory, the file on disk.
        ServiceThumbnails.Held? found = new ServiceThumbnails(_directory).Find(key);

        Assert.NotNull(found);
        Assert.Equal(png, found.Bytes);
        Assert.Equal(kept.ETag, found.ETag);
    }

    [Fact]
    public void Redrawing_forgets_every_size_of_that_layer_and_no_other()
    {
        Guid layer = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        ServiceThumbnails store = new(_directory);

        store.Keep(ServiceThumbnails.KeyFor(layer, 336, 224), [1], DateTimeOffset.UtcNow);
        store.Keep(ServiceThumbnails.KeyFor(layer, 672, 448), [2], DateTimeOffset.UtcNow);
        store.Keep(ServiceThumbnails.KeyFor(other, 336, 224), [3], DateTimeOffset.UtcNow);

        Assert.Equal(4, store.Forget(layer));

        ServiceThumbnails restarted = new(_directory);
        Assert.Null(restarted.Find(ServiceThumbnails.KeyFor(layer, 336, 224)));
        Assert.Null(restarted.Find(ServiceThumbnails.KeyFor(layer, 672, 448)));
        Assert.NotNull(restarted.Find(ServiceThumbnails.KeyFor(other, 336, 224)));
    }

    [Fact]
    public void An_empty_file_left_by_an_unfinished_write_is_drawn_again()
    {
        string key = ServiceThumbnails.KeyFor(Guid.NewGuid(), 336, 224);
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, key + ".png"), []);

        Assert.Null(new ServiceThumbnails(_directory).Find(key));
    }

    [Fact]
    public void A_store_with_no_directory_keeps_pictures_in_memory()
    {
        ServiceThumbnails store = new();
        string key = ServiceThumbnails.KeyFor(Guid.NewGuid(), 336, 224);

        store.Keep(key, [9], DateTimeOffset.UtcNow);

        Assert.NotNull(store.Find(key));
    }
}
