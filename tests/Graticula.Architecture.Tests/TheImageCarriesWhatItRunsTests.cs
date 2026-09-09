using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// The sibling executables the host runs reach the artefact that ships.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-235](../../docs/architecture-debt.md), and it survived a release.</b> `v0.1.0` is
/// tagged; no image built from it could answer a single GeometryServer overlay operation,
/// because `dotnet publish` produced no `overlay/` directory and `GeometryWorkerPool`
/// resolves the worker at <c>AppContext.BaseDirectory/overlay</c>. Measured 2026-09-09: a
/// publish of the host produced **49 entries and neither `overlay/` nor `importer/`**.
/// </para>
/// <para>
/// <b>It survived because the development loop never meets it.</b> `dotnet build` puts both
/// siblings in place, so every developer machine and every test run has them. Only `publish`
/// drops them, and only the published thing is shipped. A gap that appears exclusively in
/// the artefact nobody runs locally is the classic shape of one that lasts.
/// </para>
/// <para>
/// <b>There were two independent causes and either alone would have kept the image
/// broken.</b> The copy targets ran <c>AfterTargets="Build"</c> into <c>$(OutDir)</c>, and a
/// publish assembles <c>$(PublishDir)</c> from known content items — so nothing dropped into
/// the build output afterwards is in it. And <c>Directory.Build.targets</c> was not copied
/// into the Docker build context at all, so inside the image build those targets did not
/// exist: **MSBuild does not miss a targets file it was never told about**, and there is no
/// warning anywhere in that.
/// </para>
/// <para>
/// <b>Textual, and deliberately.</b> The honest assertion is *does a publish carry the
/// worker*, and running one in a unit test costs a minute and a clean output directory. What
/// can be checked cheaply is that the two mechanisms that carry it are both still present —
/// which is exactly what was missing, twice, in two different files. A test that ran the
/// publish would be better; a test that runs is better than one nobody waits for.
/// </para>
/// </remarks>
public sealed class TheImageCarriesWhatItRunsTests
{
    /// <summary>The repository root, from the test assembly's own location.</summary>
    private static string Root
    {
        get
        {
            DirectoryInfo? at = new(AppContext.BaseDirectory);

            while (at is not null && !Directory.Exists(Path.Combine(at.FullName, "src")))
            {
                at = at.Parent;
            }

            Assert.True(at is not null, "Could not find the repository root from the test assembly.");

            return at!.FullName;
        }
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root }.Concat(parts).ToArray()));

    [Fact]
    public void A_publish_carries_the_overlay_worker_and_not_only_a_build()
    {
        string targets = Read("Directory.Build.targets");

        Assert.True(
            Regex.IsMatch(targets, @"AfterTargets\s*=\s*""Publish"""),
            "Directory.Build.targets has no target that runs after Publish, so a published "
            + "host carries no `overlay/` and every GeometryServer overlay operation answers "
            + "with the worker missing. The copy that runs after Build is not enough: a "
            + "publish assembles $(PublishDir) and does not take what was dropped into "
            + "$(OutDir) afterwards. D-235.");

        Assert.True(
            targets.Contains("$(PublishDir)overlay", StringComparison.Ordinal),
            "Nothing copies the overlay worker into $(PublishDir)overlay, which is where "
            + "GeometryWorkerPool looks: AppContext.BaseDirectory/overlay.");
    }

    [Fact]
    public void The_publish_target_fails_rather_than_shipping_an_image_without_it()
    {
        string targets = Read("Directory.Build.targets");

        int publish = targets.IndexOf("PublishOverlayWorker", StringComparison.Ordinal);

        Assert.True(publish >= 0, "The publish target has been renamed or removed.");

        Assert.True(
            targets.IndexOf("<Error", publish, StringComparison.Ordinal) >= 0,
            "The publish target does not fail when the overlay worker's output is empty. A "
            + "silent copy of nothing is how this shipped in the first place — the image was "
            + "assembled, published and tagged, and the only evidence was a log line at the "
            + "first overlay request.");
    }

    [Fact]
    public void The_image_build_can_see_the_file_that_carries_the_siblings()
    {
        string dockerfile = Read("deploy", "server.Dockerfile");

        Assert.True(
            dockerfile.Contains("Directory.Build.targets", StringComparison.Ordinal),
            "deploy/server.Dockerfile does not copy Directory.Build.targets into the build "
            + "context, so inside the image the targets that place the sibling executables "
            + "beside the host do not exist. This is the second of D-235's two causes and it "
            + "is the quieter one: MSBuild does not miss a targets file it was never told "
            + "about, so the image builds clean and ships without them.");

        Assert.True(
            dockerfile.IndexOf("Directory.Build.targets", StringComparison.Ordinal)
                < dockerfile.IndexOf("dotnet publish", StringComparison.Ordinal),
            "Directory.Build.targets is copied after the publish that needs it.");
    }
}
