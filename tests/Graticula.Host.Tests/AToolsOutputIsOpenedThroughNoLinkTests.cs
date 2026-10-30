using System;
using System.IO;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-324: a file a user's tool wrote is opened where the server says, as a plain file reached through no link — a link
/// would have the server read, for the tool, what the fence kept from it.
/// </summary>
public sealed class AToolsOutputIsOpenedThroughNoLinkTests : IDisposable
{
    private readonly string _run = Path.Combine(Path.GetTempPath(), "run-" + Guid.NewGuid().ToString("N"));
    private readonly string _secret = Path.Combine(Path.GetTempPath(), "secret-" + Guid.NewGuid().ToString("N"));

    public AToolsOutputIsOpenedThroughNoLinkTests()
    {
        Directory.CreateDirectory(Path.Combine(_run, "outputs"));
        File.WriteAllText(_secret, "not the tool's to read");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_run, recursive: true);
            File.Delete(_secret);
        }
        catch (IOException)
        {
            // A temporary directory; the next run's is another.
        }
    }

    [Fact]
    public void A_plain_file_is_opened()
    {
        File.WriteAllBytes(Path.Combine(_run, "outputs", "out.tif"), [1, 2, 3]);

        using FileStream? opened = RunOutputs.Open(_run, "out", ".tif");

        Assert.NotNull(opened);
        Assert.Equal(3, opened!.Length);
    }

    [Theory]
    [InlineData("../out")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("")]
    public void A_name_that_is_not_one_plain_name_is_refused(string parameter)
    {
        File.WriteAllBytes(Path.Combine(_run, "out.tif"), [1]);

        Assert.Null(RunOutputs.Open(Path.Combine(_run, "outputs"), parameter, ".tif"));
    }

    [Fact]
    public void An_empty_file_or_none_is_refused()
    {
        File.WriteAllBytes(Path.Combine(_run, "outputs", "empty.tif"), []);

        Assert.Null(RunOutputs.Open(_run, "empty", ".tif"));
        Assert.Null(RunOutputs.Open(_run, "missing", ".tif"));
    }

    [Fact]
    public void A_directory_in_the_files_place_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_run, "outputs", "dir.tif"));

        Assert.Null(RunOutputs.Open(_run, "dir", ".tif"));
    }

    [Fact]
    public void A_link_in_the_files_place_is_refused()
    {
        if (!TryLink(() => File.CreateSymbolicLink(Path.Combine(_run, "outputs", "out.tif"), _secret)))
        {
            return;
        }

        Assert.Null(RunOutputs.Open(_run, "out", ".tif"));
    }

    [Fact]
    public void A_link_in_the_outputs_place_is_refused()
    {
        string elsewhere = Path.Combine(_run, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.Copy(_secret, Path.Combine(elsewhere, "out.tif"));
        Directory.Delete(Path.Combine(_run, "outputs"));

        if (!TryLink(() => Directory.CreateSymbolicLink(Path.Combine(_run, "outputs"), elsewhere)))
        {
            return;
        }

        Assert.Null(RunOutputs.Open(_run, "out", ".tif"));
    }

    [Fact]
    public void A_link_in_the_results_place_is_refused()
    {
        if (!TryLink(() => File.CreateSymbolicLink(Path.Combine(_run, "result.json"), _secret)))
        {
            return;
        }

        Assert.Null(RunOutputs.OpenResult(_run));
    }

    /// <summary>Windows makes links only with a privilege or developer mode; where it cannot, there is nothing to refuse.</summary>
    private static bool TryLink(Action make)
    {
        try
        {
            make();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
