using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Graticula.Host;

/// <summary>
/// Opens a file a user's tool wrote in its run's directory — its result and its <c>outputs</c> — without following a
/// link and without waiting on a pipe (D-324).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="File.OpenRead(string)"/>.</b> The tool could write anything beneath its run's directory,
/// including a link, and a process it started may outlive it (the sandbox kills the tree only past the deadline). A
/// link at <c>outputs</c> or at the file would have this server read, for the tool, what the fence kept from it; a
/// check before an ordinary open leaves the window between the two to such a process; and a FIFO in the file's place
/// blocks an ordinary open for ever, holding the server's only tool slot. On Linux, where tools are fenced, the
/// directory and then the file are opened with <c>O_NOFOLLOW</c>, and the file with <c>O_NONBLOCK</c>, so the checks
/// hold for the handle that is read rather than for a path. A hard link is not a way round it: Landlock refuses a link
/// into the run's directory from anywhere the tool may not write (ABI 1 refuses every cross-directory link, ABI 2's
/// <c>REFER</c> right is granted only beneath the run).
/// </para>
/// <para>
/// <b>Elsewhere</b> tools run unfenced by an operator's explicit choice (<c>Graticula:Tools:UnsafeUnfenced</c>), and the
/// same checks are made on the path before the open.
/// </para>
/// <para>
/// <b>The name is the server's</b>: the parameter's, checked to be one plain name. Nothing the tool wrote in its result
/// becomes a path.
/// </para>
/// </remarks>
internal static class RunOutputs
{
    /// <summary>
    /// <c>outputs/&lt;parameter&gt;&lt;extension&gt;</c> beneath the run's <paramref name="directory"/>, open for reading,
    /// when it is a plain file reached through no link; else null.
    /// </summary>
    internal static FileStream? Open(string directory, string parameter, string extension)
    {
        if (parameter.Length == 0 || parameter.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || parameter is "." or ".."
            || parameter != Path.GetFileName(parameter))
        {
            return null;
        }

        return OpenIn(Path.Combine(directory, "outputs"), parameter + extension);
    }

    /// <summary>
    /// The runner's <c>result.json</c> in the run's <paramref name="directory"/>, open for reading, when it is a plain
    /// file and not a link; else null. The runner writes it from the tool's own process, so it is the tool's file too.
    /// </summary>
    internal static FileStream? OpenResult(string directory) => OpenIn(directory, "result.json");

    private static FileStream? OpenIn(string folder, string name) =>
        OperatingSystem.IsLinux() ? OpenOnLinux(folder, name) : OpenElsewhere(folder, name);

    private static FileStream? OpenElsewhere(string outputs, string name)
    {
        DirectoryInfo folder = new(outputs);
        FileInfo file = new(Path.Combine(outputs, name));

        if (!folder.Exists || folder.LinkTarget is not null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || !file.Exists || file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length == 0)
        {
            return null;
        }

        return Seekable(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: false));
    }

    private static FileStream? OpenOnLinux(string outputs, string name)
    {
        // The flags differ between the two architectures the server ships for, and mean other things elsewhere (on arm32
        // asm-generic's O_NOFOLLOW is O_LARGEFILE), so any other architecture opens nothing rather than follow a link.
        Architecture architecture = RuntimeInformation.ProcessArchitecture;

        if (architecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return null;
        }

        bool arm64 = architecture == Architecture.Arm64;
        int noFollow = arm64 ? 0x8000 : 0x20000;
        int directoryOnly = arm64 ? 0x4000 : 0x10000;
        const int NonBlocking = 0x800;
        const int CloseOnExec = 0x80000;

        int folder = Native.Open(outputs, directoryOnly | noFollow | CloseOnExec);

        if (folder < 0)
        {
            return null;
        }

        try
        {
            int file = Native.OpenAt(folder, name, noFollow | NonBlocking | CloseOnExec);

            if (file < 0)
            {
                return null;
            }

            SafeFileHandle handle = new((IntPtr)file, ownsHandle: true);

            try
            {
                return Seekable(new FileStream(handle, FileAccess.Read, 1 << 16, isAsync: false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A directory, or something else a stream is not made over.
                handle.Dispose();
                return null;
            }
        }
        finally
        {
            _ = Native.Close(folder);
        }
    }

    /// <summary>
    /// A regular file is seekable, not empty and readable; a FIFO is not seekable, and a directory, which Linux opens for
    /// reading and reports a length for, refuses the first read (measured on arm64, 2026-10-30).
    /// </summary>
    private static FileStream? Seekable(FileStream stream)
    {
        try
        {
            if (stream.CanSeek && stream.Length > 0 && stream.ReadByte() >= 0)
            {
                stream.Position = 0;
                return stream;
            }
        }
        catch (IOException)
        {
            // A directory: "Is a directory".
        }

        stream.Dispose();
        return null;
    }

    private static class Native
    {
        // The file itself: the server's images carry libc.so.6 and not the development link libc.so, which a bare "libc"
        // would be looked for as — found on a build machine, missing where the server runs (measured 2026-10-30).
        private const string Libc = "libc.so.6";

        // DllImport rather than LibraryImport, whose generated form needs unsafe code this project allows none of; paths
        // go as NUL-terminated UTF-8 bytes, what the kernel reads, so no string marshalling is chosen for them.
        [DllImport(Libc, EntryPoint = "open", SetLastError = true)]
        private static extern int OpenBytes(byte[] path, int flags);

        [DllImport(Libc, EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAtBytes(int directory, byte[] path, int flags);

        internal static int Open(string path, int flags) => OpenBytes(Terminated(path), flags);

        internal static int OpenAt(int directory, string path, int flags) => OpenAtBytes(directory, Terminated(path), flags);

        private static byte[] Terminated(string path) => System.Text.Encoding.UTF8.GetBytes(path + "\0");

        [DllImport(Libc, EntryPoint = "close", SetLastError = true)]
        internal static extern int Close(int handle);
    }
}
