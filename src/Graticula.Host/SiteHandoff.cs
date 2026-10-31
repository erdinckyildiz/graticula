using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Graticula.Host;

/// <summary>
/// What one request leaves for the next to pick up, kept where any node of a site can read it — ADR-012 §5 phase 2:
/// Pro's staged service definitions, ArcGIS append uploads, OGC Processes jobs and the pictures an export answered with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Files under the site's server directories, keyed by id</b> (phase 1's shared path, <c>handoff/&lt;kind&gt;/</c>): the
/// node that is asked the second step and does not hold the first in memory reads it from there. Each feature keeps its
/// memory as before, so a node that took the first step answers the second without touching the disk.
/// </para>
/// <para>
/// <b>Nothing at all on a node of its own</b> (<see cref="Root"/> null): there is no other node to hand anything to, so
/// a one-node deployment writes nothing it did not write before.
/// </para>
/// <para>
/// <b>Bounded by age</b>: each kind sweeps what it wrote past its own lifetime when it forgets its memory, so a file left
/// by a node that died is gone within the same lifetime as the memory it mirrored.
/// </para>
/// </remarks>
internal static class SiteHandoff
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = false };

    /// <summary>Where handoffs are kept — <c>handoff</c> under the server directories — or null on a node of its own.</summary>
    public static string? Root { get; set; }

    /// <summary>Whether a site's nodes hand state to one another.</summary>
    public static bool Shared => Root is not null;

    /// <summary>The directory of one kind, made if needed; null on a node of its own.</summary>
    public static string? Directory(string kind)
    {
        if (Root is null)
        {
            return null;
        }

        string directory = Path.Combine(Root, kind);
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Whether an id is safe to make a file name of — a request's id is a caller's text.</summary>
    public static bool Safe(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 96 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !id.StartsWith('.');

    /// <summary>Writes one handoff, replacing it whole: a reader sees the old one or the new one, never half.</summary>
    public static void Save<T>(string kind, string id, T value)
    {
        if (Directory(kind) is not { } directory || !Safe(id))
        {
            return;
        }

        string path = Path.Combine(directory, id + ".json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A handoff that cannot be written is a second step on another node that finds nothing — what it was before
            // phase 2. The node that has it in memory still answers.
            TryDelete(temporary);
        }
    }

    /// <summary>Reads one handoff, or default when there is none (or none on a node of its own).</summary>
    public static T? Load<T>(string kind, string id)
    {
        if (Root is null || !Safe(id))
        {
            return default;
        }

        string path = Path.Combine(Root, kind, id + ".json");

        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    /// <summary>Every handoff of a kind — for the few lookups that search rather than ask by id.</summary>
    public static IReadOnlyList<T> All<T>(string kind)
    {
        if (Root is null || !System.IO.Directory.Exists(Path.Combine(Root, kind)))
        {
            return [];
        }

        List<T> all = [];

        foreach (string path in System.IO.Directory.EnumerateFiles(Path.Combine(Root, kind), "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) is { } read)
                {
                    all.Add(read);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Being replaced or removed by another node while read: it is that node's.
            }
        }

        return all;
    }

    /// <summary>Removes one handoff and any file kept beside it under the same id.</summary>
    public static void Remove(string kind, string id)
    {
        if (Root is null || !Safe(id))
        {
            return;
        }

        TryDelete(Path.Combine(Root, kind, id + ".json"));
        TryDelete(Path.Combine(Root, kind, id + ".bin"));
    }

    /// <summary>The path of a file kept beside a handoff — an upload's bytes — or null on a node of its own.</summary>
    public static string? FileOf(string kind, string id) =>
        Directory(kind) is { } directory && Safe(id) ? Path.Combine(directory, id + ".bin") : null;

    /// <summary>Removes every handoff of a kind older than its lifetime, whichever node wrote it.</summary>
    public static void Sweep(string kind, TimeSpan lifetime)
    {
        if (Root is null || !System.IO.Directory.Exists(Path.Combine(Root, kind)))
        {
            return;
        }

        DateTime before = DateTime.UtcNow - lifetime;

        foreach (string path in System.IO.Directory.EnumerateFiles(Path.Combine(Root, kind)))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < before)
                {
                    File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Another node removed it first.
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Gone, or another node's to remove.
        }
    }
}
