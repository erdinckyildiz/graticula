using System;
using System.Collections;
using System.Collections.Generic;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>
/// Draws features onto a canvas, using a compiled style.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Tier 1 half of rendering.</b> It decides what each feature looks like,
/// where every coordinate lands and which labels survive; it never decides how a
/// polygon is filled. Everything it touches is a type this repository owns, which is
/// what lets <see cref="IMapCanvas"/> have more than one implementation and what
/// keeps [build-vs-adopt-policy.md](../../../docs/build-vs-adopt-policy.md) §4's
/// rule checkable rather than aspirational.
/// </para>
/// <para>
/// <b>Buffers are reused across features and across layers.</b> One
/// <see cref="PixelPath"/>, one attribute view, one label placer, for the whole
/// image. [ADR-004](../../../docs/adr/ADR-004-rendering-engine.md) §0's objection to
/// rendering is an allocation measurement, and a renderer that allocated per feature
/// would prove it right on its first map.
/// </para>
/// <para>
/// <b>Labels are collected during the geometry pass and drawn after all of it.</b>
/// A label painted when its own feature is drawn ends up under whatever is drawn
/// next, which on any map with more than one layer is most of the names.
/// </para>
/// </remarks>
public sealed class MapRenderer
{
    private readonly IMapCanvas _canvas;
    private readonly PixelTransform _transform;
    private readonly PixelPath _path = new();
    private readonly LabelPlacer _labels = new();
    private readonly FeatureAttributes _attributes = new();
    private readonly double _zoom;

    /// <summary>Opens a renderer over a canvas.</summary>
    /// <param name="canvas">Where to draw.</param>
    /// <param name="transform">Map units to pixels.</param>
    /// <param name="geographic">Whether the CRS measures in degrees.</param>
    public MapRenderer(IMapCanvas canvas, PixelTransform transform, bool geographic)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        _canvas = canvas;
        _transform = transform;
        _zoom = MapScale.Zoom(transform.UnitsPerPixel, geographic);
    }

    /// <summary>The zoom this map's resolution corresponds to.</summary>
    public double Zoom => _zoom;

    /// <summary>How many features have been drawn.</summary>
    public int Drawn { get; private set; }

    /// <summary>Paints the background.</summary>
    /// <param name="colour">The colour, which may be transparent.</param>
    public void Clear(Rgba colour) => _canvas.Clear(colour);

    /// <summary>
    /// Draws one layer's features.
    /// </summary>
    /// <remarks>
    /// <b>Feature-major, not layer-major, within a layer's style.</b> Each feature is
    /// converted to pixels once and then painted by every style layer that applies to
    /// it. Iterating the style outermost would re-project every coordinate once per
    /// style layer, which on a two-layer style doubles the arithmetic of the whole
    /// map to no effect on the image.
    /// </remarks>
    /// <param name="plan">The compiled style.</param>
    /// <param name="features">The features, already in the image's CRS.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public void Draw(SymbologyPlan plan, IEnumerable<Feature> features)
    {
        ArgumentNullException.ThrowIfNull(features);

        Pass pass = Begin(plan);

        foreach (Feature feature in features)
        {
            pass.Draw(feature);
        }
    }

    /// <summary>
    /// Opens a pass over one layer, so the caller can feed it features as they arrive.
    /// </summary>
    /// <remarks>
    /// <b>Because the features arrive asynchronously and materialising them does not
    /// scale.</b> A provider streams rows; collecting a whole layer's extent into a
    /// list before drawing any of it holds every geometry in the map at once, which
    /// on a dense extent is the largest allocation in the request and is exactly what
    /// [ADR-004](../../../docs/adr/ADR-004-rendering-engine.md) §0 warned about.
    /// Tier 1 stays synchronous and the iteration stays with whoever owns the reader.
    /// </remarks>
    /// <param name="plan">The compiled style.</param>
    /// <returns>The pass.</returns>
    public Pass Begin(SymbologyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<PlanLayer> applicable = [];

        foreach (PlanLayer layer in plan.Layers)
        {
            if (layer.DrawsAt(_zoom))
            {
                applicable.Add(layer);
            }
        }

        return new Pass(this, applicable);
    }

    /// <summary>One layer being drawn, feature by feature.</summary>
    /// <remarks>
    /// The applicable style layers are resolved once when the pass opens rather than
    /// once per feature, which on a style with a zoom range is a comparison per
    /// feature per layer saved for nothing given up.
    /// </remarks>
    public sealed class Pass
    {
        private readonly MapRenderer _renderer;
        private readonly List<PlanLayer> _layers;

        internal Pass(MapRenderer renderer, List<PlanLayer> layers)
        {
            _renderer = renderer;
            _layers = layers;
        }

        /// <summary>Whether this pass would draw anything at all.</summary>
        public bool DrawsNothing => _layers.Count == 0;

        /// <summary>Draws one feature.</summary>
        /// <param name="feature">The feature, already in the image's CRS.</param>
        public void Draw(Feature feature)
        {
            ArgumentNullException.ThrowIfNull(feature);

            if (_layers.Count == 0 || feature.Geometry is not { IsEmpty: false } geometry)
            {
                return;
            }

            _renderer._attributes.Current = feature;

            StyleExpression.Context context = new(_renderer._attributes, _renderer._zoom);

            bool painted = false;

            foreach (PlanLayer layer in _layers)
            {
                painted |= _renderer.Apply(layer, geometry, context);
            }

            if (painted)
            {
                _renderer.Drawn++;
            }
        }
    }

    /// <summary>Draws the labels that fit, and returns how many did.</summary>
    /// <remarks>
    /// Called once, after every layer of the map. Labels from a layer drawn early
    /// compete with labels from a layer drawn late on equal terms except for order,
    /// which is what draw order means on a map.
    /// </remarks>
    /// <returns>How many labels were drawn.</returns>
    public int FinishLabels() => _labels.Draw(_canvas);

    private bool Apply(PlanLayer layer, Geometry geometry, in StyleExpression.Context context)
    {
        if (layer.Resolve(context) is not { } symbol)
        {
            return false;
        }

        switch (symbol)
        {
            case MapSymbol.Area area:
                if (!Rings(geometry))
                {
                    return false;
                }

                _canvas.FillArea(_path, area);
                return true;

            case MapSymbol.Stroke stroke:
                // A line layer over a polygon strokes its rings, which is what a
                // MapLibre style means by putting one there and is how an outline
                // wider than a hairline is expressed at all.
                if (!Lines(geometry) && !Rings(geometry))
                {
                    return false;
                }

                _canvas.StrokeLine(_path, stroke);
                return true;

            case MapSymbol.Marker marker:
                return Markers(geometry, marker);

            case MapSymbol.Label label:
                return Label(layer, geometry, label, context);

            default:
                throw new RenderException(
                    $"A style resolved to {symbol.GetType().Name}, which this renderer has no case "
                    + "for. Every MapSymbol needs one here; a new kind without one draws nothing "
                    + "and reports success.");
        }
    }

    private bool Label(
        PlanLayer layer, Geometry geometry, MapSymbol.Label symbol,
        in StyleExpression.Context context)
    {
        if (layer is not PlanLayer.Text text || text.TextOf(context) is not { } content)
        {
            return false;
        }

        if (GeometryMeasures.LabelPoint(geometry) is not { } anchor)
        {
            return false;
        }

        _labels.Offer(new LabelCandidate(
            content, symbol, _transform.X(anchor.X), _transform.Y(anchor.Y)));

        return true;
    }

    private bool Markers(Geometry geometry, MapSymbol.Marker symbol)
    {
        switch (geometry)
        {
            case Graticula.Geometries.Point point:
                _canvas.DrawMarker(_transform.X(point.X), _transform.Y(point.Y), symbol);
                return true;

            case MultiPoint many:
                foreach (Graticula.Geometries.Point part in many.Parts)
                {
                    _canvas.DrawMarker(_transform.X(part.X), _transform.Y(part.Y), symbol);
                }

                return many.Parts.Count > 0;

            default:
                // A marker style over a polygon draws at its label point, which is
                // what somebody styling a mixed layer means. Silently drawing
                // nothing would look like missing data.
                if (GeometryMeasures.LabelPoint(geometry) is { } anchor)
                {
                    _canvas.DrawMarker(_transform.X(anchor.X), _transform.Y(anchor.Y), symbol);
                    return true;
                }

                return false;
        }
    }

    /// <summary>Fills the path with a geometry's rings, closed.</summary>
    private bool Rings(Geometry geometry)
    {
        _path.Reset();

        switch (geometry)
        {
            case Polygon polygon:
                AddPolygon(polygon);
                break;

            case MultiPolygon many:
                foreach (Polygon part in many.Parts)
                {
                    AddPolygon(part);
                }

                break;

            default:
                return false;
        }

        _path.End();
        return !_path.IsEmpty;
    }

    /// <summary>Fills the path with a geometry's lines, open.</summary>
    private bool Lines(Geometry geometry)
    {
        _path.Reset();

        switch (geometry)
        {
            case LineString line:
                AddFigure(line.Coordinates, closed: false);
                break;

            case MultiLineString many:
                foreach (LineString part in many.Parts)
                {
                    AddFigure(part.Coordinates, closed: false);
                }

                break;

            default:
                return false;
        }

        _path.End();
        return !_path.IsEmpty;
    }

    private void AddPolygon(Polygon polygon)
    {
        AddFigure(polygon.Shell.Coordinates, closed: true);

        foreach (LinearRing hole in polygon.Holes)
        {
            AddFigure(hole.Coordinates, closed: true);
        }
    }

    private void AddFigure(XySequence coordinates, bool closed)
    {
        if (coordinates.Count == 0)
        {
            return;
        }

        _path.Begin(closed);

        for (int i = 0; i < coordinates.Count; i++)
        {
            _path.Add(_transform.X(coordinates.X(i)), _transform.Y(coordinates.Y(i)));
        }
    }

    /// <summary>
    /// One feature's attributes, by name, without building a dictionary per feature.
    /// </summary>
    /// <remarks>
    /// <b>A view, reused.</b> <see cref="Feature"/> already resolves a name against
    /// its schema; copying that into a fresh dictionary for every feature of every
    /// map would allocate once per feature for no information gained.
    /// </remarks>
    private sealed class FeatureAttributes : IReadOnlyDictionary<string, object?>
    {
        /// <summary>The feature being drawn.</summary>
        public Feature? Current { get; set; }

        /// <inheritdoc/>
        public IEnumerable<string> Keys => Current?.Schema.Names ?? [];

        /// <inheritdoc/>
        public IEnumerable<object?> Values
        {
            get
            {
                foreach (string name in Keys)
                {
                    yield return Current?[name];
                }
            }
        }

        /// <inheritdoc/>
        public int Count => Current?.Schema.Names.Count ?? 0;

        /// <inheritdoc/>
        public object? this[string key] =>
            TryGetValue(key, out object? value)
                ? value
                : throw new KeyNotFoundException($"No attribute '{key}'.");

        /// <inheritdoc/>
        public bool ContainsKey(string key) => TryGetValue(key, out _);

        /// <inheritdoc/>
        public bool TryGetValue(string key, out object? value)
        {
            value = null;

            if (Current is null)
            {
                return false;
            }

            try
            {
                value = Current[key];
                return true;
            }
            catch (KeyNotFoundException)
            {
                // A style naming a column the layer does not have is a real case —
                // one style over two layers, or a column since renamed — and it
                // resolves to null rather than failing the map.
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            foreach (string name in Keys)
            {
                yield return new KeyValuePair<string, object?>(name, Current?[name]);
            }
        }

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
