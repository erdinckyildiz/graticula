using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// Layer extents in WGS 84, and the count of round trips it takes to get them.
/// </summary>
/// <remarks>
/// <b>[Q-125](../../docs/open-questions.md) closed on a cost, and the cost was
/// wrong.</b> The row recorded projecting a capabilities document's extents as *one
/// round trip per layer* — one to two thousand at the stated scale — and concluded the
/// WFS writer should omit the box rather than pay it. The projector takes a list, so the
/// real figure is one call per distinct reference. The test that matters here is
/// therefore not *are the numbers right* but *how many times was the projector asked*,
/// which is what a counting stub is for.
/// </remarks>
public sealed class GeographicExtentsTests
{
    /// <summary>A projector that counts calls and shifts coordinates predictably.</summary>
    /// <remarks>
    /// <b>It does not project.</b> Asserting on real transformed coordinates would make
    /// this a test of PROJ; what is under test is the batching and the ordering of the
    /// answers, and a shift of a known size proves both without a datum anywhere near it.
    /// </remarks>
    private sealed class CountingProjector : IProjector
    {
        public int Calls { get; private set; }

        public List<int> From { get; } = [];

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<(IReadOnlyList<Graticula.Geometries.Geometry> Projected, ProjectionProvenance Provenance)>
            ProjectAsync(
                IReadOnlyList<Graticula.Geometries.Geometry> geometries,
                int fromSrid,
                int toSrid,
                CancellationToken cancellationToken)
        {
            Calls++;
            From.Add(fromSrid);

            List<Graticula.Geometries.Geometry> moved = [];

            foreach (Graticula.Geometries.Geometry geometry in geometries)
            {
                Envelope box = geometry.Envelope;
                moved.Add(new Point(box.MinX + fromSrid, box.MinY + fromSrid));
            }

            return Task.FromResult<(IReadOnlyList<Graticula.Geometries.Geometry>, ProjectionProvenance)>(
                (moved, new ProjectionProvenance("counting stub", null)));
        }
    }

    [Fact]
    public async Task Six_layers_over_two_references_cost_two_calls()
    {
        CountingProjector projector = new();

        (int, Envelope?)[] extents =
        [
            (3857, new Envelope(0, 0, 10, 10)),
            (2180, new Envelope(0, 0, 10, 10)),
            (3857, new Envelope(20, 20, 30, 30)),
            (2180, new Envelope(20, 20, 30, 30)),
            (3857, new Envelope(40, 40, 50, 50)),
            (2180, new Envelope(40, 40, 50, 50)),
        ];

        IReadOnlyList<Envelope?> answer =
            await GeographicExtents.InWgs84Async(projector, extents, CancellationToken.None);

        // <b>The whole of Q-125's answer is this number.</b> Six layers, two references,
        // two calls — not six. At a thousand layers over the same two references it is
        // still two.
        Assert.Equal(2, projector.Calls);
        Assert.Equal(6, answer.Count);
    }

    [Fact]
    public async Task Each_layer_gets_its_own_extent_back_in_its_own_position()
    {
        // <b>The control on batching.</b> Four layers go into one call as sixteen
        // corners, and unpicking them wrongly would give every layer the same box, or
        // the box of its neighbour — both of which look plausible in a document.
        CountingProjector projector = new();

        (int, Envelope?)[] extents =
        [
            (2180, new Envelope(0, 0, 10, 10)),
            (2180, new Envelope(100, 100, 110, 110)),
            (2180, new Envelope(200, 200, 210, 210)),
        ];

        IReadOnlyList<Envelope?> answer =
            await GeographicExtents.InWgs84Async(projector, extents, CancellationToken.None);

        Assert.Equal(1, projector.Calls);

        // The stub shifts by the source srid, so each answer is its input plus 2180.
        Assert.Equal(new Envelope(2180, 2180, 2190, 2190), answer[0]);
        Assert.Equal(new Envelope(2280, 2280, 2290, 2290), answer[1]);
        Assert.Equal(new Envelope(2380, 2380, 2390, 2390), answer[2]);
    }

    [Fact]
    public async Task A_layer_already_in_wgs84_is_not_sent_anywhere()
    {
        CountingProjector projector = new();

        (int, Envelope?)[] extents = [(4326, new Envelope(1, 2, 3, 4))];

        IReadOnlyList<Envelope?> answer =
            await GeographicExtents.InWgs84Async(projector, extents, CancellationToken.None);

        Assert.Equal(0, projector.Calls);
        Assert.Equal(new Envelope(1, 2, 3, 4), answer[0]);
    }

    [Fact]
    public async Task An_empty_or_missing_extent_answers_null_rather_than_a_point()
    {
        // A layer with no features has no extent, and a zero-area box at the origin is
        // the wrong answer twice over: it is not where the layer is, and it tells a
        // client to zoom there.
        CountingProjector projector = new();

        (int, Envelope?)[] extents = [(2180, null), (2180, Envelope.Empty)];

        IReadOnlyList<Envelope?> answer =
            await GeographicExtents.InWgs84Async(projector, extents, CancellationToken.None);

        Assert.Equal(0, projector.Calls);
        Assert.Null(answer[0]);
        Assert.Null(answer[1]);
    }

    [Fact]
    public async Task A_reference_that_cannot_be_transformed_costs_that_layer_and_no_other()
    {
        // <b>One unusual layer must not empty the document.</b> This is what the try
        // around the projector is for, and it is the difference between a client losing
        // one layer's initial zoom and a client concluding the server has nothing.
        RefusingProjector projector = new();

        (int, Envelope?)[] extents =
        [
            (3857, new Envelope(0, 0, 10, 10)),
            (99999, new Envelope(0, 0, 10, 10)),
        ];

        IReadOnlyList<Envelope?> answer =
            await GeographicExtents.InWgs84Async(projector, extents, CancellationToken.None);

        Assert.NotNull(answer[0]);
        Assert.Null(answer[1]);
    }

    private sealed class RefusingProjector : IProjector
    {
        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) =>
            Task.FromResult(srid != 99999);

        public Task<(IReadOnlyList<Graticula.Geometries.Geometry> Projected, ProjectionProvenance Provenance)>
            ProjectAsync(
                IReadOnlyList<Graticula.Geometries.Geometry> geometries,
                int fromSrid,
                int toSrid,
                CancellationToken cancellationToken)
        {
            if (fromSrid == 99999)
            {
                throw new InvalidOperationException($"No transformation from {fromSrid}.");
            }

            List<Graticula.Geometries.Geometry> moved = [];

            foreach (Graticula.Geometries.Geometry geometry in geometries)
            {
                Envelope box = geometry.Envelope;
                moved.Add(new Point(box.MinX, box.MinY));
            }

            return Task.FromResult<(IReadOnlyList<Graticula.Geometries.Geometry>, ProjectionProvenance)>(
                (moved, new ProjectionProvenance("refusing stub", null)));
        }
    }
}
