-- The platform store lives in its own schema, not in public.
--
-- ADR-002 keeps platform metadata logically distinct from hosted data, and this
-- is where that becomes physical: `gisserver` holds definitions, identity,
-- audit and the schema stamp, while `public` holds whatever the customer
-- publishes. A restore that mixes them is then visibly wrong rather than
-- subtly so.
create schema if not exists gisserver;

-- PostGIS in the same database, because Q-69 made the datastore mandatory and
-- hosted data has nowhere else to go.
create extension if not exists postgis;

-- A stamp the server can read to tell an appliance datastore from a database
-- somebody pointed at us. Deliberately not the schema version — that is the
-- server's to write, in gisserver.platform_schema, and two components writing
-- one number is how they come to disagree.
create table if not exists gisserver.datastore_stamp (
    only_row     boolean     not null default true primary key,
    image_version integer    not null,
    initialised_at timestamptz not null default now(),
    constraint datastore_stamp_single_row check (only_row)
);

insert into gisserver.datastore_stamp (image_version) values (1)
on conflict (only_row) do nothing;
