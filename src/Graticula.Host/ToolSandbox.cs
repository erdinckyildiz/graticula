using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Graticula.Host;

/// <summary>
/// How user tools are run on this deployment — ADR-223 §5.3 and §5.9, read from <c>Graticula:Tools:*</c>.
/// </summary>
/// <param name="Python">The interpreter, with the curated wheel set: <c>Graticula:Tools:Python</c>, <c>python3</c> by default.</param>
/// <param name="Grapy">The folder grapy's package is in: <c>Graticula:Tools:Grapy</c>, the one beside the server by default.</param>
/// <param name="Unfenced">
/// <c>Graticula:Tools:UnsafeUnfenced</c> — runs tools with no fence, for development on a system without Linux's Landlock
/// and seccomp. Its name says what it is; off by default, and refused nowhere it is off.
/// </param>
/// <param name="WallClock">How long a run may take, start to end, before its process tree is killed.</param>
/// <param name="MemoryBytes">Its address-space limit.</param>
/// <param name="CpuSeconds">Its CPU-time limit.</param>
/// <param name="FileBytes">The largest file it may write.</param>
/// <param name="Tasks">The ceiling on the OS user's tasks — RLIMIT_NPROC, which Linux counts per user.</param>
internal sealed record ToolSettings(
    string Python, string Grapy, bool Unfenced, TimeSpan WallClock, long MemoryBytes, int CpuSeconds, long FileBytes, int Tasks)
{
    /// <summary>The settings as configured, with ADR-223 §5.9c's defaults.</summary>
    public static ToolSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection tools = configuration.GetSection("Graticula:Tools");

        return new ToolSettings(
            tools["Python"] ?? "python3",
            tools["Grapy"] ?? Path.Combine(AppContext.BaseDirectory, "grapy"),
            bool.TryParse(tools["UnsafeUnfenced"], out bool unfenced) && unfenced,
            TimeSpan.FromMinutes(int.TryParse(tools["WallClockMinutes"], out int minutes) && minutes > 0 ? minutes : 30),
            (long.TryParse(tools["MemoryMegabytes"], out long memory) && memory > 0 ? memory : 2048) * 1024 * 1024,
            int.TryParse(tools["CpuSeconds"], out int cpu) && cpu > 0 ? cpu : 600,
            (long.TryParse(tools["FileMegabytes"], out long file) && file > 0 ? file : 1024) * 1024 * 1024,
            int.TryParse(tools["Tasks"], out int tasks) && tasks > 0 ? tasks : 1024);
    }

    /// <summary>Why tools cannot run here, or null when they can.</summary>
    public string? Refusal =>
        OperatingSystem.IsLinux() || Unfenced
            ? null
            : "User tools run fenced by Linux's Landlock and seccomp, and this server is not on Linux (ADR-223 §5.9b).";
}

/// <summary>
/// Runs grapy's runner over a run's directory, in its own process, and reads what it wrote — ADR-223 §5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fence is the runner's, put up on itself before the toolbox is imported</b>; this side gives it a clean
/// environment — none of the server's variables, so neither its keys nor its connection strings — the limits, and the
/// wall clock, past which the whole process tree is killed.
/// </para>
/// <para>
/// <b>-I, and the package's folder on the path by the command, not by PYTHONPATH</b>, which isolated mode ignores: the
/// interpreter reads nothing from the environment or the user's site-packages.
/// </para>
/// </remarks>
internal static class ToolSandbox
{
    /// <summary>What a run answered: the runner's document, or why there is none.</summary>
    internal sealed record Outcome(JsonObject? Result, int ExitCode, bool TimedOut, string ErrorTail);

    /// <summary>Runs the runner over a directory holding <c>run.json</c> and the toolbox.</summary>
    public static async Task<Outcome> RunAsync(string directory, ToolSettings settings, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Refusal is { } refusal)
        {
            return new Outcome(new JsonObject { ["ok"] = false, ["fence"] = false, ["error"] = refusal }, -1, false, string.Empty);
        }

        string grapy = settings.Grapy.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        string folder = directory.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

        ProcessStartInfo start = new(settings.Python)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"import sys; sys.path.insert(0, '{grapy}'); from grapy.runner import main; sys.exit(main('{folder}'))");

        // A clean environment: only what an interpreter needs, nothing of the server's.
        start.Environment.Clear();
        foreach (string kept in (string[])["PATH", "SYSTEMROOT", "LANG", "LC_ALL", "TZ"])
        {
            if (Environment.GetEnvironmentVariable(kept) is { } value)
            {
                start.Environment[kept] = value;
            }
        }

        start.Environment["HOME"] = directory;
        start.Environment["OPENBLAS_NUM_THREADS"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        if (settings.Unfenced)
        {
            start.Environment["GRAPY_UNFENCED"] = "1";
        }

        using Process process = new() { StartInfo = start };
        List<string> errors = [];
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lock (errors)
                {
                    errors.Add(line);
                    if (errors.Count > 40)
                    {
                        errors.RemoveAt(0);
                    }
                }
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(settings.WallClock);
        bool timedOut = false;

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellation.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended on its own between the deadline and the kill.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }

        string written = Path.Combine(directory, "result.json");
        JsonObject? result = File.Exists(written) ? JsonNode.Parse(await File.ReadAllTextAsync(written, CancellationToken.None).ConfigureAwait(false)) as JsonObject : null;

        string tail;
        lock (errors)
        {
            tail = string.Join('\n', errors);
        }

        return new Outcome(result, process.ExitCode, timedOut, tail);
    }

    /// <summary>The limits a run is told, as run.json carries them.</summary>
    public static JsonObject Limits(ToolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new JsonObject
        {
            ["memory"] = settings.MemoryBytes,
            ["cpu"] = settings.CpuSeconds,
            ["file"] = settings.FileBytes,
            ["processes"] = settings.Tasks,
        };
    }
}
