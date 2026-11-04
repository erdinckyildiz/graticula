using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
using Graticula.Cartography.Printing;
using Graticula.Catalog;
using Graticula.Coverages;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// The print service — ADR-265: ArcGIS's <c>Utilities/PrintingTools</c> GPServer, whose <c>Export Web Map Task</c> draws a
/// <c>Web_Map_as_JSON</c> on the server and answers the address of the file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the public REST reference</b> — the ExportWebMap specification, the <c>Export Web Map</c> and
/// <c>Get Layout Templates Info</c> tasks, and the GP service, task, execute and job resources (docs/research/gp-research.md).
/// The shapes are the documented ones; the drawing and the page are this server's own.
/// </para>
/// <para>
/// <b>It fetches nothing.</b> A layer is drawn only when its address is one of this server's own services, resolved from the
/// catalogue and drawn through the same renderer its MapServer export uses (<see cref="WmsEndpoints.DrawLayerAsync"/>, and
/// <see cref="ImageServerEndpoints.DrawIntoMapAsync"/> for an image service), as the caller who asked — never as the server,
/// and never with a token the web map carries. A layer at any other address is left out and the result says so (§5.3,
/// INFERRED): the conservative reading of "a layer at another origin is SSRF" is to make no request at all.
/// </para>
/// </remarks>
internal static class PrintEndpoints
{
    /// <summary>Where the service is, as ArcGIS Enterprise's portal names it in <c>helperServices.printTask</c>.</summary>
    public const string ServicePath = "/rest/services/Utilities/PrintingTools/GPServer";

    /// <summary>The task that prints.</summary>
    public const string ExportTask = "Export Web Map Task";

    /// <summary>The task that lists the layouts.</summary>
    public const string TemplatesTask = "Get Layout Templates Info Task";

    /// <summary>How many prints run at once on this server; the rest wait <see cref="ConnectionBudget.Default"/>, then are refused.</summary>
    public const int Concurrency = 2;

    /// <summary>The most pixels one print draws: an A3 page at 300 dpi, and a little over.</summary>
    public const long MaximumPixels = 20_000_000;

    /// <summary>The longest one print may take.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    /// <summary>The most finished jobs remembered for <c>submitJob</c>'s client to read.</summary>
    public const int MaximumJobs = 1024;

    /// <summary>The formats written, as the task's choice list names them.</summary>
    internal static readonly string[] Formats = ["PDF", "PNG32", "PNG8", "JPG"];

    private static readonly SemaphoreSlim Running = new(Concurrency, Concurrency);

    private sealed record PrintJob(string Status, string? Url, IReadOnlyList<object> Messages, DateTimeOffset Until);

    private static readonly ConcurrentDictionary<string, PrintJob> Jobs = new(StringComparer.Ordinal);

    /// <summary>Maps the routes.</summary>
    public static void Map(WebApplication app)
    {
        // ADR-018 condition 5: the service and its tasks describe themselves and hold no data; a print draws only what
        // each layer's own service shares with the caller (§5.3); a job is read by its id, a capability like the output
        // directory's file name, and holds only that address.
        app.MapMethods(ServicePath, ["GET", "POST"], ServiceAsync).Governed(SharingGovernedExtensions.Public);
        app.MapMethods(ServicePath + "/{task}", ["GET", "POST"], TaskAsync).Governed(SharingGovernedExtensions.Public);
        app.MapMethods(ServicePath + "/{task}/execute", ["GET", "POST"], ExecuteAsync)
            .Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        app.MapMethods(ServicePath + "/{task}/submitJob", ["GET", "POST"], SubmitAsync)
            .Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        app.MapMethods(ServicePath + "/{task}/jobs/{jobId}", ["GET", "POST"], JobAsync)
            .Governed(SharingGovernedExtensions.Public);
        app.MapMethods(ServicePath + "/{task}/jobs/{jobId}/results/{param}", ["GET", "POST"], ResultAsync)
            .Governed(SharingGovernedExtensions.Public);
    }

    /// <summary>The address <c>portals/self</c> names in <c>helperServices.printTask.url</c>.</summary>
    /// <param name="origin">The organisation's origin.</param>
    /// <returns>The task's address.</returns>
    public static string TaskUrl(string origin) => $"{origin}{ServicePath}/{Uri.EscapeDataString(ExportTask)}";

    // ------------------------------------------------------------------ the service, as it describes itself

    private static Task ServiceAsync(HttpContext context) =>
        AnalysisEndpoints.WriteAsync(context, new
        {
            currentVersion = 11.3,
            serviceDescription = "Prints a web map on this server: Web_Map_as_JSON drawn as a PDF or an image, on a page "
                + "with a title, legend, scale bar and north arrow or as the map alone (ADR-265).",
            tasks = new[] { ExportTask, TemplatesTask },
            executionType = "esriExecutionTypeSynchronous",
            resultMapServerName = string.Empty,
            maximumRecords = 1000,
            capabilities = string.Empty,
        });

    private static async Task TaskAsync(HttpContext context, string task)
    {
        if (Named(task) is not { } name)
        {
            await AnalysisEndpoints.RefuseAsync(context, 404, NoSuchTask(task)).ConfigureAwait(false);
            return;
        }

        object[] parameters = name == ExportTask
            ?
            [
                Parameter("Web_Map_as_JSON", "GPString", "Web Map as JSON",
                    "The map to print, as the ExportWebMap specification describes it.", output: false, required: true),
                Parameter("Output_File", "GPDataFile", "Output File", "The printed file.", output: true, required: false),
                Parameter("Format", "GPString", "Format", "The file's format.", output: false, required: false,
                    "PDF", Formats),
                Parameter("Layout_Template", "GPString", "Layout Template",
                    "The page the map is printed on, or MAP_ONLY for the map alone at exportOptions.outputSize.",
                    output: false, required: false, PageTemplate.MapOnlyName, [.. PageTemplate.All.Select(t => t.Name)]),
            ]
            :
            [
                Parameter("Output_JSON", "GPString", "Output JSON", "Each layout's page size, map frame and elements.",
                    output: true, required: false),
            ];

        await AnalysisEndpoints.WriteAsync(context, new
        {
            name,
            displayName = name,
            description = name == ExportTask
                ? "Prints a web map as a PDF, PNG or JPEG file (ADR-265)."
                : "Lists the layouts the print task composes.",
            category = string.Empty,
            helpUrl = string.Empty,
            executionType = "esriExecutionTypeSynchronous",
            parameters,
        }).ConfigureAwait(false);
    }

    private static object Parameter(
        string name, string type, string display, string description, bool output, bool required,
        object? defaultValue = null, string[]? choices = null) =>
        new
        {
            name,
            dataType = type,
            displayName = display,
            description,
            direction = output ? "esriGPParameterDirectionOutput" : "esriGPParameterDirectionInput",
            defaultValue,
            parameterType = output ? "esriGPParameterTypeDerived"
                : required ? "esriGPParameterTypeRequired" : "esriGPParameterTypeOptional",
            category = string.Empty,
            choiceList = choices,
        };

    private static string? Named(string task) =>
        string.Equals(task, ExportTask, StringComparison.OrdinalIgnoreCase) ? ExportTask
        : string.Equals(task, TemplatesTask, StringComparison.OrdinalIgnoreCase) ? TemplatesTask
        : null;

    private static string NoSuchTask(string task) =>
        $"There is no task '{task}' in the print service. Its tasks are '{ExportTask}' and '{TemplatesTask}'.";

    // ------------------------------------------------------------------ running

    /// <summary>What one run answered: the file's address, or the sentence that refused it, and its messages.</summary>
    private sealed record Outcome(string? Url, string? Refusal, List<object> Messages, object? Templates = null);

    private static async Task ExecuteAsync(
        HttpContext context, string task, CatalogFallback catalog, ServiceContexts contexts, IMapCanvasFactory canvases,
        HostSettings settings, IProjector projector, ICoverageCatalog coverages, ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        if (Named(task) is not { } name)
        {
            await AnalysisEndpoints.RefuseAsync(context, 404, NoSuchTask(task)).ConfigureAwait(false);
            return;
        }

        Outcome outcome = await RunAsync(
            context, name, catalog, contexts, canvases, settings, projector, coverages, readers, cancellation).ConfigureAwait(false);

        if (outcome.Refusal is { } refused)
        {
            await AnalysisEndpoints.RefuseAsync(context, 400, refused).ConfigureAwait(false);
            return;
        }

        await AnalysisEndpoints.WriteAsync(context, new
        {
            results = new[] { Result(name, outcome) },
            messages = outcome.Messages,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>submitJob</c>: the same print, drawn before this answers, kept as a finished job — ADR-265 §5.7.
    /// </summary>
    /// <remarks>
    /// <b>A synchronous service that also takes jobs</b>, because a client configured for an asynchronous print service
    /// submits and polls whatever the service says. The print is drawn under the same bounds as <c>execute</c>, so a job
    /// is never a way around them; the job only remembers the answer, for as long as the file is kept.
    /// </remarks>
    private static async Task SubmitAsync(
        HttpContext context, string task, CatalogFallback catalog, ServiceContexts contexts, IMapCanvasFactory canvases,
        HostSettings settings, IProjector projector, ICoverageCatalog coverages, ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        if (Named(task) is not { } name)
        {
            await AnalysisEndpoints.RefuseAsync(context, 404, NoSuchTask(task)).ConfigureAwait(false);
            return;
        }

        Outcome outcome = await RunAsync(
            context, name, catalog, contexts, canvases, settings, projector, coverages, readers, cancellation).ConfigureAwait(false);

        List<object> messages = outcome.Messages;

        if (outcome.Refusal is { } refused)
        {
            messages.Insert(0, new { type = "esriJobMessageTypeError", description = refused });
        }

        string id = "j" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Keep(id, new PrintJob(
            outcome.Refusal is null ? "esriJobSucceeded" : "esriJobFailed",
            outcome.Url ?? (outcome.Templates is null ? null : System.Text.Json.JsonSerializer.Serialize(outcome.Templates)),
            messages,
            DateTimeOffset.UtcNow + ExportOutputs.Lifetime));

        await AnalysisEndpoints.WriteAsync(context, new
        {
            jobId = id,
            jobStatus = outcome.Refusal is null ? "esriJobSucceeded" : "esriJobFailed",
        }).ConfigureAwait(false);
    }

    private static async Task JobAsync(HttpContext context, string task, string jobId)
    {
        if (Named(task) is not { } name || Find(jobId) is not { } job)
        {
            await AnalysisEndpoints.RefuseAsync(context, 400, "Job not found. A print job is kept for ten minutes.").ConfigureAwait(false);
            return;
        }

        string result = name == ExportTask ? "Output_File" : "Output_JSON";

        await AnalysisEndpoints.WriteAsync(context, new
        {
            jobId,
            jobStatus = job.Status,
            results = job.Status == "esriJobSucceeded"
                ? new Dictionary<string, object> { [result] = new { paramUrl = $"results/{result}" } }
                : [],
            inputs = new Dictionary<string, object>(),
            messages = job.Messages,
        }).ConfigureAwait(false);
    }

    private static async Task ResultAsync(HttpContext context, string task, string jobId, string param)
    {
        string? name = Named(task);
        string expected = name == ExportTask ? "Output_File" : "Output_JSON";

        if (name is null || Find(jobId) is not { Status: "esriJobSucceeded" } job
            || !string.Equals(param, expected, StringComparison.OrdinalIgnoreCase))
        {
            await AnalysisEndpoints.RefuseAsync(context, 400, "No such result. A print job's one result is "
                + $"'{expected}', kept for ten minutes.").ConfigureAwait(false);
            return;
        }

        await AnalysisEndpoints.WriteAsync(context, name == ExportTask
            ? new { paramName = expected, dataType = "GPDataFile", value = (object)new { url = job.Url } }
            : new { paramName = expected, dataType = "GPString", value = (object)System.Text.Json.JsonDocument.Parse(job.Url!).RootElement.Clone() })
            .ConfigureAwait(false);
    }

    private static object Result(string task, Outcome outcome) =>
        task == ExportTask
            ? new { paramName = "Output_File", dataType = "GPDataFile", value = (object)new { url = outcome.Url } }
            : new { paramName = "Output_JSON", dataType = "GPString", value = outcome.Templates! };

    /// <summary>The handoff kind a job is kept under on a site (ADR-012 phase 2), as the output directory keeps its files.</summary>
    private const string Kind = "printjobs";

    private static PrintJob? Find(string jobId) =>
        (Jobs.TryGetValue(jobId, out PrintJob? job) ? job : SiteHandoff.Load<PrintJob>(Kind, jobId)) is { } found
        && found.Until > DateTimeOffset.UtcNow
            ? found
            : null;

    /// <summary>Keeps a finished job, after forgetting the expired ones and, past the bound, the oldest.</summary>
    private static void Keep(string id, PrintJob job)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SiteHandoff.Sweep(Kind, ExportOutputs.Lifetime);

        foreach ((string key, PrintJob kept) in Jobs)
        {
            if (kept.Until <= now)
            {
                Jobs.TryRemove(key, out _);
            }
        }

        if (Jobs.Count >= MaximumJobs)
        {
            foreach ((string key, _) in Jobs.OrderBy(j => j.Value.Until).Take(Jobs.Count - MaximumJobs + 1).ToList())
            {
                Jobs.TryRemove(key, out _);
            }
        }

        Jobs[id] = job;

        // The client polls next, and a balancer may send the poll to another node — as ExportOutputs hands its files on.
        SiteHandoff.Save(Kind, id, job);
    }

    private static async Task<Outcome> RunAsync(
        HttpContext context, string task, CatalogFallback catalog, ServiceContexts contexts, IMapCanvasFactory canvases,
        HostSettings settings, IProjector projector, ICoverageCatalog coverages, ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        if (task == TemplatesTask)
        {
            return new Outcome(null, null, [], TemplatesInfo());
        }

        Func<string, string?> asked = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        string format = (asked("Format") is { Length: > 0 } f ? f.Trim() : "PDF").ToUpperInvariant();

        if (format is "PNG" or "PNG24")
        {
            format = "PNG32";
        }
        else if (format == "JPEG")
        {
            format = "JPG";
        }

        if (!Formats.Contains(format))
        {
            return new Outcome(null,
                $"Format '{asked("Format")}' is not one this print service writes. It writes {string.Join(", ", Formats)}; "
                + "GIF, EPS, SVG and SVGZ are not written (ADR-265 §6).", []);
        }

        if (PageTemplate.Named(asked("Layout_Template")) is not { } template)
        {
            return new Outcome(null,
                $"Layout_Template '{asked("Layout_Template")}' is not a layout of this print service. Its layouts are "
                + string.Join(", ", PageTemplate.All.Select(t => t.Name)) + ".", []);
        }

        if (!WebMapPrintRequest.TryParse(asked("Web_Map_as_JSON"), out WebMapPrintRequest? print, out string? unreadable))
        {
            return new Outcome(null, unreadable, []);
        }

        RequestDeadline.LowerTo(context, Deadline);

        // <b>Admitted before anything is allocated</b>, as ImageServer's export is: the page canvas is the largest single
        // cost of a print, and a print refused after drawing it would have paid for the refusal. The refusal is the one
        // every admission bound here gives — 503 and Retry-After, through ConnectionBudgetFullException (ADR-046).
        if (!await Running.WaitAsync(ConnectionBudget.Default, cancellation).ConfigureAwait(false))
        {
            throw new ConnectionBudgetFullException(
                $"This server already draws {Concurrency} prints at once, which is its bound (ADR-265 §5.8). Retry shortly.");
        }

        try
        {
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            bounded.CancelAfter(Deadline);

            try
            {
                return await DrawAsync(
                    context, print!, format, template, catalog, contexts, canvases, settings, projector, coverages, readers,
                    bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                return new Outcome(null,
                    $"The print took longer than {Deadline.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds, "
                    + "which is this server's bound on one print. Print a smaller area, fewer layers or a lower dpi.", []);
            }
        }
        finally
        {
            Running.Release();
        }
    }

    /// <summary>One thing to draw: a vector layer with how it is drawn, or an image service.</summary>
    private sealed record Drawable(
        PublishedLayer? Layer, PublishedCoverage? Coverage, AttributePredicate? Definition, string? Symbology);

    private static async Task<Outcome> DrawAsync(
        HttpContext context, WebMapPrintRequest print, string format, PageTemplate template, CatalogFallback catalog,
        ServiceContexts contexts, IMapCanvasFactory canvases, HostSettings settings, IProjector projector,
        ICoverageCatalog coverages, ICoverageReaderFactory readers, CancellationToken cancellation)
    {
        List<object> messages = [];
        int srid = print.Srid;

        if (!await projector.KnowsAsync(srid, cancellation).ConfigureAwait(false))
        {
            return new Outcome(null,
                $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)} is not a reference this server's projection database has.", []);
        }

        Envelope extent = print.Extent;

        if (srid != print.ExtentSrid)
        {
            if (!await projector.KnowsAsync(print.ExtentSrid, cancellation).ConfigureAwait(false)
                || await ImageServerEndpoints.ProjectEnvelopeAsync(extent, print.ExtentSrid, srid, projector, cancellation)
                    .ConfigureAwait(false) is not { } carried)
            {
                return new Outcome(null,
                    $"mapOptions.extent could not be carried from EPSG:{print.ExtentSrid.ToString(CultureInfo.InvariantCulture)} "
                    + $"into the map's EPSG:{srid.ToString(CultureInfo.InvariantCulture)}.", []);
            }

            extent = carried;
        }

        // ---- the page, or the map alone
        double ratio = print.Dpi / PageComposer.ScreenDpi;
        int pageWidth, pageHeight, logicalWidth, logicalHeight;
        PageLayout? layout = null;

        // The legend's entries are resolved with the layers, so whether the page leaves room for one is known first.
        List<(WebMapPrintLayer Asked, List<Drawable> Drawn)> resolved = [];

        foreach (WebMapPrintLayer layer in print.Layers)
        {
            if (!layer.Visible)
            {
                continue;
            }

            (List<Drawable> drawn, string? left) = await ResolveAsync(
                context, layer, catalog, contexts, coverages, cancellation).ConfigureAwait(false);

            if (left is not null)
            {
                messages.Add(Warning(left));
            }

            if (drawn.Count > 0)
            {
                resolved.Add((layer, drawn));
            }
        }

        List<LegendGroup> legend = template.IsMapOnly ? [] : Legend(print, resolved);

        if (template.IsMapOnly)
        {
            pageWidth = print.OutputWidth;
            pageHeight = print.OutputHeight;

            if (pageWidth > settings.MaximumImageWidth || pageHeight > settings.MaximumImageHeight)
            {
                return new Outcome(null,
                    $"exportOptions.outputSize is {pageWidth}x{pageHeight}; this server draws at most "
                    + $"{settings.MaximumImageWidth}x{settings.MaximumImageHeight} pixels.", []);
            }

            logicalWidth = Math.Max(1, (int)Math.Round(pageWidth / ratio));
            logicalHeight = Math.Max(1, (int)Math.Round(pageHeight / ratio));
        }
        else
        {
            layout = PageComposer.Layout(template, print.Dpi, legend: legend.Count > 0);
            pageWidth = layout.Width;
            pageHeight = layout.Height;
            logicalWidth = layout.LogicalMapWidth;
            logicalHeight = layout.LogicalMapHeight;
        }

        if ((long)pageWidth * pageHeight > MaximumPixels)
        {
            return new Outcome(null,
                $"The page would be {pageWidth}x{pageHeight} pixels at {print.Dpi.ToString(CultureInfo.InvariantCulture)} dpi; "
                + $"this server draws at most {MaximumPixels.ToString("N0", CultureInfo.InvariantCulture)} pixels in one "
                + "print. Ask for a lower dpi or a smaller page.", []);
        }

        bool geographic = AxisOrder.IsGeographic(srid);
        Envelope framed = Frame(extent, print.Scale, logicalWidth, logicalHeight, geographic);

        if (Math.Abs(print.Rotation) > 1e-9)
        {
            messages.Add(Warning(
                $"mapOptions.rotation ({print.Rotation.ToString(CultureInfo.InvariantCulture)}°) was not applied: this server "
                + "prints with north up (ADR-265 §6)."));
        }

        // ---- drawing
        using IMapCanvas page = canvases.Create(pageWidth, pageHeight);
        page.Clear(Rgba.White);

        PlacedCanvas window = layout is null
            ? new PlacedCanvas(page, 0, 0, logicalWidth, logicalHeight, ratio)
            : PageComposer.MapWindow(page, layout);

        PixelTransform transform = new(framed, logicalWidth, logicalHeight);
        MapRenderer renderer = new(window, transform, geographic);

        foreach ((WebMapPrintLayer asked, List<Drawable> drawn) in resolved)
        {
            foreach (Drawable one in drawn)
            {
                if (one.Coverage is { } coverage)
                {
                    string? refused = await ImageServerEndpoints.DrawIntoMapAsync(
                            context, window, coverage, framed, logicalWidth, logicalHeight, srid, readers, projector, cancellation)
                        .ConfigureAwait(false);

                    if (refused is not null)
                    {
                        messages.Add(Warning($"'{Title(asked)}' was not drawn: {refused}"));
                    }

                    continue;
                }

                try
                {
                    await WmsEndpoints.DrawLayerAsync(
                            contexts, renderer, transform, one.Layer!, srid, null, settings.MaximumRecordCount, cancellation,
                            symbology: one.Symbology, honourVisibleRange: true, definition: one.Definition, whole: true)
                        .ConfigureAwait(false);
                }
                catch (RenderException e)
                {
                    messages.Add(Warning($"'{Title(asked)}' was not drawn: {e.Message}"));
                }
            }
        }

        renderer.FinishLabels();

        if (layout is not null)
        {
            PageComposer.Mask(page, layout);

            double? metres = await MetresPerPagePixelAsync(framed, srid, transform, ratio, projector, cancellation)
                .ConfigureAwait(false);

            foreach (string note in PageComposer.DrawFurniture(page, layout, new PageFurniture(
                         print.Title, print.Author, print.Copyright, metres, print.Units, legend)))
            {
                messages.Add(Warning(note));
            }
        }

        // ---- the file
        byte[] file;
        string type;

        switch (format)
        {
            case "PDF":
                byte[] jpeg = page.Encode(MapImageFormat.Jpeg, 92);
                file = PdfPage.Wrap(
                    jpeg, pageWidth, pageHeight,
                    layout is null ? pageWidth / print.Dpi : template.WidthInches,
                    layout is null ? pageHeight / print.Dpi : template.HeightInches,
                    print.Title);
                type = "application/pdf";
                break;
            case "JPG":
                file = page.Encode(MapImageFormat.Jpeg, settings.JpegQuality);
                type = "image/jpeg";
                break;
            default:
                // PNG8 is written as a PNG of full colour, as MapServer's export answers png8 (ADR-265 §5.6).
                file = page.Encode(MapImageFormat.Png, 90);
                type = "image/png";
                break;
        }

        return new Outcome(ExportOutputs.Keep(context, file, type), null, messages);
    }

    /// <summary>
    /// The extent drawn: the one asked for, widened to the frame's shape about its centre — or, with
    /// <c>mapOptions.scale</c>, the frame at that scale about the extent's centre, as the specification says.
    /// </summary>
    internal static Envelope Frame(Envelope extent, double? scale, int width, int height, bool geographic)
    {
        double cx = (extent.MinX + extent.MaxX) / 2;
        double cy = (extent.MinY + extent.MaxY) / 2;

        // A scale is read as the web map writes it: ground units per screen inch at 96 dpi, a degree taken at the equator.
        double unitsPerPixel = scale is { } s
            ? s * 0.0254 / PageComposer.ScreenDpi / (geographic ? MapScale.MetresPerDegree : 1)
            : Math.Max(extent.Width / width, extent.Height / height);

        double halfWidth = unitsPerPixel * width / 2;
        double halfHeight = unitsPerPixel * height / 2;

        return new Envelope(cx - halfWidth, cy - halfHeight, cx + halfWidth, cy + halfHeight);
    }

    /// <summary>
    /// The ground one page pixel covers at the frame's centre, measured on the ellipsoid's mean sphere between two points
    /// carried into EPSG:4326 — ADR-133's measure, so a feet-based or a Web Mercator map gets a true scale bar. Null when
    /// either point cannot be carried.
    /// </summary>
    private static async Task<double?> MetresPerPagePixelAsync(
        Envelope framed, int srid, PixelTransform transform, double ratio, IProjector projector, CancellationToken cancellation)
    {
        double cx = (framed.MinX + framed.MaxX) / 2;
        double cy = (framed.MinY + framed.MaxY) / 2;
        double step = transform.UnitsPerPixel * 50;

        try
        {
            Geometry[] points = [new Point(cx - (step / 2), cy), new Point(cx + (step / 2), cy)];
            IReadOnlyList<Geometry> carried = srid == 4326
                ? points
                : (await projector.ProjectAsync(points, srid, 4326, cancellation).ConfigureAwait(false)).Projected;

            if (carried.Count != 2 || carried[0] is not Point a || carried[1] is not Point b)
            {
                return null;
            }

            // EPSG:4326 in this server's geometries is x = longitude, y = latitude.
            double metres = Haversine(a.Y, a.X, b.Y, b.X);
            return metres > 0 && double.IsFinite(metres) ? metres / (50 * ratio) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_008.8;
        double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
        double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
        double h = (Math.Sin(dp / 2) * Math.Sin(dp / 2)) + (Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2));
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static object Warning(string text) => new { type = "esriJobMessageTypeWarning", description = text };

    private static string Title(WebMapPrintLayer layer) => layer.Title ?? layer.Id ?? layer.Url ?? "(a layer)";

    // ------------------------------------------------------------------ which layers are this server's, and readable

    /// <summary>The services this server answers, by the name a URL gives their face.</summary>
    private static readonly string[] Faces = ["MapServer", "FeatureServer", "VectorTileServer", "ImageServer"];

    /// <summary>
    /// What one web-map layer draws on this server, and the sentence that says why it draws less, or nothing.
    /// </summary>
    private static async Task<(List<Drawable> Drawn, string? Left)> ResolveAsync(
        HttpContext context, WebMapPrintLayer asked, CatalogFallback catalog, ServiceContexts contexts,
        ICoverageCatalog coverages, CancellationToken cancellation)
    {
        string title = Title(asked);

        if (asked.Url is not { } url)
        {
            return ([], $"'{title}' was not drawn: it names no service. A feature collection or graphics in the web map are "
                + "not printed by this server (ADR-265 §6).");
        }

        if (OwnServicePath(context, url) is not { } segments)
        {
            return ([], $"'{title}' was not drawn: its address is not one of this server's services, and this print service "
                + "makes no request to another address (ADR-265 §5.3).");
        }

        // Absent and unshared are one sentence, so the answer says nothing about what exists.
        string unreadable = $"'{title}' was not drawn: there is no service at its address that you may read.";

        int face = Array.FindIndex(segments, s => Faces.Contains(s, StringComparer.Ordinal));

        if (face is < 1 or > 2)
        {
            return ([], unreadable);
        }

        string? folder = face == 2 ? segments[0] : null;
        string name = segments[face - 1];
        string kind = segments[face];
        int? index = segments.Length > face + 1
            && int.TryParse(segments[face + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : null;

        if (kind == "ImageServer")
        {
            PublishedCoverage? coverage = await coverages.FindAsync(folder, name, cancellation).ConfigureAwait(false);
            RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

            return coverage is not null && coverage.Status == ServiceStatus.Started
                && LayerAccess.Evaluate(coverage.Sharing, coverage.Owner, current.Principal, current.Authorization, coverage.SharedWith)
                    .IsAllowed()
                ? ([new Drawable(null, coverage, null, null)], null)
                : ([], unreadable);
        }

        if (await ServiceLookup.ReadableQuietlyAsync(context, catalog, folder, name, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return ([], unreadable);
        }

        bool answers = kind switch
        {
            "MapServer" => ServiceFaces.AnswersMap(service),
            "FeatureServer" => ServiceFaces.AnswersFeatures(service),
            _ => ServiceFaces.AnswersTiles(service),
        };

        if (!answers)
        {
            return ([], unreadable);
        }

        List<PublishedLayer> drawable = [.. service.Layers.Where(l => l.Definition.GeometryColumn is { Length: > 0 })];
        List<PublishedLayer> chosen;
        IReadOnlyDictionary<int, string> definitions = asked.Definitions;

        if (index is { } one)
        {
            List<int> beneath = MapServerEndpoints.LayersBeneath(service, one);
            chosen = [.. drawable.Where(l => l.LayerIndex == one || beneath.Contains(l.LayerIndex))];

            if (asked.DefinitionExpression is { } where)
            {
                definitions = chosen.ToDictionary(l => l.LayerIndex, _ => where);
            }
        }
        else if (asked.VisibleLayers is { } visible)
        {
            HashSet<int> shown = [.. visible];

            foreach (int id in visible)
            {
                shown.UnionWith(MapServerEndpoints.LayersBeneath(service, id));
            }

            chosen = [.. drawable.Where(l => shown.Contains(l.LayerIndex))];
        }
        else
        {
            chosen = drawable;
        }

        if (chosen.Count == 0)
        {
            return ([], index is null ? null : unreadable);
        }

        List<string> notes = [];
        List<PublishedLayer> readable = [];

        foreach (PublishedLayer layer in chosen)
        {
            if (CapabilityCeilings.Refuses(layer, "Query"))
            {
                notes.Add($"'{layer.DisplayName}' was not drawn: {CapabilityCeilings.Explain(layer, "Query")}");
                continue;
            }

            readable.Add(layer);
        }

        (Dictionary<Guid, AttributePredicate> predicates, string? badDefinition) = await LayerDefinitions.ParseAsync(
                definitions.Where(d => readable.Any(l => l.LayerIndex == d.Key)).ToDictionary(d => d.Key, d => d.Value),
                readable, contexts, cancellation)
            .ConfigureAwait(false);

        if (badDefinition is not null)
        {
            return ([], $"'{title}' was not drawn: its definition expression could not be read. {badDefinition}");
        }

        List<Drawable> drawn = [];

        foreach (PublishedLayer layer in readable)
        {
            string? symbology = null;

            // The renderer the client drew the layer with, when it sent one — read as WMS reads a pasted one (ADR-171),
            // pictures only from data it carries (MarkerPicture refuses an address).
            if (asked.DrawingInfo is { } info && index is not null)
            {
                try
                {
                    symbology = CimEsri.FromDrawingInfo(info, layer.RequireGeometryType()).Renderer.ToJsonString();
                }
                catch (Exception e) when (e is SymbologyException or InvalidOperationException or FormatException
                    or System.Text.Json.JsonException)
                {
                    notes.Add($"'{title}' was drawn with its service's own symbols: the renderer the web map gave it could "
                        + $"not be read. {e.Message}");
                }
            }

            drawn.Add(new Drawable(layer, null, predicates.GetValueOrDefault(layer.Id), symbology));
        }

        return (drawn, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    /// <summary>
    /// A URL's path below <c>/rest/services</c>, as segments, when it addresses this server — same host and port as this
    /// request, or a path with no host — and null for anywhere else.
    /// </summary>
    internal static string[]? OwnServicePath(HttpContext context, string url)
    {
        string path;

        if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
        {
            path = url;
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out Uri? address)
            && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp)
            && string.Equals(address.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(address.UserInfo))
        {
            path = address.AbsolutePath;
        }
        else
        {
            return null;
        }

        int query = path.IndexOfAny(['?', '#']);
        PathString at = new(Uri.UnescapeDataString(query < 0 ? path : path[..query]));

        if (context.Request.PathBase.HasValue && at.StartsWithSegments(context.Request.PathBase, out PathString below))
        {
            at = below;
        }

        if (ArcGisAddresses.Map(at, publishing: false) is { Outcome: ArcGisAddresses.Outcome.Rewritten } mapped)
        {
            at = mapped.Path;
        }

        if (!at.StartsWithSegments("/rest/services", out PathString rest) || !rest.HasValue)
        {
            return null;
        }

        string[] segments = rest.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s is "." or "..") ? null : segments;
    }

    // ------------------------------------------------------------------ the legend

    private static List<LegendGroup> Legend(
        WebMapPrintRequest print, List<(WebMapPrintLayer Asked, List<Drawable> Drawn)> resolved)
    {
        List<LegendGroup> groups = [];

        // Top of the map first, as a legend reads.
        foreach ((WebMapPrintLayer asked, List<Drawable> drawn) in Enumerable.Reverse(resolved))
        {
            if (asked.IsBasemap)
            {
                continue;
            }

            IReadOnlyList<int>? subLayers = null;

            if (print.LegendLayers is { } shown
                && (asked.Id is null || !shown.TryGetValue(asked.Id, out subLayers)))
            {
                continue;
            }

            foreach (Drawable one in Enumerable.Reverse(drawn))
            {
                if (one.Layer is not { } layer || (subLayers is { Count: > 0 } && !subLayers.Contains(layer.LayerIndex)))
                {
                    continue;
                }

                string? chosen = one.Symbology is { Length: > 0 } ? one.Symbology : layer.Symbology;
                SymbologyPlan plan;

                try
                {
                    plan = chosen is { Length: > 0 } stored
                        ? SymbologyPlan.Compile(stored)
                        : SymbologyPlan.Default(layer.Definition.Name, layer.RequireGeometryType());
                }
                catch (SymbologyException)
                {
                    continue;
                }

                GeometryKind geometry = layer.RequireGeometryType();
                List<LegendRow> rows = [];

                if (plan.LegendClasses() is { } axis)
                {
                    foreach (StyleExpression.ClassCase each in axis.Cases.Take(Graticula.Api.Wms.LegendGraphic.MaximumRows))
                    {
                        object? value = each.Value;
                        rows.Add(new LegendRow(each.Label, (at, side) => Swatch(at, plan, geometry, side, axis.Field, value)));
                    }
                }
                else
                {
                    rows.Add(new LegendRow(string.Empty, (at, side) => Swatch(at, plan, geometry, side, null, null)));
                }

                string heading = drawn.Count == 1 && asked.Title is { Length: > 0 } t ? t : layer.DisplayName;
                groups.Add(new LegendGroup(heading, rows));
            }
        }

        return groups;
    }

    private static void Swatch(IMapCanvasFactory at, SymbologyPlan plan, GeometryKind geometry, int side, string? field, object? value)
    {
        using IMapCanvas drawn = Graticula.Api.Wms.LegendGraphic.DrawClass(
            at, plan, geometry, (side, side), Rgba.Transparent, field, value);
    }

    // ------------------------------------------------------------------ Get Layout Templates Info

    private static List<object> TemplatesInfo()
    {
        List<object> templates = [];

        foreach (PageTemplate template in PageTemplate.All.Where(t => !t.IsMapOnly))
        {
            // The frame in inches, from the layout at an even 100 dpi.
            PageLayout layout = PageComposer.Layout(template, 100, legend: true);
            double[] frame = [Math.Round((layout.Map.MaxX - layout.Map.MinX) / 100, 2), Math.Round((layout.Map.MaxY - layout.Map.MinY) / 100, 2)];

            templates.Add(new
            {
                layoutTemplate = template.Name,
                pageSize = new[] { template.WidthInches, template.HeightInches },
                pageUnits = "INCH",
                activeDataFrameSize = frame,
                webMapFrameSize = frame,
                layoutOptions = new
                {
                    hasTitleText = true,
                    hasAuthorText = true,
                    hasCopyrightText = true,
                    hasLegend = true,
                    customTextElements = Array.Empty<object>(),
                },
            });
        }

        return templates;
    }
}
