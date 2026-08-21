using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// A native library that parses somebody else's file is referenced by one project and no other.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-88], and it exists because a rule was downgraded from a test to a convention.</b>
/// [A-016](../../docs/architecture-assumptions.md) and
/// [ADR-009](../../docs/adr/ADR-009-raster-engine.md) §2.2 said *"the serving container ships no
/// GDAL — it exists only in the job worker image"*, and §2.2 is explicit about why that form was
/// chosen: it was strengthened *"from a placement decision to an artefact rule"* because an image
/// boundary is **checkable at build time** rather than depending on discipline.
/// </para>
/// <para>
/// <b>[ADR-037](../../docs/adr/ADR-037-job-workers-come-in-two-kinds.md) §5a spent that.</b> GDAL is
/// now a package in the solution, loaded by a child process rather than by a separate image. The
/// isolation survives — the serving process never loads it — but the *check* did not, and
/// [Q-28](../../docs/open-questions.md) had recorded the stricter form precisely because *"a package
/// reference is a thing a test can forbid, where an image boundary is a thing a human has to notice."*
/// </para>
/// <para>
/// <b>So this is that test, in the weaker place it now has to live.</b> Not *no reference anywhere*,
/// which is no longer true, but *exactly one project, named*. This repository has recorded four times
/// what happens to a rule nobody checks — <see href="../../docs/architecture-debt.md">D-46</see> for
/// UI shapes, D-71 for a constant that became a lookup, D-74 for an enumeration that gained a member,
/// D-83 for a value the console never learned — and each of those was a convention that held until it
/// quietly did not.
/// </para>
/// </remarks>
public sealed class NativeDependencyTests
{
    /// <summary>
    /// Packages that carry a native library, and the one project each may appear in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Matched by prefix, because a native dependency arrives as a family.</b> GDAL's managed
    /// binding is <c>MaxRev.Gdal.Core</c> and its payloads are <c>MaxRev.Gdal.LinuxRuntime.Minimal</c>
    /// and <c>MaxRev.Gdal.WindowsRuntime.Minimal</c>; a rule naming only the first would pass while the
    /// second was referenced from the server. That is D-74's shape — a set of values with no single
    /// place that names them all.
    /// </para>
    /// <para>
    /// <b>NetTopologySuite is here too, and it is not native.</b> It is in this list because the
    /// argument is the same and was measured first: `benchmarks/geometry-overlay` found a 6,408-vertex
    /// input costing 153 seconds and 16.7 GB with no way to interrupt it, so the library is confined to
    /// a process that can be killed (ADR-022 §9). Confinement is the property, whether the risk is a
    /// native parser or an uninterruptible algorithm.
    /// </para>
    /// </remarks>
    private static readonly (string Prefix, string Project, string Why)[] Confined =
    [
        ("MaxRev.Gdal",
         "Graticula.Import.Reader",
         "GDAL parses a file somebody else chose. ADR-009 §2.2's own words for keeping it out of the "
         + "serving process are that it 'removes an untrusted-file parser from the process that serves "
         + "public requests' — with the package in the solution, a child process is what keeps that "
         + "true. ADR-037 §5a, D-88."),

        ("NetTopologySuite",
         "Graticula.Overlay.Worker",
         "An adversarial overlay cost 153 seconds and 16.7 GB and OverlayNG cannot be interrupted, so "
         + "the library lives where the cost can be killed. ADR-022 §9, Q-97."),
    ];

    /// <summary>
    /// Tier 2 libraries confined to one project but reachable from the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second list, because <see cref="Confined"/> asserts two things and only one of them
    /// applies here.</b> GDAL and NetTopologySuite are confined to one project <em>and</em> kept
    /// out of the serving process, for reasons that are about the library rather than about tiers:
    /// one parses untrusted files, the other cannot be interrupted. A rasteriser is neither, and it
    /// has to be reachable from the host or the host cannot draw a map.
    /// </para>
    /// <para>
    /// <b>What survives is the tier rule</b>
    /// ([build-vs-adopt-policy.md](../../docs/build-vs-adopt-policy.md) §4): a Tier 2 library lives
    /// behind our own port, in an adapter project, and its types appear nowhere else. The peer
    /// implementation read for [ADR-041](../../docs/adr/ADR-041-the-map-renderer.md) §4 chose the
    /// same library and let it into twenty files including their protocol handlers, which is
    /// exactly what this forbids and the reason it is written down rather than assumed.
    /// </para>
    /// </remarks>
    private static readonly (string Prefix, string Project, string Why)[] Ported =
    [
        ("SkiaSharp",
         "Graticula.Render.Skia",
         "The rasteriser is Tier 2 behind IMapCanvas. ADR-041 §5.1: no Skia type crosses into "
         + "Tier 1, and the port is what makes that possible. A second project naming the library "
         + "is the port stopping being a port."),

        ("BitMiracle.LibTiff",
         "Graticula.Raster.Tiff",
         "The raster reader is Tier 2 behind ICoverageReader. ADR-043 §3.5 draws the line at the "
         + "same place ADR-041 drew it for the canvas: the library hands us numbers and we decide "
         + "what colour they are, so no TIFF type — directory, strip, photometric interpretation — "
         + "may appear in a Tier 1 signature. Its condition 4 asks for this test on the same day "
         + "as the adapter, because a port with one implementation and no test is a port by "
         + "intention only."),
    ];

    [Fact]
    public void A_ported_library_is_referenced_by_its_adapter_and_no_other()
    {
        DirectoryInfo root = FindRepositoryRoot();

        List<string> offenders = [];
        List<string> missing = [];

        foreach ((string prefix, string project, string why) in Ported)
        {
            bool present = false;

            foreach (string file in Directory.EnumerateFiles(
                Path.Combine(root.FullName, "src"), "*.csproj", SearchOption.AllDirectories))
            {
                string owner = Path.GetFileNameWithoutExtension(file);

                bool references = XDocument.Load(file)
                    .Descendants("PackageReference")
                    .Select(static element => element.Attribute("Include")?.Value ?? string.Empty)
                    .Any(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                if (!references)
                {
                    continue;
                }

                if (string.Equals(owner, project, StringComparison.Ordinal))
                {
                    present = true;
                }
                else
                {
                    offenders.Add($"{owner} references {prefix}*, which belongs to {project}. {why}");
                }
            }

            // The other direction. A rule asserting only *nobody else* passes trivially once the
            // project it names has gone or lost the reference, and then it reads as a guard while
            // guarding nothing.
            if (!present)
            {
                missing.Add(
                    $"{project} does not reference {prefix}*, so this rule is asserting nothing. "
                    + "Either the adapter moved — in which case this list moves with it — or the "
                    + "library is gone, in which case delete the row.");
            }
        }

        Assert.True(
            offenders.Count == 0 && missing.Count == 0,
            $"""
             A Tier 2 library is not confined to its adapter:

                 {string.Join(Environment.NewLine + "    ", offenders.Concat(missing))}

             build-vs-adopt-policy.md §4 permits a Tier 2 library behind our own port interface.
             The port is worth nothing if a second project can reach past it.
             """);
    }

    [Fact]
    public void A_confined_dependency_is_referenced_by_its_one_project_and_no_other()
    {
        DirectoryInfo root = FindRepositoryRoot();

        List<string> offenders = [];
        List<string> missing = [];

        foreach ((string prefix, string project, string why) in Confined)
        {
            List<string> found = [];

            // Every project in the repository, not a list of the ones we remember. A rule that
            // enumerates the projects it guards stops guarding the next one somebody adds.
            foreach (string file in Directory.EnumerateFiles(
                Path.Combine(root.FullName, "src"), "*.csproj", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(
                    Path.Combine(root.FullName, "tests"), "*.csproj", SearchOption.AllDirectories)))
            {
                string owner = Path.GetFileNameWithoutExtension(file);

                bool references = XDocument.Load(file)
                    .Descendants("PackageReference")
                    .Select(static element => element.Attribute("Include")?.Value ?? string.Empty)
                    .Any(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                if (!references)
                {
                    continue;
                }

                found.Add(owner);

                if (!string.Equals(owner, project, StringComparison.Ordinal))
                {
                    offenders.Add($"{owner} references {prefix}*, which belongs to {project}. {why}");
                }
            }

            // <b>The other direction, and it is the half that catches a rename.</b> A rule asserting
            // only *nobody else* passes trivially once the project it names has gone or lost the
            // reference — and then it reads as a guard while guarding nothing.
            if (!found.Contains(project, StringComparer.Ordinal))
            {
                missing.Add(
                    $"{project} does not reference {prefix}*, so this rule is asserting nothing. "
                    + "Either the dependency moved — in which case this list moves with it and the "
                    + "reason is rewritten — or it is gone, in which case delete the row.");
            }
        }

        Assert.True(
            offenders.Count == 0 && missing.Count == 0,
            $"""
             A confined dependency is not where it is supposed to be:

                 {string.Join("\n    ", offenders.Concat(missing))}

             build-vs-adopt-policy.md §4 permits a Tier 2 library behind our own port. These two are
             confined further than that — to one process each — because one parses untrusted files and
             the other cannot be interrupted. Confinement that is not checked is a convention, and
             D-46, D-71, D-74 and D-83 are all conventions that held until they quietly did not.
             """);
    }

    /// <summary>
    /// The serving project loads neither, directly or through a project reference.
    /// </summary>
    /// <remarks>
    /// <b>The stronger half, because the test above only reads project files.</b> A package can reach
    /// the server through a chain of project references without ever appearing in its own
    /// <c>.csproj</c> — which is exactly how a confined library escapes: not by somebody adding the
    /// reference, but by somebody referencing the project that has it.
    /// </remarks>
    [Fact]
    public void The_host_cannot_reach_a_confined_dependency_through_a_project_reference()
    {
        DirectoryInfo root = FindRepositoryRoot();

        string host = Path.Combine(
            root.FullName, "src", "Graticula.Host", "Graticula.Host.csproj");

        Assert.True(File.Exists(host), $"The host project is not at {host}. Has it moved?");

        HashSet<string> reachable = new(StringComparer.OrdinalIgnoreCase);

        Walk(host, reachable);

        List<string> offenders = [];

        foreach (string project in reachable)
        {
            if (!File.Exists(project))
            {
                continue;
            }

            foreach (string package in XDocument.Load(project)
                .Descendants("PackageReference")
                .Select(static element => element.Attribute("Include")?.Value ?? string.Empty))
            {
                foreach ((string prefix, _, string why) in Confined)
                {
                    if (package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add(
                            $"{Path.GetFileNameWithoutExtension(project)} is reachable from the host "
                            + $"and references {package}. {why}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"""
             The serving process can load a confined dependency:

                 {string.Join("\n    ", offenders)}

             These live in child processes so that the process answering public requests cannot load
             them. A project reference is how that stops being true without anybody deciding it.
             """);
    }

    private static void Walk(string project, HashSet<string> seen)
    {
        if (!seen.Add(project) || !File.Exists(project))
        {
            return;
        }

        string directory = Path.GetDirectoryName(project)!;

        foreach (XElement reference in XDocument.Load(project).Descendants("ProjectReference"))
        {
            string relative = reference.Attribute("Include")?.Value ?? string.Empty;

            if (relative.Length == 0)
            {
                continue;
            }

            // <b>`ReferenceOutputAssembly="false"` is the mechanism, not a detail</b> — and the first
            // version of this test did not honour it, so it failed on the pattern it exists to protect.
            // `Graticula.Host` references `Graticula.Overlay.Worker` that way, with `Private="false"`
            // beside it: the host **builds** the worker so it is there to spawn, and does not reference
            // its assembly or copy its output. Verified while fixing this — there is no
            // NetTopologySuite.dll in the host's output directory.
            //
            // So the walk follows only references that put an assembly within the host's reach. A
            // `ProjectReference` to a confined project *without* this attribute is the escape this test
            // looks for, and it is now the only thing it reports.
            if (string.Equals(
                reference.Attribute("ReferenceOutputAssembly")?.Value,
                "false",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Walk(Path.GetFullPath(Path.Combine(directory, relative.Replace('\\', '/'))), seen);
        }
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!;
    }
}
