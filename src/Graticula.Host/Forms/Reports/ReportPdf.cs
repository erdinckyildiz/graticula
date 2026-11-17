using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host.Forms.Reports;

/// <summary>
/// Lays a filled feature report out as a PDF — ADR-302.
/// </summary>
/// <remarks>
/// The Word document is still the report (ADR-301); a PDF is that document laid out by a page-layout engine, so a template
/// Word opens can still be one the engine cannot place. A refusal is a <see cref="ReportPdfException"/> whose message is a
/// sentence for the person who asked, and the request answers with it and offers the Word document instead.
/// </remarks>
internal interface IReportPdf
{
    /// <summary>Whether the engine is installed beside this server; when it is not, PDF is not offered.</summary>
    bool Available { get; }

    /// <summary>The PDF of one filled <c>.docx</c>.</summary>
    /// <exception cref="ReportPdfException">The engine could not lay it out, or took too long.</exception>
    Task<byte[]> ConvertAsync(byte[] docx, CancellationToken cancellation);
}

/// <summary>A report the engine could not lay out, said as a sentence.</summary>
internal sealed class ReportPdfException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// <see cref="IReportPdf"/> in a child process, <c>Graticula.Report.Pdf</c> — ADR-302 §5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>The offline reader's shape</b> (<see cref="OfflineUploadReader"/>): one process per document, two paths on standard
/// input, the PDF back in a file, and the bounds — a deadline, a heap ceiling, a working-set ceiling polled from outside, a
/// lower priority — set here, because the engine is the part that cannot be trusted to bound itself.
/// </para>
/// <para>
/// <b>The deadline is a minute.</b> The slowest of thirteen public Survey123 templates took 14 s on the showcase's arm64
/// (ADR-302 §4); a minute is four times that, and a request a person is waiting on.
/// </para>
/// </remarks>
internal sealed class ReportPdfConverter : IReportPdf
{
    /// <summary>The engine's managed heap ceiling.</summary>
    public const long HeapLimitBytes = 1L << 30;

    /// <summary>Its working set, polled from outside, past which it is stopped.</summary>
    public const long MemoryCeilingBytes = 3L << 29;

    /// <summary>How long one document may take.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Where the image keeps the engine's fonts, as a home whose <c>.fonts</c> folder OfficeIMO reads — D-428.
    /// </summary>
    /// <remarks>
    /// <b>Not /usr/share/fonts, ever.</b> The fontconfig inside SkiaSharp's native reads that folder in this server's own
    /// process, and a font there killed 1.0.524 at its first map drawing on arm64 (deploy/server.Dockerfile, reportfonts).
    /// Only the child is pointed here, through its HOME; where the folder is absent, as on a developer's machine, the child
    /// keeps the HOME it inherits and reads the machine's fonts.
    /// </remarks>
    public const string DefaultFontsHome = "/opt/graticula/report-fonts";

    private static readonly TimeSpan MemoryPoll = TimeSpan.FromMilliseconds(250);

    private readonly string _executable;
    private readonly string _scratch;
    private readonly TimeSpan _deadline;
    private readonly string? _fontsHome;

    public ReportPdfConverter(string executable, string? fontsHome = DefaultFontsHome)
        : this(executable, Path.Combine(Path.GetTempPath(), "graticula-report-pdf"), DefaultDeadline, fontsHome)
    {
    }

    internal ReportPdfConverter(string executable, string scratch, TimeSpan deadline, string? fontsHome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratch);
        _executable = executable;
        _scratch = scratch;
        _deadline = deadline;
        _fontsHome = fontsHome is not null && Directory.Exists(Path.Combine(fontsHome, ".fonts")) ? fontsHome : null;
    }

    /// <summary>The HOME the engine's child is given, or null when it keeps the one it inherits.</summary>
    internal string? FontsHome => _fontsHome;

    /// <summary>Where the engine is in a build or an image: <c>reportpdf/</c> beside this assembly.</summary>
    public static string ExecutableBesideThisOne()
    {
        string name = OperatingSystem.IsWindows() ? "Graticula.Report.Pdf.exe" : "Graticula.Report.Pdf";
        return Path.Combine(AppContext.BaseDirectory, "reportpdf", name);
    }

    public bool Available => SiblingProcess.Installed(_executable);

    public async Task<byte[]> ConvertAsync(byte[] docx, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(docx);

        if (SiblingProcess.StartInfo(_executable) is not { } start)
        {
            throw new ReportPdfException("This server's PDF engine is not installed, so a report can be made as Word only.");
        }

        Directory.CreateDirectory(_scratch);
        string stem = Path.Combine(_scratch, Guid.NewGuid().ToString("N"));
        string input = stem + ".docx";
        string output = stem + ".pdf";

        try
        {
            await File.WriteAllBytesAsync(input, docx, cancellation).ConfigureAwait(false);
            string answer = await RunAsync(start, new JsonObject { ["input"] = input, ["output"] = output }.ToJsonString(), cancellation)
                .ConfigureAwait(false);
            Refusal(answer);

            return File.Exists(output)
                ? await File.ReadAllBytesAsync(output, cancellation).ConfigureAwait(false)
                : throw new ReportPdfException("The PDF engine said it had laid the report out and wrote nothing.");
        }
        finally
        {
            foreach (string path in (string[])[input, output])
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A leftover in the temporary folder is not a reason to fail a report that was made.
                }
            }
        }
    }

    /// <summary>Throws the engine's own reason unless it answered <c>{"ok": true}</c>.</summary>
    internal static void Refusal(string answer)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(answer);
            JsonElement root = parsed.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind is JsonValueKind.True)
            {
                return;
            }

            string why = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement said)
                && said.ValueKind == JsonValueKind.String
                    ? said.GetString()!
                    : "it gave no reason";
            throw new ReportPdfException($"The PDF engine could not lay this template out: {why.TrimEnd('.')}.");
        }
        catch (JsonException malformed)
        {
            throw new ReportPdfException(
                "The PDF engine answered something that is not JSON, so this server and the engine were built apart.", malformed);
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

        if (_fontsHome is not null)
        {
            start.Environment["HOME"] = _fontsHome;
        }

        using Process process = Process.Start(start)
            ?? throw new ReportPdfException("The PDF engine did not start.");
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timer.CancelAfter(_deadline);
        GeodatabaseReader.Yield(process);
        using GeodatabaseReader.MemoryGuard memory = new(process, MemoryCeilingBytes, timer, MemoryPoll);

        try
        {
            await process.StandardInput.WriteLineAsync(request.AsMemory(), timer.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timer.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            string? answer = await process.StandardOutput.ReadLineAsync(timer.Token).ConfigureAwait(false)
                ?? throw new ReportPdfException("The PDF engine stopped without answering. Its own account is on this server's error stream.");
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
            return answer;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            Kill(process);
            throw new ReportPdfException(memory.Exceeded is { } held
                ? $"Laying it out held {held / (1024.0 * 1024.0):0} MB, past the {MemoryCeilingBytes / (1024.0 * 1024.0):0} MB this server allows one report, and the engine was stopped."
                : $"Laying it out took longer than {_deadline.TotalSeconds:0.#} s, and the engine was stopped.");
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
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
