using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host;

/// <summary>
/// The mobile geodatabase files of preplanned map area packages, kept in the server directories — ADR-288 §11a.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside the scene packages, not among them.</b> ADR-274's <c>scene-package-files/</c> is swept of every file no
/// document names (D-389); a package's file is named by a row in another table, and a sweep that does not know it would
/// take it. This folder has its own, and no sweep yet (D-407).
/// </para>
/// <para>
/// <b>Named by a fresh id, not by the hash.</b> Two packages written from the same rows are two replicas, and each owns
/// its file: deleting one must not take the other's. A row records the relative name, never an absolute path, so a site's
/// nodes and a restored backup read the same name.
/// </para>
/// </remarks>
internal sealed partial class MapAreaPackageFiles
{
    /// <summary>The folder under the server directories, and the prefix every recorded name starts with.</summary>
    public const string Folder = "map-area-package-files";

    private readonly string _root;

    /// <summary>Over a deployment's server directories.</summary>
    /// <param name="serverDirectoryRoot">The server directories' root (<see cref="HostSettings.ServerDirectoryRoot"/>).</param>
    public MapAreaPackageFiles(string serverDirectoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverDirectoryRoot);
        _root = serverDirectoryRoot;
    }

    [GeneratedRegex("^[0-9a-f]{32}\\.geodatabase$")]
    private static partial Regex FileName();

    /// <summary>Where the files are.</summary>
    public string Directory => Path.Combine(_root, Folder);

    /// <summary>A path for a file about to be written, and the name to record for it.</summary>
    /// <returns>The absolute path and the recorded relative name.</returns>
    public (string Path, string Recorded) Next()
    {
        System.IO.Directory.CreateDirectory(Directory);
        string name = Guid.NewGuid().ToString("N") + ".geodatabase";
        return (System.IO.Path.Combine(Directory, name), $"{Folder}/{name}");
    }

    /// <summary>
    /// The absolute path of a recorded name, or null when the name is not one this class writes — so a row can never
    /// point outside the folder.
    /// </summary>
    /// <param name="recorded">The recorded name.</param>
    /// <returns>The path, or null.</returns>
    public string? PathOf(string? recorded)
    {
        if (string.IsNullOrEmpty(recorded)
            || !recorded.StartsWith(Folder + "/", StringComparison.Ordinal)
            || !FileName().IsMatch(recorded[(Folder.Length + 1)..]))
        {
            return null;
        }

        return System.IO.Path.Combine(Directory, recorded[(Folder.Length + 1)..]);
    }

    /// <summary>Removes a recorded file if it is there; false when there was nothing to remove or it could not be.</summary>
    /// <param name="recorded">The recorded name.</param>
    /// <returns>Whether a file was removed.</returns>
    public bool Delete(string? recorded)
    {
        if (PathOf(recorded) is not { } path || !File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The SHA-256 (lower-case hexadecimal) and length of a file, read as a stream.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The hash and the size.</returns>
    public static async Task<(string Sha256, long Size)> HashAsync(string path, CancellationToken cancellation)
    {
        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false);
        return (Convert.ToHexStringLower(hash), file.Length);
    }
}
