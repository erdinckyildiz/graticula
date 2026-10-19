-- Hands an existing deployment's own schemas to a serving role that row-level security binds — ADR-204
-- condition 9, docs/deployment.md §0.3.
--
-- <b>Why this exists.</b> Until 2026-10-19 compose.yaml connected the server as the datastore image's
-- POSTGRES_USER, which is a superuser, and every object the server ever made is that role's. A superuser is
-- never bound by row-level security, so the floor ADR-204 keeps organisations apart with would be inert. The
-- server has to connect as a role that is neither a superuser nor BYPASSRLS, and that role has to own what the
-- server made.
--
-- <b>Not REASSIGN OWNED.</b> That also hands over cluster-wide objects — every database the old role owns, the
-- development database beside the production one included. This moves the server's schemas and what is in
-- them, and nothing else: gisserver, hosted, hosted_*, graticula_recycle. PostGIS stays the superuser's;
-- spatial_ref_sys is readable by everyone already. Other schemas — tables a registered data source reads, a
-- customer's own — are not the server's and keep their owners; grant the serving role what it needs on them.
--
-- <b>Measured 2026-10-19</b> (benchmarks/tenant-floor/ROLES.md): a fixture built and seeded as a superuser, this
-- run, the server restarted as the new role — Conformance 853 passed and 10 failed, the same 853 and the same 10
-- as the same fixture served as the superuser, and no permission error in the server's log.
--
-- Run as a superuser, in the deployment's database, with the role already made and the server stopped:
--
--   CREATE ROLE graticula LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD '…';
--   PGOPTIONS="-c graticula.serving_role=graticula" psql -d gis -v ON_ERROR_STOP=1 -f tools/serving-role.sql
--
-- then point Graticula__PlatformStore at the new role and start the server: it no longer warns 1094.
-- One transaction: it moves everything or nothing. Back up first (`graticula backup`).

begin;

create temp table server_schemas on commit drop as
  select nspname from pg_namespace
   where nspname in ('gisserver', 'hosted', 'graticula_recycle') or nspname like 'hosted\_%';

do $$
declare
  r record;
  target text := current_setting('graticula.serving_role', true);
begin
  if target is null or target = '' then
    raise exception 'Say which role: PGOPTIONS="-c graticula.serving_role=<role>". Nothing was changed.';
  end if;

  if not exists (select 1 from pg_roles where rolname = target) then
    raise exception 'Role % does not exist; create it first (docs/deployment.md §0.3). Nothing was changed.', target;
  end if;

  if exists (select 1 from pg_roles where rolname = target and (rolsuper or rolbypassrls)) then
    raise exception 'Role % is a superuser or has BYPASSRLS, which is what this is for removing. Nothing was changed.',
      target;
  end if;

  for r in select nspname from server_schemas loop
    execute format('alter schema %I owner to %I', r.nspname, target);
  end loop;

  -- Tables, views, materialized views, foreign tables, and free-standing sequences; a sequence a column owns
  -- (serial or identity) moves with its table and refuses to be moved alone.
  for r in
    select c.oid::regclass as name, c.relkind
      from pg_class c join pg_namespace n on n.oid = c.relnamespace
     where n.nspname in (select nspname from server_schemas)
       and c.relkind in ('r', 'p', 'v', 'm', 'f', 'S')
       and not (c.relkind = 'S' and exists (
             select 1 from pg_depend d
              where d.objid = c.oid and d.classid = 'pg_class'::regclass and d.deptype in ('a', 'i')))
  loop
    execute format('alter %s %s owner to %I',
      case r.relkind when 'v' then 'view' when 'm' then 'materialized view' when 'f' then 'foreign table'
                     when 'S' then 'sequence' else 'table' end,
      r.name, target);
  end loop;

  -- The feature-history triggers' functions live in hosted (ADR-078).
  for r in
    select p.oid::regprocedure as name, p.prokind
      from pg_proc p join pg_namespace n on n.oid = p.pronamespace
     where n.nspname in (select nspname from server_schemas)
  loop
    execute format('alter %s %s owner to %I',
      case r.prokind when 'p' then 'procedure' when 'a' then 'aggregate' else 'function' end, r.name, target);
  end loop;

  for r in
    select t.oid::regtype as name
      from pg_type t join pg_namespace n on n.oid = t.typnamespace
     where n.nspname in (select nspname from server_schemas) and t.typtype in ('d', 'e')
  loop
    execute format('alter type %s owner to %I', r.name, target);
  end loop;

  -- The server makes schemas at runtime (hosted_*, graticula_recycle) and needs to reach the database.
  execute format('grant create, connect, temporary on database %I to %I', current_database(), target);

  raise notice 'The server''s schemas are %''s now. Point Graticula__PlatformStore at it and start the server.', target;
end $$;

commit;
