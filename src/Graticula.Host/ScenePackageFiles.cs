using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host;

/// <summary>
/// Scene layer packages above the document limit, as files in the server directories — ADR-274, by owner decision
/// ADR-272 §5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Named by their SHA-256, under <c>scene-package-files/</c></b> (not <c>scene-packages/</c>, which is each node's own copies, ScenePackages) in the server directories (ADR-012 phase 1: the state
/// directory on a node of its own, the one path every node mounts on a site). A row records the relative name, never an
/// absolute path, so a site's nodes and a restored backup read the same name.
/// </para>
/// <para>
/// <b>Streamed, never held.</b> An upload is copied to a partial file while it is hashed, then moved to its name; the
/// caller checks it is a package before recording it. A partial file older than a day is an upload that died, and the
/// sweep removes it. A complete file is removed only when no row of any organisation, and no copy in the recycle bin,
/// names it, and it is a day old (<see cref="SweepOrphans"/>, D-389).
/// </para>
/// </remarks>
internal sealed class ScenePackageFiles
{
    /// <summary>The folder under the server directories, and the prefix every recorded name starts with.</summary>
    public const string Folder = "scene-package-files";

    private const string PartialSuffix = ".part";

    private readonly string _root;

    /// <summary>Over a deployment's server directories.</summary>
    /// <param name="serverDirectoryRoot">The server directories' root (<see cref="HostSettings.ServerDirectoryRoot"/>).</param>
    public ScenePackageFiles(string serverDirectoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverDirectoryRoot);
        _root = serverDirectoryRoot;
    }

    /// <summary>Where the files are.</summary>
    public string Directory => Path.Combine(_root, Folder);

    /// <summary>
    /// The absolute path of a recorded name, or null when the name is not one this class writes — so a row can never
    /// point outside the folder.
    /// </summary>
    public string? PathOf(string? recorded)
    {
        if (string.IsNullOrEmpty(recorded)
            || !recorded.StartsWith(Folder + "/", StringComparison.Ordinal)
            || !IsHashName(recorded[(Folder.Length + 1)..]))
        {
            return null;
        }

        return Path.Combine(Directory, recorded[(Folder.Length + 1)..]);
    }

    /// <summary>Copies a stream to its hashed name and answers the recorded name, the hash and the size.</summary>
    public async Task<(string Recorded, string Sha256, long Size)> KeepAsync(Stream content, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(content);
        System.IO.Directory.CreateDirectory(Directory);
        string partial = Path.Combine(Directory, Guid.NewGuid().ToString("N") + PartialSuffix);
        string sha;
        long size;

        try
        {
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (FileStream file = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                byte[] buffer = new byte[1 << 16];
                int read;

                while ((read = await content.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                }

                size = file.Length;
                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            string name = sha + ".slpk";
            string final = Path.Combine(Directory, name);

            // The same bytes are the same file: a second upload of one package shares it.
            if (File.Exists(final))
            {
                File.Delete(partial);
            }
            else
            {
                File.Move(partial, final);
            }

            return ($"{Folder}/{name}", sha, size);
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Removes partial files older than <paramref name="age"/>: uploads that died. Answers how many.</summary>
    public int SweepPartials(TimeSpan age)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        int removed = 0;
        DateTime before = DateTime.UtcNow - age;

        foreach (string partial in System.IO.Directory.EnumerateFiles(Directory, "*" + PartialSuffix))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(partial) < before)
                {
                    File.Delete(partial);
                    removed++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>
    /// Removes the complete files no row names (D-389) — <paramref name="named"/> being every organisation's documents and
    /// the recycle bin's copies — once they are older than <paramref name="age"/>, so a file kept a moment before its row
    /// was written is never taken. Answers how many.
    /// </summary>
    public int SweepOrphans(IReadOnlySet<string> named, TimeSpan age)
    {
        ArgumentNullException.ThrowIfNull(named);
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        int removed = 0;
        DateTime before = DateTime.UtcNow - age;

        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.slpk"))
        {
            string name = Path.GetFileName(file);
            if (!IsHashName(name) || named.Contains($"{Folder}/{name}"))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(file) < before)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static bool IsHashName(string name) =>
        name.Length == 64 + 5 && name.EndsWith(".slpk", StringComparison.Ordinal)
        && name.AsSpan(0, 64).IndexOfAnyExcept(Hex) < 0;

    private static readonly System.Buffers.SearchValues<char> Hex = System.Buffers.SearchValues.Create("0123456789abcdef");
}
