using System;
using System.Diagnostics;
using System.IO;

namespace Graticula.Host;

/// <summary>
/// Starting one of this server's sibling executables — the overlay worker and the import reader.
/// </summary>
/// <remarks>
/// <para>
/// <b>Because the shipped image has no native launcher, and both siblings were therefore off in
/// every published release — [D-235](../../docs/architecture-debt.md).</b> A .NET executable
/// normally ships as two files: an <em>apphost</em>, which is native code for one architecture,
/// and the portable <c>.dll</c> that holds the program. `deploy/server.Dockerfile` publishes with
/// <c>UseAppHost=false</c> **on purpose** — [D-262](../../docs/architecture-debt.md): the image is
/// built for amd64 and arm64 from one compile, which is only possible while nothing in it is
/// native. So the image carries <c>overlay/Graticula.Overlay.Worker.dll</c> and no
/// <c>overlay/Graticula.Overlay.Worker</c>, and every published server said *the geometry overlay
/// worker is not installed* at startup and refused every overlay operation and every File
/// Geodatabase and shapefile import.
/// </para>
/// <para>
/// <b>Found by starting a server against a restored backup on 2026-09-16</b> and reading its log —
/// which is also what the running showcase had been saying since the image was first built. D-235
/// was repaired twice before this, and both repairs were about the files being <em>copied</em>;
/// nobody asked whether the thing the host actually launches was among them.
/// </para>
/// <para>
/// <b>The fix is to launch the portable half the way this server itself is launched.</b> The image's
/// entry point is <c>dotnet Graticula.Host.dll</c>, so a muxer is present by definition — and
/// <see cref="Environment.ProcessPath"/> names it, which is better than trusting <c>PATH</c> to
/// hold the same .NET this server is running on. Where an apphost does exist — every development
/// machine, because <c>dotnet build</c> makes one — it is used unchanged, so nothing about how the
/// tests run changes.
/// </para>
/// </remarks>
internal static class SiblingProcess
{
    /// <summary>The portable half of a sibling, beside its apphost.</summary>
    /// <remarks>
    /// <b>Not <see cref="Path.ChangeExtension(string, string)"/>, and that cost a release.</b>
    /// These names carry dots — <c>Graticula.Overlay.Worker</c> — so on Linux, where an apphost
    /// has no extension at all, <c>ChangeExtension</c> reads <c>.Worker</c> as the extension and
    /// answers <c>Graticula.Overlay.dll</c>: a file that has never existed. Written that way and
    /// tested on Windows, where the name ends in <c>.exe</c> and the same call is right, it
    /// passed four tests and failed the first Linux image — caught by the quickstart rehearsal's
    /// new check on the server's own startup log, in the release that added it.
    /// </remarks>
    /// <param name="executable">The apphost path, as the sibling's own class names it.</param>
    /// <returns>The <c>.dll</c> path.</returns>
    internal static string Portable(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(executable.AsSpan(0, executable.Length - 4), ".dll")
            : executable + ".dll";
    }

    /// <summary>
    /// Whether this deployment can start the sibling at all, by either route.
    /// </summary>
    /// <param name="executable">The apphost path.</param>
    /// <returns>Whether something is there to run.</returns>
    internal static bool Installed(string executable) =>
        File.Exists(executable) || File.Exists(Portable(executable));

    /// <summary>
    /// How to start it: the apphost where there is one, otherwise this server's own .NET host
    /// pointed at the sibling's assembly.
    /// </summary>
    /// <remarks>
    /// <b>The caller sets its own redirections and environment on what comes back</b>, because the
    /// two siblings want different ones — the reader caps its heap for GDAL, the worker for
    /// OverlayNG — and neither difference belongs here.
    /// </remarks>
    /// <param name="executable">The apphost path.</param>
    /// <returns>A start that launches the sibling, or null when it is not installed.</returns>
    internal static ProcessStartInfo? StartInfo(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        if (File.Exists(executable))
        {
            return new ProcessStartInfo(executable);
        }

        string portable = Portable(executable);

        if (!File.Exists(portable))
        {
            return null;
        }

        ProcessStartInfo start = new(Muxer());
        start.ArgumentList.Add(portable);

        return start;
    }

    /// <summary>
    /// The .NET host to run a portable assembly with.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Environment.ProcessPath"/> first, because it is this server's own runtime.</b>
    /// A server started as <c>dotnet Graticula.Host.dll</c> — every published image — reports the
    /// muxer here, so the sibling runs on the same .NET rather than on whichever one a
    /// <c>PATH</c> lookup finds. A server started through its own apphost reports that instead,
    /// which is not a muxer, so the name is checked rather than assumed and <c>PATH</c> is the
    /// fallback.
    /// </remarks>
    /// <returns>A path or a command name.</returns>
    private static string Muxer()
    {
        string expected = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        return Environment.ProcessPath is { Length: > 0 } running
            && string.Equals(Path.GetFileName(running), expected, StringComparison.OrdinalIgnoreCase)
                ? running
                : "dotnet";
    }
}
