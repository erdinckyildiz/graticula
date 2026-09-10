-- Three geometry tables, in a schema of their own, that no layer claims.
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
-- <b>NOT in the `hosted` schema, since 2026-09-10.</b> They were, and it made the fixture
-- contradict a decision the product had already taken.
-- [ADR-058](../docs/adr/ADR-058-the-datastore-schema-is-edited-from-the-screen.md) says the
-- server may alter *what it imported or defined -- everything in the `hosted` schema*, and
-- `HostedDataEndpoints.AlterableSchema` is that sentence as code: datastore source **and**
-- schema `hosted`. Two foreign tables sitting inside `hosted` are therefore indistinguishable
-- from the server's own, so `HostedFieldConformanceTests.A_table_this_server_did_not_create_is
-- _refused` registered one, asked for a column, and got **201**. It failed only in CI, because
-- a developer machine has dozens of free tables and the first one is in another schema.
--
-- <b>The rule the fixture broke is the product's, not the test's.</b> A schema name is a weak
-- proof of authorship and that is worth knowing -- but `hosted` being the server's alone is a
-- stated decision, and a fixture that puts somebody else's table in there is asserting the
-- opposite of what the product promises. The honest fixture is a table this server plainly did
-- not create, which is what a schema of its own makes it.
--
-- Run after the server has migrated; this needs no schema of the server's to exist.
create schema if not exists cifree;

create table if not exists cifree.zz_free_one (
    objectid integer generated always as identity primary key,
    name     text,
    shape    geometry(Polygon, 3857)
);

-- <b>A third, for `MyContentIsWhatTheCallerOwnsTests`.</b> That test publishes as a second
-- member so that it can tell *what I own* from *what I can see*, and it FAILS rather than
-- skips without a table to publish — so CI never ran it and said so on every build for weeks.
-- Its own table, because the other two are claimed by `AmbiguousLayerNameTests` and a fixture
-- that shares one table between tests couples them at whatever order xUnit chooses.
create table if not exists cifree.zz_free_three (
    objectid integer generated always as identity primary key,
    name     text,
    shape    geometry(Polygon, 3857)
);

create table if not exists cifree.zz_free_two (
    objectid integer generated always as identity primary key,
    name     text,
    shape    geometry(Polygon, 3857)
);

-- One row each, because a table with no geometry at all can be read as having no
-- geometry column by anything that samples rather than reads the catalogue.
insert into cifree.zz_free_one (name, shape)
select 'free one', ST_SetSRID(ST_MakeEnvelope(3200000, 5000000, 3200500, 5000500), 3857)
where not exists (select 1 from cifree.zz_free_one);

-- <b>A real Web Mercator extent, beside its two neighbours.</b> The first attempt used
-- `ST_MakeEnvelope(0, 0, 1, 1)` and the publish refused it in a sentence worth keeping: a
-- one-metre square at the origin *is what degrees stored under a projected code look like*, and
-- publishing over it would answer every request correctly and put the data somewhere it is not.
-- The guard is right; the fixture was wrong.
insert into cifree.zz_free_three (name, shape)
select 'free three', ST_SetSRID(ST_MakeEnvelope(3202000, 5002000, 3202500, 5002500), 3857)
where not exists (select 1 from cifree.zz_free_three);

insert into cifree.zz_free_two (name, shape)
select 'free two', ST_SetSRID(ST_MakeEnvelope(3201000, 5001000, 3201500, 5001500), 3857)
where not exists (select 1 from cifree.zz_free_two);
