using System;
using System.Diagnostics;
using System.IO;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// How the server starts the overlay worker and the import reader — D-235, 2026-09-16.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every published image shipped both siblings and could start neither.</b> The image publishes
/// portable IL on purpose — one compile for amd64 and arm64, D-262 — so it has
/// <c>Graticula.Overlay.Worker.dll</c> and no native <c>Graticula.Overlay.Worker</c> beside it, and
/// the host looked only for the second. Every check the quickstart rehearsal runs passed the whole
/// time, because none of them asks for an overlay or an import.
/// </para>
/// <para>
/// <b>These cases are the two shapes of deployment</b>, written against files on disk rather than
/// against a mocked filesystem: a development machine, where <c>dotnet build</c> makes an apphost,
/// and an image, where nothing is native.
/// </para>
/// </remarks>
public sealed class SiblingProcessTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "graticula-sibling-" + Guid.NewGuid().ToString("N")[..8]);

    public SiblingProcessTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that outlives one test run costs nothing worth failing over.
        }
    }

    private string Executable(string name) =>
        Path.Combine(_folder, OperatingSystem.IsWindows() ? name + ".exe" : name);

    /// <summary>
    /// The assembly beside an apphost, for both shapes of name — and this is the case that shipped.
    /// </summary>
    /// <remarks>
    /// <b>Named paths rather than the temporary directory, so both run on either operating
    /// system.</b> The first version of this type used <c>Path.ChangeExtension</c>, which is right
    /// for <c>Graticula.Import.Reader.exe</c> and wrong for <c>Graticula.Import.Reader</c>: the
    /// dots in the name make <c>.Reader</c> look like an extension, so a Linux image looked for
    /// <c>Graticula.Import.dll</c> and found nothing. Every test here passed on Windows while that
    /// was true.
    /// </remarks>
    [Theory]
    [InlineData("/app/overlay/Graticula.Overlay.Worker", "/app/overlay/Graticula.Overlay.Worker.dll")]
    [InlineData("/app/importer/Graticula.Import.Reader", "/app/importer/Graticula.Import.Reader.dll")]
    [InlineData(@"C:pp\overlay\Graticula.Overlay.Worker.exe", @"C:pp\overlay\Graticula.Overlay.Worker.dll")]
    [InlineData("worker", "worker.dll")]
    public void The_portable_half_keeps_the_whole_name(string executable, string expected) =>
        Assert.Equal(expected, SiblingProcess.Portable(executable));

    [Fact]
    public void Nothing_there_is_not_installed_and_starts_nothing()
    {
        string executable = Executable("Graticula.Overlay.Worker");

        Assert.False(SiblingProcess.Installed(executable));
        Assert.Null(SiblingProcess.StartInfo(executable));
    }

    [Fact]
    public void An_apphost_is_launched_directly()
    {
        string executable = Executable("Graticula.Overlay.Worker");
        File.WriteAllText(executable, "not really a binary");

        Assert.True(SiblingProcess.Installed(executable));

        ProcessStartInfo start = Assert.IsType<ProcessStartInfo>(SiblingProcess.StartInfo(executable));

        Assert.Equal(executable, start.FileName);
        Assert.Empty(start.ArgumentList);
    }

    [Fact]
    public void The_portable_assembly_alone_is_launched_with_this_server_s_own_dotnet()
    {
        string executable = Executable("Graticula.Import.Reader");
        string portable = Path.Combine(_folder, "Graticula.Import.Reader.dll");

        File.WriteAllText(portable, "not really an assembly");

        // The shape of the published image: the assembly is there and nothing native is.
        Assert.False(File.Exists(executable));
        Assert.True(SiblingProcess.Installed(executable));

        ProcessStartInfo start = Assert.IsType<ProcessStartInfo>(SiblingProcess.StartInfo(executable));

        Assert.Equal(portable, Assert.Single(start.ArgumentList));

        // <b>A muxer, by name.</b> It is this process's own runtime where that is what started
        // the server — which is every published image, whose entry point is
        // `dotnet Graticula.Host.dll`. The test runner is started through `testhost`, so this
        // run takes the other branch and gets the bare command name for PATH to resolve; both
        // answers are the muxer and neither is a path this test can predict.
        Assert.Contains(
            Path.GetFileName(start.FileName),
            (string[])["dotnet", "dotnet.exe"],
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_apphost_wins_when_both_are_there()
    {
        string executable = Executable("Graticula.Overlay.Worker");

        File.WriteAllText(executable, "not really a binary");
        File.WriteAllText(Path.Combine(_folder, "Graticula.Overlay.Worker.dll"), "nor this");

        ProcessStartInfo start = Assert.IsType<ProcessStartInfo>(SiblingProcess.StartInfo(executable));

        Assert.Equal(executable, start.FileName);
        Assert.Empty(start.ArgumentList);
    }
}
