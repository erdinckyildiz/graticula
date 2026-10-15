-- ADR-010 condition 4, A-029: how often a tile and a feature query repeat an identical earlier request, by window.
-- Aggregates only: no address or principal is printed. Run against a platform store, e.g.
--   psql -U gis -d gis -A -F ' | ' < tools/cache-reuse.sql
-- First run on the showcase, 2026-10-15, over 2026-09-14..2026-10-14 (ADR-010 §12, condition 4).
set search_path = gisserver;
with r as (
  select occurred_at, source_address, case
           when path like '%/tile/%' or (lower(query) like '%request=gettile%') then 'tile'
           when path ~* '/(FeatureServer|MapServer)/[0-9]+/query$' or path ~* '/ogc/features/.*/items' or lower(query) like '%request=getfeature%' then 'feature query'
           else 'skip' end as kind,
         md5(path || '?' || coalesce(query, '')) as k
    from request_log
   where status = 200 and method in ('GET','POST')
     and path not like '/admin/%' and path not like '/healthz%' and path not like '/server/%' and path not like '/studio/%'
), w as (
  select kind, occurred_at - lag(occurred_at) over (partition by kind, k order by occurred_at) as gap,
         occurred_at - lag(occurred_at) over (partition by kind, k, source_address order by occurred_at) as gap_same
    from r where kind <> 'skip'
)
select kind, count(*) requests,
  round(100.0*count(*) filter (where gap <= interval '5 minutes')/count(*),1) "5min",
  round(100.0*count(*) filter (where gap <= interval '1 hour')/count(*),1) "1h",
  round(100.0*count(*) filter (where gap <= interval '1 day')/count(*),1) "1d",
  round(100.0*count(*) filter (where gap <= interval '1 day' and (gap_same is null or gap_same > interval '1 day'))/count(*),1) "1d_other_client",
  round(100.0*count(*) filter (where gap is not null)/count(*),1) "ever"
from w group by kind;
