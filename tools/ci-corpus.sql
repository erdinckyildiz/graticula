-- A geometry corpus for CI, because the real one cannot travel.
--
-- Two suites discover a table with a geometry column and check an
-- implementation against PostGIS on whatever polygons they find. On a developer
-- machine that is real data. In CI there is none, and the suites fail loudly
-- rather than skip -- which is the right behaviour and leaves CI with nothing to
-- compare against.
--
-- **These are generated, and that is a weaker check than the local one.** Real
-- cadastral and administrative polygons have collinear runs, repeated vertices,
-- near-degenerate slivers and rings that close on themselves; that is precisely
-- why the corpus tests exist, and it is what found the Douglas-Peucker ring
-- defects. What is below is irregular but well-behaved. It proves the code runs
-- and agrees with PostGIS; it does not prove it survives real data.
--
-- **Deterministic, so a failure is reproducible.** setseed fixes the sequence,
-- so the polygon that fails in CI is the polygon that fails when you run this
-- locally. A random corpus turns an intermittent failure into an unreproducible
-- one, which is worse than no corpus.

create schema if not exists cicorpus;

drop table if exists cicorpus.shapes;

create table cicorpus.shapes (
    id   integer primary key,
    geom geometry(Polygon, 4326)
);

-- A closed ring built from a random walk in polar coordinates: the radius
-- wanders, so the outline is irregular rather than a circle, and the vertices
-- are ordered by angle so it cannot self-intersect. Between 12 and 60 vertices,
-- which straddles the 8-to-200 window both suites select on.
do $$
declare
    shape      integer;
    vertices   integer;
    step       integer;
    angle      double precision;
    radius     double precision;
    centre_x   double precision;
    centre_y   double precision;
    points     geometry[];
    candidate  geometry;
begin
    perform setseed(0.20260815);

    for shape in 1..60 loop
        vertices := 12 + (random() * 48)::integer;

        -- Spread across a few degrees so the shapes are metres-to-kilometres
        -- across once projected, and away from the poles and the antimeridian
        -- where UTM and MGRS have their own rules.
        centre_x := 25 + (random() * 20);
        centre_y := 36 + (random() * 8);

        radius := 0.002 + (random() * 0.02);
        points := array[]::geometry[];

        for step in 0..(vertices - 1) loop
            angle := 2 * pi() * step / vertices;

            -- The radius wanders by up to 40 per cent, which is what makes the
            -- outline irregular without letting it cross itself.
            radius := greatest(0.001, radius * (0.8 + random() * 0.4));

            points := points || ST_MakePoint(
                centre_x + radius * cos(angle),
                centre_y + radius * sin(angle));
        end loop;

        points := points || points[1];

        candidate := ST_SetSRID(ST_MakePolygon(ST_MakeLine(points)), 4326);

        -- Validity is asserted rather than assumed: one suite selects on
        -- ST_IsValid and would silently get a smaller corpus, and a corpus that
        -- quietly shrinks to nine rows fails with "no table held at least ten
        -- polygons" instead of naming the real problem.
        if ST_IsValid(candidate) then
            insert into cicorpus.shapes (id, geom) values (shape, candidate);
        end if;
    end loop;
end $$;

-- Loud, so a seed that produced too few shapes is a failed step rather than a
-- confusing test failure two jobs later.
do $$
declare
    total integer;
begin
    select count(*) into total from cicorpus.shapes;

    if total < 30 then
        raise exception
            'The CI corpus has only % shapes and the suites need at least ten valid ones '
            'with 8 to 200 vertices. The generator above produced too few.', total;
    end if;

    raise notice 'CI corpus: % polygons', total;
end $$;
