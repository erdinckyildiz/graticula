using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;

namespace Graticula.Host;

/// <summary>
/// Reads a client's <c>synchronizeReplica</c> upload in a process of its own — ADR-288 condition 7.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's decision of 2026-11-10, "Ayrı sürece taşı" (Q-233, D-402).</b> The upload is a mobile geodatabase
/// a client chose, and reading it parses that file with the native <c>e_sqlite3</c>. ADR-009 §2.2 keeps an
/// untrusted-file parser out of the process that serves public requests; <see cref="GeodatabaseReader"/> is how GDAL
/// honours that, and this is the same arrangement for SQLite. The child is <c>Graticula.Offline.Reader</c>, built and
/// copied into an <c>offline</c> directory beside the server (<c>Directory.Build.targets</c>), and started the way
/// every sibling is (<see cref="SiblingProcess"/>).
/// </para>
/// <para>
/// <b>Only the read moved.</b> The host still writes offline copies and download deltas with SQLite in-process, behind
/// <see cref="IMobileGeodatabaseWriter"/>: those start from rows this server holds, not from a client's file.
/// </para>
/// <para>
/// <b>The bounds are <see cref="GeodatabaseReader"/>'s</b> — a deadline, a managed heap ceiling, a working-set ceiling
/// polled from outside (which counts SQLite's native allocation), and a priority below the server's — with numbers of
/// their own. As there, none of them is a container or a seccomp fence; <see cref="ToolSandbox"/>'s Landlock fence is
/// for user Python and is not applied to either reader.
/// </para>
/// <para>
/// <b>Every failure is an <see cref="InvalidOperationException"/> with a sentence a client can be shown</b>, because the
/// replica route turns that into <i>"The uploaded delta could not be read: … Nothing was applied."</i>
/// </para>
/// </remarks>
internal sealed class OfflineUploadReader : IOfflineDeltaReader
{
    /// <summary>The managed heap ceiling for the child.</summary>
    /// <remarks>
    /// A gigabyte, twice the import reader's, because this child holds every attachment of an upload in memory while it
    /// writes them back — a field worker's photos — where the import reader streams rows.
    /// </remarks>
    public const long HeapLimitBytes = 1L << 30;

    /// <summary>How much of the machine the child may hold, native allocation included.</summary>
    /// <remarks>The import reader's two gigabytes, and a judgement for the same reason as there.</remarks>
    public const long MemoryCeilingBytes = 2L << 30;

    /// <summary>How long one upload may take to read before the child is killed.</summary>
    /// <remarks>
    /// Two minutes. The owner's measured sync read three rows and a 45 KB photo; this bounds the case nobody has met, and
    /// a <c>synchronizeReplica</c> request is held open for it, so it is shorter than any import's.
    /// </remarks>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan MemoryPoll = TimeSpan.FromMilliseconds(250);

    private readonly string _executable;
    private readonly string _scratch;
    private readonly TimeSpan _deadline;
    private readonly long _ceiling;

    /// <summary>Creates the reader with the default bounds.</summary>
    /// <param name="executable">Where the child is.</param>
    public OfflineUploadReader(string executable)
        : this(executable, Path.Combine(Path.GetTempPath(), "graticula-offline-reads"), DefaultDeadline, MemoryCeilingBytes)
    {
    }

    /// <summary>Creates the reader with bounds of its own, so tests can reach them.</summary>
    /// <param name="executable">Where the child is.</param>
    /// <param name="scratch">The folder the child's answer is written in, one file per read, deleted after.</param>
    /// <param name="deadline">How long the child has.</param>
    /// <param name="ceiling">How much the child may hold.</param>
    internal OfflineUploadReader(string executable, string scratch, TimeSpan deadline, long ceiling)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratch);
        ArgumentOutOfRangeException.ThrowIfLessThan(ceiling, 1L << 20);

        _executable = executable;
        _scratch = scratch;
        _deadline = deadline;
        _ceiling = ceiling;
    }

    /// <summary>The child, in its own directory beside the server's.</summary>
    /// <remarks>Beside the host rather than configurable, for the reason <see cref="GeodatabaseReader.ExecutableBesideThisOne"/> gives.</remarks>
    /// <returns>The apphost path.</returns>
    public static string ExecutableBesideThisOne()
    {
        string name = OperatingSystem.IsWindows() ? "Graticula.Offline.Reader.exe" : "Graticula.Offline.Reader";

        return Path.Combine(AppContext.BaseDirectory, "offline", name);
    }

    /// <summary>Whether the child is installed where this server expects it.</summary>
    public bool Available => SiblingProcess.Installed(_executable);

    /// <inheritdoc />
    public async Task<OfflineDelta> ReadDeltaAsync(string path, IReadOnlyList<OfflineLayer> layers, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(layers);

        if (SiblingProcess.StartInfo(_executable) is not { } start)
        {
            throw new InvalidOperationException(
                $"This server's offline reader is not installed at '{_executable}', nor is the portable "
                + $"'{Path.GetFileName(SiblingProcess.Portable(_executable))}' beside it, and an upload is read only "
                + "there (ADR-288 condition 7).");
        }

        Directory.CreateDirectory(_scratch);
        string output = Path.Combine(_scratch, Guid.NewGuid().ToString("N") + ".delta");

        try
        {
            string answer = await RunAsync(start, new JsonObject
            {
                ["upload"] = Path.GetFullPath(path),
                ["output"] = output,
                ["layers"] = OfflineDeltaWire.Shapes(layers),
            }.ToJsonString(), cancellationToken).ConfigureAwait(false);

            Refusal(answer);

            if (!File.Exists(output))
            {
                throw new InvalidOperationException("The offline reader said it had read the upload and wrote nothing.");
            }

            try
            {
                using FileStream file = new(output, FileMode.Open, FileAccess.Read, FileShare.None);
                return OfflineDeltaWire.Read(file, layers);
            }
            catch (InvalidDataException malformed)
            {
                throw new InvalidOperationException(
                    $"The offline reader's answer is not in the form this server reads: {malformed.Message}", malformed);
            }
        }
        finally
        {
            try
            {
                File.Delete(output);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover in the temporary folder is not a reason to fail a sync that worked.
            }
        }
    }

    /// <summary>Throws the child's own refusal, or nothing when it says it succeeded.</summary>
    /// <param name="answer">The child's one line.</param>
    internal static void Refusal(string answer)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(answer);
            JsonElement root = parsed.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind is JsonValueKind.True)
            {
                return;
            }

            string why = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out JsonElement said)
                && said.ValueKind == JsonValueKind.String
                    ? said.GetString()!
                    : "it gave no reason.";

            throw new InvalidOperationException(why);
        }
        catch (JsonException malformed)
        {
            throw new InvalidOperationException(
                "The offline reader answered something that is not JSON, so this server and the reader were built apart "
                + "or the reader wrote to standard output what belongs on standard error.",
                malformed);
        }
    }

    private async Task<string> RunAsync(ProcessStartInfo start, string request, CancellationToken cancellation)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = false;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.Environment["DOTNET_GCHeapHardLimit"] = HeapLimitBytes.ToString("X", CultureInfo.InvariantCulture);
        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment["DOTNET_gcConcurrent"] = "0";

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"The offline reader at '{_executable}' did not start.");

        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timer.CancelAfter(_deadline);

        GeodatabaseReader.Yield(process);

        using GeodatabaseReader.MemoryGuard memory = new(process, _ceiling, timer, MemoryPoll);

        try
        {
            await process.StandardInput.WriteLineAsync(request.AsMemory(), timer.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timer.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            string? answer = await process.StandardOutput.ReadLineAsync(timer.Token).ConfigureAwait(false);

            if (answer is null)
            {
                throw new InvalidOperationException(
                    "The offline reader stopped without answering, which is what a crash inside SQLite looks like. Its "
                    + "own account is on this server's error stream.");
            }

            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);

            return answer;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            Kill(process);

            throw new InvalidOperationException(memory.Exceeded is { } held
                ? $"Reading it held {held / (1024.0 * 1024.0):0} MB, past the {_ceiling / (1024.0 * 1024.0):0} MB this "
                    + "server allows one upload, and the reader was stopped."
                : $"Reading it took longer than {_deadline.TotalSeconds:0.#} s, and the reader was stopped.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        finally
        {
            Kill(process);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone between the check and the kill, which is the ordinary race.
        }
    }
}
