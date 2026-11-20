using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-429: a user tool's process is started with every temporary-file variable pointing at its run directory, the only
/// directory the fence lets it write. Without them SQLite spilled a GeoPackage write of 4,515 polygons to /var/tmp,
/// Landlock refused the open, and the run failed as "unable to open database file". They must be in the starting
/// environment, not set by the runner, because importing grapy initialises SQLite, which reads them once.
/// </summary>
public sealed class AToolRunsTemporaryFilesStayInItsDirectoryTests
{
    private static readonly ToolSettings Settings = new(
        "python3", "/app/grapy", false, TimeSpan.FromMinutes(1), 2L << 30, 600, 1L << 30, 1024);

    [Fact]
    public void Every_temporary_variable_is_the_run_directory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "run-d429");

        ProcessStartInfo start = ToolSandbox.StartInfo(directory, Settings);

        foreach (string name in new[] { "SQLITE_TMPDIR", "TMPDIR", "TEMP", "TMP", "CPL_TMPDIR" })
        {
            Assert.Equal(directory, start.Environment[name]);
        }
    }

    [Fact]
    public void The_environment_is_still_clean_of_the_servers_own_variables()
    {
        Environment.SetEnvironmentVariable("GRATICULA_D429_PROBE", "secret-ish");
        try
        {
            ProcessStartInfo start = ToolSandbox.StartInfo("/runs/one", Settings);

            Assert.False(start.Environment.ContainsKey("GRATICULA_D429_PROBE"));
            Assert.False(start.Environment.ContainsKey("GRAPY_UNFENCED"));
            Assert.Equal("/runs/one", start.Environment["HOME"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRATICULA_D429_PROBE", null);
        }
    }
}
