-- Two geometry tables in the hosted schema that no layer claims.
--
-- <b>`AmbiguousLayerNameTests` publishes one name from two different tables</b> and
-- asserts the ambiguous name becomes a refusal rather than a coin flip. To do that it
-- needs two tables the catalogue does not already own, and CI has none: everything the
-- conformance fixtures create is published the moment it exists.
--
-- <b>Made with SQL rather than through the API, and that is the whole point.</b> The
-- first attempt defined two layers and unpublished them — which does leave the tables
-- behind, and also leaves two empty services in the directory answering 404 for their
-- layers ([D-157](../docs/architecture-debt.md)). **Not because nothing deletes a
-- service** -- `DELETE /admin/featureservices/{name}` does, and that half of D-157 was
-- corrected on 2026-08-25 -- but because unpublishing does not, and a fixture that has
-- to remember a second call is a fixture that eventually forgets it.
-- A fixture that creates a defect in order to test something else is worse than no
-- fixture. These rows never reach the catalogue.
--
-- Run after the server has migrated, so the `hosted` schema exists.

create table if not exists hosted.zz_free_one (
    objectid integer generated always as identity primary key,
    name     text,
    shape    geometry(Polygon, 3857)
);

create table if not exists hosted.zz_free_two (
    objectid integer generated always as identity primary key,
    name     text,
    shape    geometry(Polygon, 3857)
);

-- One row each, because a table with no geometry at all can be read as having no
-- geometry column by anything that samples rather than reads the catalogue.
insert into hosted.zz_free_one (name, shape)
select 'free one', ST_SetSRID(ST_MakeEnvelope(3200000, 5000000, 3200500, 5000500), 3857)
where not exists (select 1 from hosted.zz_free_one);

insert into hosted.zz_free_two (name, shape)
select 'free two', ST_SetSRID(ST_MakeEnvelope(3201000, 5001000, 3201500, 5001500), 3857)
where not exists (select 1 from hosted.zz_free_two);
