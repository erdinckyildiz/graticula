drop table if exists q68_polygons, q68_lines, q68_wide;
create table q68_polygons as select osm_id::bigint as osm_id, name, geom from public.polygons;
create table q68_lines as select osm_id::bigint as osm_id, name, ST_Multi(ST_Boundary(geom))::geometry(MultiLineString, 3857) as geom from public.polygons;
-- 24 attributes beside name: low-cardinality categories, mid-cardinality names, numbers, and one value unique to
-- each feature, which is the case where the value dictionary is as large as the tile's feature count.
create table q68_wide as
select osm_id::bigint as osm_id, name, building,
       (array['residential','commercial','industrial','school','hospital','mosque','retail','office','garage','shed'])[1 + (osm_id::bigint % 10)] as kind,
       (array['concrete','brick','stone','wood','steel'])[1 + (osm_id::bigint % 5)] as material,
       (array['yes','no'])[1 + (osm_id::bigint % 2)] as roof_flat,
       'İlçe ' || (osm_id::bigint % 40) as district,
       'Mahalle ' || (osm_id::bigint % 900) as neighbourhood,
       'Sokak ' || (osm_id::bigint % 5000) as street,
       (osm_id::bigint % 200)::text as house_number,
       (osm_id::bigint % 12)::int as levels,
       round((ST_Area(geom) / 100)::numeric, 1)::double precision as area_hm2,
       round((ST_Perimeter(geom))::numeric, 1)::double precision as perimeter_m,
       (1950 + osm_id::bigint % 75)::int as built_year,
       ((osm_id::bigint * 7) % 1000) / 10.0 as height_m,
       (osm_id::bigint % 3 = 0) as listed,
       'src-' || (osm_id::bigint % 4) as source,
       to_char(date '2020-01-01' + (osm_id::bigint % 2000)::int, 'YYYY-MM-DD') as surveyed,
       (array['A','B','C','D'])[1 + (osm_id::bigint % 4)] as energy_class,
       (osm_id::bigint % 6)::int as entrances,
       (osm_id::bigint % 97)::int as units,
       'Parsel ' || (osm_id::bigint % 30000) as parcel,
       md5(osm_id) as global_code,
       (osm_id::bigint % 1000) * 1.37 as assessed_value,
       (array['public','private','mixed'])[1 + (osm_id::bigint % 3)] as ownership,
       geom
  from public.polygons;
create index on q68_polygons using gist (geom);
create index on q68_lines using gist (geom);
create index on q68_wide using gist (geom);
analyze q68_polygons; analyze q68_lines; analyze q68_wide;
select 'polygons', count(*), pg_size_pretty(pg_total_relation_size('q68_polygons')) from q68_polygons
union all select 'lines', count(*), pg_size_pretty(pg_total_relation_size('q68_lines')) from q68_lines
union all select 'wide', count(*), pg_size_pretty(pg_total_relation_size('q68_wide')) from q68_wide;
