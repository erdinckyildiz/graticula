using System;

namespace Graticula.Platform.Catalog;

/// <summary>How many rows on each side of a relationship.</summary>
/// <remarks>
/// <b>Many-to-many is deliberately absent.</b> ADR-013 §3 allows it "via an
/// intermediate table", which is a second declaration — the junction table and
/// both its key columns — that the ADR sketches and does not specify. Accepting
/// the word without the mechanism would produce a relationship that declares
/// itself and cannot be queried, so it is refused with that reason rather than
/// half-supported.
/// </remarks>
public enum RelationshipCardinality
{
    /// <summary>One row on each side.</summary>
    OneToOne,

    /// <summary>One origin row, many related rows.</summary>
    OneToMany,
}

/// <summary>
/// A declared relationship between two layers.
/// </summary>
/// <param name="Id">Its identity.</param>
/// <param name="Name">What an administrator called it.</param>
/// <param name="OriginLayerId">The layer a client starts from.</param>
/// <param name="OriginKey">The column on that side.</param>
/// <param name="RelatedLayerId">The layer it reaches.</param>
/// <param name="RelatedKey">The column on that side.</param>
/// <param name="Cardinality">How many rows on each side.</param>
/// <param name="Composite">
/// Whether deleting an origin row deletes the related ones.
/// </param>
/// <remarks>
/// <para>
/// <b>Declared rather than discovered</b> (ADR-013 §3), which makes it work on a
/// plain PostGIS schema with ordinary foreign keys — or with none at all. An
/// administrator can relate two tables that were never designed to be related.
/// </para>
/// <para>
/// <b>And therefore able to be wrong.</b> Nothing in this record knows whether
/// the two columns hold the same thing. The admin API checks that both exist and
/// that their types can be compared before accepting a declaration, which is
/// §7's condition — it cannot check that the values mean the same thing, and
/// nothing could.
/// </para>
/// </remarks>
public sealed record LayerRelationship(
    Guid Id,
    string Name,
    Guid OriginLayerId,
    string OriginKey,
    Guid RelatedLayerId,
    string RelatedKey,
    RelationshipCardinality Cardinality,
    bool Composite);
