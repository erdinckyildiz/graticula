"""Fleet routing over grapy's network datasets: vehicle routing, last mile delivery and waste collection — ADR-239.

The pages define each problem's inputs, outputs, constraints and violation codes; Pro's solver, which they describe only
as a travel-time matrix feeding route assignment and sequencing, is not published. grapy solves them with published
heuristics, an *equivalent* under ADR-224 §5.6:

- **Construction**: Solomon's insertion heuristic I1 (Solomon 1987) — each route seeded with the unrouted order farthest
  from its start, then the order with the largest ``λ·t(start, u) − c1(u)`` inserted at its cheapest feasible position,
  ``c1`` the increase in the objective below and λ = 1.
- **Improvement**: ruin and recreate (Schrimpf, Schneider, Stamm-Wilbrandt and Dueck 2000) — radial or random removal
  of a few orders, best insertion of them back in a random order, accepted by threshold accepting (Dueck and Scheuer
  1990) with a threshold falling linearly to zero. The random numbers come from a fixed seed, so a solve is
  reproducible.
- **Waste collection** routes collection *units* — the stops on one street edge, or on one side of it — as the
  capacitated arc routing problem's tasks, each served by traversing its edge whole; for a fixed order of units the
  direction of each traversal is chosen by dynamic programming over the two directions (Beullens, Muyldermans,
  Cattrysse and Van Oudheusden 2003).

What a route's evaluation enforces, as the pages define it: time windows (one or two; waiting when early; lateness up
to MaxViolationTime, beyond it a hard violation), depot time windows (hard), earliest and latest route start,
InboundArriveTime and OutboundDepartTime, capacities in up to nine dimensions, MaxOrderCount, MaxTotalTime,
MaxTotalTravelTime, MaxTotalDistance, specialties, hard and soft route zones, order pairs (same route, pickup first,
MaxTransitTime), breaks of the three documented kinds (at most five a route), renewals, AssignmentRule and anchoring.

The objective, INFERRED where the pages give none: serving as many orders as possible comes first; then the routes'
TotalCost (FixedCost, RegularTimeCost, OvertimeCost and DistanceCost as the pages define them), plus lateness weighted
by Time Window Importance (High 10, Medium 1, Low 0.0001 times CostPerUnitTime per unit of violation time — measured
against ArcGIS Pro 3.7 on 2026-10-31: High between 7.5 and 10, Medium between 0.91 and 1.11, and Low above zero, since
it breaks a tie toward the order on time, but below 0.001), excess transit time of order pairs weighted by Excess
Transit Importance as High 10, Medium 1, Low 0.1 (not measured), and a soft zone's straight-line distance from
an order times the route's CostPerUnitDistance (or CostPerUnitTime when that is zero), less the Revenue of the orders
served; of two plans of equal cost, the one with less travel time (Pro 3.7 chose so when waiting for a break absorbed
the difference). Waste collection minimises the number of routes used, then their total time, as its page says it does.
"""
from __future__ import annotations

import datetime
import math
import random

from ._runtime import ExecuteError, NotBuiltError

EPOCH = datetime.datetime(1899, 12, 30)
SEED = 20261031
BIG = 1e9
ROUTE_WEIGHT = 1e6
TRAVEL_TIE = 1e-6       # of two plans of equal cost, the one that travels less, as Pro 3.7 chose (measured 2026-10-31)
_TO_MINUTES = {"SECONDS": 1 / 60, "MINUTES": 1.0, "HOURS": 60.0, "DAYS": 1440.0}
_IMPORTANCE = {"HIGH": 10.0, "MEDIUM": 1.0, "LOW": 0.1}
# Time Window Importance as Pro 3.7 weighs it (measured 2026-10-31, ADR-239 §5.8 f): Low is a tie-breaker, not 0.1.
_WINDOW_IMPORTANCE = {**_IMPORTANCE, "LOW": 0.0001}
CONSTRUCTION = ("Solomon's insertion heuristic I1", "Solomon 1987")
IMPROVEMENT = ("ruin and recreate with threshold accepting", "Schrimpf et al. 2000; Dueck and Scheuer 1990")
ARC_ROUTING = ("collection units as capacitated arc routing tasks, traversal directions by dynamic programming",
               "Beullens et al. 2003")
METHOD = (f"{CONSTRUCTION[0]}, then {IMPROVEMENT[0]}", f"{CONSTRUCTION[1]}; {IMPROVEMENT[1]}")
RESEQUENCE = ("Or-opt moves of each collection unit within its own route, traversal directions by dynamic programming",
              "Or 1976; Beullens et al. 2003")
WASTE_METHOD = (f"{ARC_ROUTING[0]}; {CONSTRUCTION[0]}, then {IMPROVEMENT[0]}",
                f"{ARC_ROUTING[1]}; {CONSTRUCTION[1]}; {IMPROVEMENT[1]}")

# Violation codes, as the pages number them.
MAX_ORDER_COUNT, CAPACITY, MAX_TOTAL_TIME, MAX_TRAVEL_TIME, MAX_DISTANCE = 0, 1, 2, 3, 4
HARD_WINDOW, SPECIALTY, HARD_ZONE, MAX_TRANSIT, PAIR, UNREACHABLE = 5, 6, 7, 8, 9, 10
NO_BREAK, NO_RENEWAL, BREAK_TRAVEL, BREAK_WORK, INBOUND_OUTBOUND, ANCHOR = 11, 12, 13, 14, 15, 16


# ---- values -----------------------------------------------------------------------------------------------------------

def get(row, *names, default=None):
    """A row's value for the first of the names it has (case-insensitive), or the default."""
    lower = row.get("_lower")
    if lower is None:
        lower = {str(k).lower(): v for k, v in row.items()}
        row["_lower"] = lower
    for name in names:
        value = lower.get(name.lower())
        if value is not None and not (isinstance(value, str) and value.strip() == ""):
            return value
    return default


def number(value, default=None):
    if value is None or (isinstance(value, str) and not value.strip()):
        return default
    return float(value)


def moment(value, default_date):
    """Minutes since 1899-12-30 of a date, time or text; a time alone falls on the default date."""
    if value is None or (isinstance(value, str) and not value.strip()):
        return None
    if isinstance(value, str):
        text = value.strip().replace("T", " ")
        for form in ("%Y-%m-%d %H:%M:%S.%f", "%Y-%m-%d %H:%M:%S", "%Y-%m-%d %H:%M", "%Y-%m-%d", "%H:%M:%S", "%H:%M"):
            try:
                parsed = datetime.datetime.strptime(text, form)
            except ValueError:
                continue
            value = parsed.time() if form.startswith("%H") else parsed
            break
        else:
            raise ExecuteError(f"ERROR 030000: '{value}' is not a date or time.")
    if isinstance(value, datetime.datetime):
        stamp = value.replace(tzinfo=None)
    elif isinstance(value, datetime.date):
        stamp = datetime.datetime.combine(value, datetime.time())
    elif isinstance(value, datetime.time):
        stamp = datetime.datetime.combine(default_date, value)
    else:
        raise ExecuteError(f"ERROR 030000: '{value}' is not a date or time.")
    return (stamp - EPOCH).total_seconds() / 60.0


def stamp(minutes):
    return None if minutes is None else EPOCH + datetime.timedelta(minutes=minutes)


def time_factor(units):
    word = str(getattr(units, "value", units) or "Minutes").upper()
    if word not in _TO_MINUTES:
        raise ExecuteError(f"ERROR 030000: '{units}' is not a time unit.")
    return _TO_MINUTES[word]


def importance(value, table=None):
    table = table or _IMPORTANCE
    word = str(getattr(value, "name", value) or "Medium").upper()
    if word not in table:
        raise ExecuteError(f"ERROR 030000: '{value}' is not High, Medium or Low.")
    return table[word]


# ---- the network costs between places ---------------------------------------------------------------------------------

class Matrix:
    """Travel time (minutes) and distance (metres, or None without a distance attribute) between located places, by
    Dijkstra's shortest path on the travel mode's impedance; each path's line on demand."""

    def __init__(self, graph, places):
        from . import _network
        units = {a: u for a, (_, u) in graph.attributes.items()}
        impedance = graph.impedance
        word = str(units.get(impedance) or "").upper()
        if word not in _TO_MINUTES:
            raise ExecuteError("ERROR 030000: fleet routing needs a travel mode whose impedance is a time attribute.")
        self.per_minute = _TO_MINUTES[word]
        self.distance_attribute = graph.mode.get("distanceAttributeName")
        if not self.distance_attribute:
            linear = [a for a, (usage, u) in graph.attributes.items() if usage == "Cost" and
                      str(u or "").upper().replace(" ", "") in _network._LINEAR]
            self.distance_attribute = linear[0] if linear else None
        self.per_metre = _network._LINEAR[str(units[self.distance_attribute]).upper().replace(" ", "")] \
            if self.distance_attribute else None
        self.graph = graph
        self.places = places                   # [(key, edge index, t)]
        self.solver = graph.solver(places)
        n = len(places)
        self.time = [[math.inf] * n for _ in range(n)]
        self.distance = [[math.inf] * n for _ in range(n)]
        self.searches = []
        for a, (key, _, _) in enumerate(places):
            distances, search = self.solver.run(key)
            self.searches.append(search)
            for b, (other, _, _) in enumerate(places):
                cost = float(distances[self.solver.location_node[other]])
                if a == b:
                    self.time[a][b], self.distance[a][b] = 0.0, 0.0
                elif math.isfinite(cost):
                    self.time[a][b] = cost * self.per_minute
                    if self.distance_attribute:
                        arcs = self.solver.path(search, key, other)
                        self.distance[a][b] = self.solver.total(arcs, self.distance_attribute) * self.per_metre
        self._lines = {}

    def line(self, a, b):
        if (a, b) not in self._lines:
            arcs = self.solver.path(self.searches[a], self.places[a][0], self.places[b][0]) if a != b else []
            self._lines[(a, b)] = self.solver.shape(arcs) if arcs else None
        return self._lines[(a, b)]


# ---- the search, shared by every problem ----------------------------------------------------------------------------

class Engine:
    """Solomon I1 construction and ruin-and-recreate improvement over a problem that answers:

    ``routes`` (indices usable), ``groups`` (tuples of item ids inserted together, in order), ``fixed`` ({route: [items
    that stay as they are]}), ``allowed(route, item)``, ``evaluate(route, sequence) -> (violations, cost)``,
    ``start_cost(route, item)`` (Solomon's t(start, u)), ``closeness(a, b)`` and ``revenue(item)``."""

    def __init__(self, problem, iterations=None):
        self.p = problem
        self.cache = {}
        free = [i for g in problem.groups for i in g]
        n = len(free)
        self.iterations = iterations if iterations is not None else (0 if n <= 1 else min(2000, max(150, 30000 // n)))
        self.rng = random.Random(SEED)

    def cost(self, route, sequence):
        key = (route, tuple(sequence))
        found = self.cache.get(key)
        if found is None:
            if len(self.cache) > 200000:
                self.cache.clear()
            found = self.p.evaluate(route, list(sequence))
            self.cache[key] = found
        return found

    def total(self, solution, unassigned):
        cost = sum(self.cost(r, s)[1] for r, s in solution.items())
        served = sum(self.p.revenue(i) for s in solution.values() for i in s)
        return BIG * sum(len(g) for g in unassigned) + cost - served

    def insertions(self, route, sequence, group):
        """Every feasible way to insert a group (one item, or a pickup then its delivery) into a sequence, with its
        cost increase: [(increase, new sequence)]."""
        if not all(self.p.allowed(route, i) for i in group):
            return []
        codes, before = self.cost(route, sequence)
        out = []
        n = len(sequence)
        if len(group) == 1:
            trials = (sequence[:k] + [group[0]] + sequence[k:] for k in range(n + 1))
        else:
            first, second = group
            trials = (sequence[:k] + [first] + sequence[k:j] + [second] + sequence[j:]
                      for k in range(n + 1) for j in range(k, n + 1))
        for trial in trials:
            found, cost = self.cost(route, trial)
            if found <= codes:          # an insertion adds no violation; one a preassignment forced may stay
                out.append((cost - before, trial))
        return out

    def best(self, solution, group, routes=None):
        found = None
        for route in routes if routes is not None else self.p.routes:
            for increase, trial in self.insertions(route, solution[route], group):
                if found is None or increase < found[0] - 1e-9:
                    found = (increase, route, trial)
        return found

    def construct(self):
        p = self.p
        solution = {r: list(p.fixed.get(r, [])) for r in p.routes}
        waiting = list(p.groups)
        for route in p.routes:
            while True:
                options = []
                for group in waiting:
                    moves = self.insertions(route, solution[route], group)
                    if moves:
                        increase, trial = min(moves, key=lambda m: m[0])
                        options.append((group, increase, trial))
                if not options:
                    break
                if not any(i not in p.fixed.get(route, []) for i in solution[route]):
                    # Solomon's seed: the order farthest from the route's start.
                    group, _, trial = max(options, key=lambda o: (p.start_cost(route, o[0][0]), -waiting.index(o[0])))
                else:
                    group, _, trial = max(options, key=lambda o: (p.start_cost(route, o[0][0]) - o[1],
                                                                  -waiting.index(o[0])))
                solution[route] = trial
                waiting.remove(group)
        return solution, waiting

    def recreate(self, solution, pool):
        self.rng.shuffle(pool)
        left = []
        for group in pool:
            found = self.best(solution, group)
            if found is None:
                left.append(group)
            else:
                solution[found[1]] = found[2]
        return left

    def solve(self):
        p = self.p
        solution, unassigned = self.construct()
        unassigned = self.recreate(solution, unassigned)
        current = self.total(solution, unassigned)
        best = ({r: list(s) for r, s in solution.items()}, list(unassigned), current)
        movable = {i: g for g in p.groups for i in g}
        start = abs(current - BIG * sum(len(g) for g in unassigned))
        threshold = 0.02 * max(1.0, start)
        for step in range(self.iterations):
            trial = {r: list(s) for r, s in solution.items()}
            routed = [i for r in p.routes for i in trial[r] if i in movable]
            pool = list(unassigned)
            if routed:
                count = self.rng.randint(1, max(1, min(len(routed), max(2, len(routed) * 3 // 10))))
                if self.rng.random() < 0.5:
                    centre = self.rng.choice(routed)
                    chosen = sorted(routed, key=lambda i: (p.closeness(centre, i), i))[:count]
                else:
                    chosen = self.rng.sample(routed, count)
                groups = []
                for i in chosen:
                    if movable[i] not in groups:
                        groups.append(movable[i])
                for group in groups:
                    for r in p.routes:
                        trial[r] = [i for i in trial[r] if i not in group]
                pool += groups
            left = self.recreate(trial, pool)
            value = self.total(trial, left)
            limit = threshold * (1 - step / max(1, self.iterations))
            if value < current + limit:
                solution, unassigned, current = trial, left, value
                if value < best[2] - 1e-9:
                    best = ({r: list(s) for r, s in trial.items()}, list(left), value)
        return best[0], best[1]


# ---- vehicle routing and last mile delivery ---------------------------------------------------------------------------

class Fleet:
    """A vehicle routing problem (``kind`` "VRP") or a last mile delivery (``kind`` "LMD") from its located inputs.

    ``inputs`` maps an input class name (Orders, Depots, Routes, Breaks, OrderPairs, RouteRenewals, RouteZones / Zones,
    OrderSpecialties, RouteSpecialties) to rows: dictionaries of field values, with ``oid``, ``point`` (shapely) and
    ``located`` (Graph.locate's answer) on the located classes. ``schema`` "One" reads the VehicleRoutingProblem schema
    One's names (TimeWindowStart1, DeliveryQuantities and Capacities as space-separated text, SpecialtyNames);
    otherwise the analysis layer's and schema Two's (DeliveryQuantity_1 …)."""

    def __init__(self, graph, inputs, kind="VRP", schema="Two", time_units="Minutes", distance_units="Miles",
                 default_date=None, time_window_factor="Medium", excess_transit_factor="Medium",
                 earliest_date=None, earliest_time=None, max_route_total_time=None, ignore_invalid=True):
        from . import _network
        self.graph, self.kind, self.schema = graph, kind, schema
        self.per_unit = time_factor(time_units)
        self.distance_word = str(getattr(distance_units, "value", distance_units) or "Miles").upper().replace(" ", "")
        if self.distance_word not in _network._LINEAR:
            raise ExecuteError(f"ERROR 030000: '{distance_units}' is not a distance unit.")
        self.per_distance_unit = _network._LINEAR[self.distance_word]
        self.default_date = default_date or datetime.date.today()
        self.window_weight = importance(time_window_factor, _WINDOW_IMPORTANCE)
        self.transit_weight = importance(excess_transit_factor)
        self.metres_per_map_unit = _network._per_unit(graph.srid) or 1.0
        orders = inputs.get("Orders", [])
        depots = inputs.get("Depots", [])
        # Places: the located orders and depots.
        places, self.place_of = [], {}
        for kind_name, rows in (("Orders", orders), ("Depots", depots)):
            for row in rows:
                if row.get("located") is not None:
                    self.place_of[(kind_name, row["oid"])] = len(places)
                    places.append(((kind_name, row["oid"]), row["located"][0], row["located"][1]))
        self.matrix = Matrix(graph, places)
        self.points = [graph.edges[i]["shape"].interpolate(t, normalized=True) for _, i, t in places]
        self.depot_rows = depots
        self.depots = {}
        for row in depots:
            name = str(get(row, "Name") or "").lower()
            if not name:
                raise ExecuteError("ERROR 030000: every depot needs a Name.")
            if name in self.depots:
                raise ExecuteError(f"ERROR 030000: the depot name '{get(row, 'Name')}' is not unique.")
            self.depots[name] = {"row": row, "place": self.place_of.get(("Depots", row["oid"])),
                                 "windows": self._windows(row, depot=True)}
        self._read_routes(inputs, earliest_date, earliest_time, max_route_total_time)
        self._read_orders(orders, inputs, ignore_invalid)
        self._read_pairs(inputs.get("OrderPairs", []))
        self.routes = [r for r, route in enumerate(self.route_list) if route["include"]]
        self._assign()

    # -- reading

    def _duration(self, value, default=0.0):
        found = number(value)
        return default if found is None else found * self.per_unit

    def _windows(self, row, depot=False):
        one = self.schema == "One"
        pairs = [("TimeWindowStart1" if one else "TimeWindowStart", "TimeWindowEnd1" if one else "TimeWindowEnd",
                  "MaxViolationTime1" if one else "MaxViolationTime")]
        if self.kind == "VRP":
            pairs.append(("TimeWindowStart2", "TimeWindowEnd2", "MaxViolationTime2"))
        windows = []
        for s, e, v in pairs:
            start, end = moment(get(row, s), self.default_date), moment(get(row, e), self.default_date)
            if start is None and end is None:
                continue
            limit = 0.0 if depot else (None if get(row, v) is None else self._duration(get(row, v)))
            if limit is not None and limit < 0:
                raise ExecuteError("ERROR 030000: MaxViolationTime cannot be negative.")
            windows.append((start, end, limit))
        if len(windows) == 2 and windows[0][1] is not None and windows[1][0] is not None and \
                windows[1][0] < windows[0][1]:
            raise ExecuteError("ERROR 030000: a second time window must follow the first and not overlap it.")
        return windows

    def _quantities(self, row, prefix):
        if self.schema == "One":
            text = get(row, {"Delivery": "DeliveryQuantities", "Pickup": "PickupQuantities", "Capacity": "Capacities"}[prefix])
            values = [float(v) for v in str(text).split()] if text is not None else []
            values += [None] * (9 - len(values))
        else:
            name = "Capacity_{}" if prefix == "Capacity" else prefix + "Quantity_{}"
            values = [number(get(row, name.format(k))) for k in range(1, 10)]
        if any(v is not None and v < 0 for v in values):
            raise ExecuteError("ERROR 030000: quantities and capacities cannot be negative.")
        if prefix == "Capacity":
            return tuple(math.inf if v is None else v for v in values)
        return tuple(0.0 if v is None else v for v in values)

    def _specialties(self, inputs, table, key_field, rows_key):
        found = {}
        for row in inputs.get(table, []):
            owner = str(get(row, key_field) or "").lower()
            name = get(row, "SpecialtyName")
            if owner and name:
                found.setdefault(owner, set()).add(str(name))
        return found

    def _read_routes(self, inputs, earliest_date, earliest_time, max_route_total_time):
        lmd = self.kind == "LMD"
        route_specialties = self._specialties(inputs, "RouteSpecialties", "RouteName", "Routes")
        zones = {}
        if lmd:
            for row in inputs.get("Zones", []):
                zones[str(get(row, "Name") or "").lower()] = row.get("point")
        else:
            for row in inputs.get("RouteZones", []):
                zones[str(get(row, "RouteName") or "").lower()] = (row.get("point"),
                                                                   int(number(get(row, "IsHardZone"), 1)) == 1)
        breaks = {}
        for row in inputs.get("Breaks", []):
            breaks.setdefault(str(get(row, "RouteName") or "").lower(), []).append(row)
        renewals = {}
        for row in inputs.get("RouteRenewals", []):
            depot = self.depots.get(str(get(row, "DepotName") or "").lower())
            if depot is None:
                raise ExecuteError(f"ERROR 030000: the route renewal names no depot '{get(row, 'DepotName')}'.")
            renewals.setdefault(str(get(row, "RouteName") or "").lower(), []).append(
                (depot["place"], self._duration(get(row, "ServiceTime")), str(get(row, "DepotName"))))
        self.route_list, self.route_index = [], {}
        for n, row in enumerate(inputs.get("Routes", [])):
            name = get(row, "Name") or f"Route {n + 1}"
            key = str(name).lower()
            if key in self.route_index:
                raise ExecuteError(f"ERROR 030000: the route name '{name}' is not unique.")
            start, end = (str(get(row, f) or "").lower() or None for f in ("StartDepotName", "EndDepotName"))
            for depot in (start, end):
                if depot is not None and depot not in self.depots:
                    raise ExecuteError(f"ERROR 030000: route '{name}' names no depot '{depot}'.")
                if depot is not None and self.depots[depot]["place"] is None:
                    raise ExecuteError(f"ERROR 030024: the depot '{depot}' is not located on the network.")
            if self.kind == "VRP" and start is None and end is None:
                raise ExecuteError(f"ERROR 030000: route '{name}' needs a start or an end depot.")
            if lmd:
                day = get(row, "EarliestStartDate") or earliest_date
                hour = get(row, "EarliestStartTime") or earliest_time
                if day is None or hour is None:
                    raise ExecuteError(f"ERROR 030000: route '{name}' needs an earliest start date and time, on the route "
                                       "or the analysis.")
                day = moment(day, self.default_date)
                if isinstance(hour, datetime.datetime):
                    hour = hour.time()
                earliest = moment(hour, stamp(day).date())
                latest = earliest + self._duration(get(row, "StartFlexibility"))
                limit = get(row, "MaxTotalTime")
                limit = self._duration(limit) if limit is not None else (
                    None if max_route_total_time is None else float(max_route_total_time) * self.per_unit)
                if limit is None:
                    raise ExecuteError(f"ERROR 030000: route '{name}' needs a maximum total time, on the route or the "
                                       "analysis.")
                rule = int(number(get(row, "AssignmentRule"), 0))
                include, closed = rule != 2, rule == 1
                max_orders = number(get(row, "MaxOrderCount"))
                zone = None
                if get(row, "ZoneName") is not None:
                    shape = zones.get(str(get(row, "ZoneName")).lower())
                    if shape is None:
                        raise ExecuteError(f"ERROR 030000: route '{name}' names no zone '{get(row, 'ZoneName')}'.")
                    zone = (shape, int(number(get(row, "IsHardZone"), 1)) == 1)
            else:
                earliest = moment(get(row, "EarliestStartTime"), self.default_date)
                latest = moment(get(row, "LatestStartTime"), self.default_date)
                earliest = moment(datetime.time(8, 0), self.default_date) if earliest is None else earliest
                latest = moment(datetime.time(10, 0), self.default_date) if latest is None else latest
                limit = None if get(row, "MaxTotalTime") is None else self._duration(get(row, "MaxTotalTime"))
                include, closed = int(number(get(row, "AssignmentRule"), 1)) != 2, False
                max_orders = number(get(row, "MaxOrderCount"), 30)
                zone = zones.get(key)
            if latest < earliest:
                raise ExecuteError(f"ERROR 030000: route '{name}' has a latest start before its earliest.")
            specialties = set(str(get(row, "SpecialtyNames") or "").split()) if self.schema == "One" else \
                route_specialties.get(key, set())
            per_time = number(get(row, "CostPerUnitTime"), 1.0)
            overtime = get(row, "OvertimeStartTime")
            route = {"index": n, "name": str(name), "row": row, "key": key,
                     "start": self.depots[start]["place"] if start else None, "start_name": start,
                     "end": self.depots[end]["place"] if end else None, "end_name": end,
                     "start_service": self._duration(get(row, "StartDepotServiceTime")),
                     "end_service": self._duration(get(row, "EndDepotServiceTime")),
                     "earliest": earliest, "latest": latest, "delay": self._duration(get(row, "ArriveDepartDelay")),
                     "capacity": self._quantities(row, "Capacity"), "fixed": number(get(row, "FixedCost"), 0.0),
                     "per_time": per_time / self.per_unit,
                     "per_distance": number(get(row, "CostPerUnitDistance"), 0.0) / self.per_distance_unit,
                     "overtime": None if overtime is None else self._duration(overtime),
                     "per_overtime": number(get(row, "CostPerUnitOvertime"), per_time) / self.per_unit,
                     "max_orders": math.inf if max_orders is None else max_orders, "max_time": limit,
                     "max_travel": None if get(row, "MaxTotalTravelTime") is None else
                     self._duration(get(row, "MaxTotalTravelTime")),
                     "max_distance": None if get(row, "MaxTotalDistance") is None else
                     number(get(row, "MaxTotalDistance")) * self.per_distance_unit,
                     "specialties": specialties, "zone": zone, "include": include, "closed": closed,
                     "breaks": self._read_breaks(breaks.get(key, []), str(name)),
                     "renewals": [r for r in renewals.get(key, []) if r[0] is not None]}
            if (route["max_distance"] is not None or route["per_distance"]) and self.matrix.distance_attribute is None:
                raise ExecuteError("ERROR 030000: MaxTotalDistance and CostPerUnitDistance need a distance attribute.")
            self.route_index[key] = len(self.route_list)
            self.route_list.append(route)

    def _read_breaks(self, rows, route):
        out, kinds = [], set()
        for row in rows:
            if get(row, "Precedence") is None:
                raise ExecuteError(f"ERROR 030000: a break of route '{route}' has no Precedence.")
            ws, we = moment(get(row, "TimeWindowStart"), self.default_date), moment(get(row, "TimeWindowEnd"),
                                                                                     self.default_date)
            travel, work = get(row, "MaxTravelTimeBetweenBreaks"), get(row, "MaxCumulWorkTime")
            if ws is not None or we is not None:
                kind = "window"
                if travel is not None or work is not None:
                    raise ExecuteError("ERROR 030000: a time window break leaves MaxTravelTimeBetweenBreaks and "
                                       "MaxCumulWorkTime null.")
            elif travel is not None:
                kind = "travel"
                if work is not None:
                    raise ExecuteError("ERROR 030000: a break is of one kind only.")
            elif work is not None:
                kind = "work"
            else:
                raise ExecuteError("ERROR 030000: a break needs a time window, a MaxTravelTimeBetweenBreaks or a "
                                   "MaxCumulWorkTime.")
            kinds.add(kind)
            viol = get(row, "MaxViolationTime")
            paid = get(row, "IsPaid")
            out.append({"row": row, "kind": kind, "precedence": int(number(get(row, "Precedence"))),
                        "service": self._duration(get(row, "ServiceTime"), 60.0 * self.per_unit),
                        "start": ws, "end": we, "limit": self._duration(travel if kind == "travel" else work)
                        if kind != "window" else None,
                        "max_violation": None if viol is None else self._duration(viol),
                        "paid": True if paid is None else bool(int(number(paid))) if not isinstance(paid, str) else
                        paid.strip().lower() in ("1", "true", "yes")})
        if len(kinds) > 1:
            raise ExecuteError("ERROR 030000: breaks of different kinds cannot be mixed in one analysis.")
        if len(out) > 5:
            raise ExecuteError(f"ERROR 030000: route '{route}' has more than five breaks.")
        return sorted(out, key=lambda b: b["precedence"])

    def _read_orders(self, rows, inputs, ignore_invalid):
        lmd = self.kind == "LMD"
        order_specialties = self._specialties(inputs, "OrderSpecialties", "OrderName", "Orders")
        self.orders, self.order_index = [], {}
        for n, row in enumerate(rows):
            name = get(row, "Name") or f"Location {n + 1}"
            if str(name).lower() in self.order_index:
                raise ExecuteError(f"ERROR 030000: the order name '{name}' is not unique.")
            delivery, pickup = self._quantities(row, "Delivery"), self._quantities(row, "Pickup")
            if lmd:
                anchor = {1: "first", 2: "last"}.get(int(number(get(row, "AnchorRule"), 0)))
                rule = {1: "sequence", 2: "route", 3: "unassign"}.get(int(number(get(row, "AssignmentRule"), 0)), "free")
            else:
                code = int(number(get(row, "AssignmentRule"), 3))
                rule = {0: "exclude", 1: "sequence", 2: "route", 3: "free", 4: "free", 5: "free"}.get(code)
                if rule is None:
                    raise ExecuteError(f"ERROR 030000: order '{name}' has AssignmentRule {code}, not 0 to 5.")
                anchor = {4: "first", 5: "last"}.get(code)
            route = str(get(row, "RouteName") or "").lower() or None
            if route is not None and route not in self.route_index:
                raise ExecuteError(f"ERROR 030000: order '{name}' names no route '{get(row, 'RouteName')}'.")
            sequence = get(row, "Sequence")
            if sequence is not None and route is None:
                raise ExecuteError(f"ERROR 030000: order '{name}' has a Sequence without a RouteName.")
            specialties = set(str(get(row, "SpecialtyNames") or "").split()) if self.schema == "One" else \
                order_specialties.get(str(name).lower(), set())
            place = self.place_of.get(("Orders", row["oid"]))
            if place is None and not ignore_invalid:
                raise ExecuteError(f"ERROR 030024: the order '{name}' is not located on the network, and invalid "
                                   "locations halt the solve.")
            self.order_index[str(name).lower()] = n
            self.orders.append({"index": n, "name": str(name), "row": row, "place": place, "point": row.get("point"),
                                "service": self._duration(get(row, "ServiceTime")), "windows": self._windows(row),
                                "inbound": moment(get(row, "InboundArriveTime"), self.default_date),
                                "outbound": moment(get(row, "OutboundDepartTime"), self.default_date),
                                "delivery": delivery, "pickup": pickup,
                                "revenue": number(get(row, "Revenue"), 0.0), "rule": rule, "anchor": anchor,
                                "route": route, "sequence": None if sequence is None else int(sequence),
                                "specialties": specialties})
        if lmd:
            deliveries = any(any(o["delivery"]) for o in self.orders)
            pickups = any(any(o["pickup"]) for o in self.orders)
            if deliveries and pickups:
                raise ExecuteError("ERROR 030000: a last mile delivery has delivery orders or pickup orders, not both.")

    def _read_pairs(self, rows):
        self.pairs, self.pair_of = [], {}
        for row in rows:
            names = [str(get(row, f) or "").lower() for f in ("FirstOrderName", "SecondOrderName")]
            if any(n not in self.order_index for n in names):
                raise ExecuteError("ERROR 030000: an order pair names an order that is not there.")
            first, second = (self.order_index[n] for n in names)
            if any(self.orders[first]["delivery"]) or any(self.orders[second]["pickup"]):
                raise ExecuteError("ERROR 030000: an order pair's first order is a pickup and its second a delivery.")
            limit = get(row, "MaxTransitTime")
            pair = (first, second, None if limit is None else self._duration(limit))
            self.pairs.append(pair)
            self.pair_of[first] = self.pair_of[second] = pair

    # -- what the engine reads

    def _assign(self):
        self.fixed, self.preassigned = {r: [] for r in self.routes}, {}
        self.excluded = set()
        for order in self.orders:
            if order["place"] is None or order["rule"] in ("exclude", "unassign"):
                self.excluded.add(order["index"])
                continue
            route = order["route"]
            if order["rule"] in ("sequence", "route") and route is not None:
                r = self.route_index[route]
                if r not in self.fixed:
                    self.excluded.add(order["index"])
                    continue
                self.preassigned[order["index"]] = r
        # Orders that keep their route and relative sequence stay where their sequence puts them.
        for r in self.routes:
            kept = [o for o in self.orders if self.preassigned.get(o["index"]) == r and o["rule"] == "sequence"]
            kept.sort(key=lambda o: (o["sequence"] is None, o["sequence"] or 0, o["index"]))
            self.fixed[r] = [o["index"] for o in kept]
        placed = {i for s in self.fixed.values() for i in s}
        self.groups = []
        for order in self.orders:
            i = order["index"]
            if i in self.excluded or i in placed:
                continue
            pair = self.pair_of.get(i)
            if pair is None:
                self.groups.append((i,))
            elif pair[0] == i:
                if pair[1] in self.excluded or pair[1] in placed:
                    self.excluded.add(i)
                else:
                    self.groups.append((pair[0], pair[1]))
            elif pair[0] in self.excluded or pair[0] in placed:
                self.excluded.add(i)
        # Orders preserved on a route but sequenced by the solver are inserted first, wherever they cost least.
        self.forced = [g for g in self.groups if self.preassigned.get(g[0]) is not None]

    def place_point(self, place):
        return self.points[place]

    def allowed(self, r, i):
        order, route = self.orders[i], self.route_list[r]
        if route["closed"] and self.preassigned.get(i) != r:
            return False
        if i in self.preassigned and self.preassigned[i] != r:
            return False
        if not order["specialties"] <= route["specialties"]:
            return False
        zone = route["zone"]
        if zone is not None and zone[1] and order["point"] is not None and not zone[0].covers(order["point"]):
            return False
        return True

    def start_cost(self, r, i):
        route, place = self.route_list[r], self.orders[i]["place"]
        start = route["start"] if route["start"] is not None else route["end"]
        return self.matrix.time[start][place] if start is not None else 0.0

    def closeness(self, a, b):
        pa, pb = self.orders[a]["place"], self.orders[b]["place"]
        return min(self.matrix.time[pa][pb], self.matrix.time[pb][pa])

    def revenue(self, i):
        return self.orders[i]["revenue"]

    def evaluate(self, r, sequence):
        plan = self.plan(r, sequence)
        return frozenset(plan["codes"]), plan["objective"]

    # -- a route's schedule

    def plan(self, r, sequence, detail=False):
        """The route's schedule for a sequence of orders: when it reaches and leaves each place, its breaks and
        renewals, its totals, costs, objective and the violated constraints (``codes`` route-wide, ``order_codes`` per
        order)."""
        route = self.route_list[r]
        if not sequence:
            return {"codes": set(), "order_codes": {}, "objective": 0.0, "cost": 0.0, "used": False}
        static = set()
        order_codes = {i: set() for i in sequence}
        for i in sequence:
            order = self.orders[i]
            if not order["specialties"] <= route["specialties"]:
                order_codes[i].add(SPECIALTY)
            zone = route["zone"]
            if zone is not None and zone[1] and order["point"] is not None and not zone[0].covers(order["point"]):
                order_codes[i].add(HARD_ZONE)
        if len(sequence) > route["max_orders"]:
            static.add(MAX_ORDER_COUNT)
        firsts = [k for k, i in enumerate(sequence) if self.orders[i]["anchor"] == "first"]
        lasts = [k for k, i in enumerate(sequence) if self.orders[i]["anchor"] == "last"]
        if firsts and firsts != list(range(len(firsts))) or lasts and lasts != list(range(len(sequence) - len(lasts),
                                                                                             len(sequence))):
            for k in firsts + lasts:
                order_codes[sequence[k]].add(ANCHOR)
        inbound = [self.orders[i]["inbound"] for i in sequence if self.orders[i]["inbound"] is not None
                   and any(self.orders[i]["delivery"])]
        start = max([route["earliest"], *inbound])
        depot_start = self.depots[route["start_name"]]["windows"] if route["start_name"] else []
        for s, e, _ in depot_start[:1]:
            if s is not None and start < s:
                start = s
        late_start = start > route["latest"] + 1e-9
        segments = self._segments(route, sequence)
        plan = self._simulate(route, sequence, start, segments, detail)
        first_wait = plan.get("first_wait", 0.0)
        if not late_start and first_wait > 1e-9:
            shift = min(route["latest"] - start, first_wait)
            if shift > 1e-9:
                later = self._simulate(route, sequence, start + shift, segments, detail)
                if later["objective"] <= plan["objective"] + 1e-9:
                    plan = later
        codes = set(plan["codes"]) | static
        if late_start:
            codes.add(INBOUND_OUTBOUND if inbound and max(inbound) > route["latest"] else HARD_WINDOW)
        for i, found in plan["order_codes"].items():
            order_codes[i] |= found
        if segments is None:
            codes.add(CAPACITY)
        for i, found in order_codes.items():
            codes |= found
        plan["codes"], plan["order_codes"] = codes, order_codes
        return plan

    def _load_ok(self, route, orders):
        """Whether the vehicle carries a sequence's load: every delivery loaded at the start, save that of a paired
        order, which its pickup brings; every pickup carried from where it is made."""
        capacity = route["capacity"]
        carried = {self.pair_of[i][1] for i in orders if i in self.pair_of and self.pair_of[i][0] in orders}
        load = [sum(self.orders[i]["delivery"][d] for i in orders if i not in carried) for d in range(9)]
        if any(load[d] > capacity[d] + 1e-9 for d in range(9)):
            return False
        for i in orders:
            order = self.orders[i]
            for d in range(9):
                load[d] += order["pickup"][d] - order["delivery"][d]
                if load[d] > capacity[d] + 1e-9:
                    return False
        return True

    def _segments(self, route, sequence):
        """The sequence cut into loads the vehicle can carry, a renewal between each two (greedily, at the latest point
        and the renewal with the least detour), or None when no cut fits."""
        if self._load_ok(route, sequence):
            return [list(sequence)]
        if not route["renewals"]:
            return None
        segments, current = [], []
        for i in sequence:
            if self._load_ok(route, current + [i]):
                current.append(i)
            elif current and self._load_ok(route, [i]):
                segments.append(current)
                current = [i]
            else:
                return None
        segments.append(current)
        return segments

    def _renewal(self, route, after, before):
        """The renewal (place, service, depot name) with the least detour between two places."""
        def detour(r):
            a = self.matrix.time[after][r[0]] if after is not None else 0.0
            b = self.matrix.time[r[0]][before] if before is not None else 0.0
            return a + b
        return min(route["renewals"], key=lambda r: (detour(r), r[2]))

    def _simulate(self, route, sequence, start, segments, detail):
        m = self.matrix
        codes, order_codes = set(), {}
        visits = []                     # (kind, item, place, arrive, start of service, depart, wait, violation, ...)
        breaks_out = []
        pending = list(route["breaks"])
        time = start
        travel_total = distance_total = wait_total = violation_total = 0.0
        order_service = break_service = renewal_service = unpaid = 0.0
        since_break = 0.0
        last_travel_limit = None        # the limit of the last travel time break taken, which bounds the rest too
        work = route["start_service"]
        first_wait = None
        place = route["start"]
        # Stops: the start depot, the orders with renewals between segments, the end depot.
        stops = []
        for k, segment in enumerate(segments or [list(sequence)]):
            if k:
                previous = self.orders[segments[k - 1][-1]]["place"]
                renewal = self._renewal(route, previous, self.orders[segment[0]]["place"])
                stops.append(("renewal", renewal))
            stops += [("order", i) for i in segment]
        stops.append(("end", None))
        departed_start = time + route["start_service"]
        visits.append({"kind": "start", "place": place, "arrive": time, "start": time, "depart": departed_start,
                       "wait": 0.0, "service": route["start_service"]})
        time = departed_start
        cumulative_travel = cumulative_distance = 0.0
        leg_from = place
        position = 0
        for kind, item in stops:
            if kind == "order":
                target, service = self.orders[item]["place"], self.orders[item]["service"]
            elif kind == "renewal":
                target, service = item[0], item[1]
            else:
                target, service = route["end"], route["end_service"]
            if leg_from is None or target is None:
                leg_time, leg_distance = 0.0, 0.0
            else:
                leg_time, leg_distance = m.time[leg_from][target], m.distance[leg_from][target]
                if not math.isfinite(leg_time):
                    codes.add(UNREACHABLE)
                    if kind == "order":
                        order_codes.setdefault(item, set()).add(UNREACHABLE)
                    return {"codes": codes, "order_codes": order_codes, "objective": BIG, "cost": BIG, "used": True}
                if leg_time > 0 or (leg_distance or 0) > 0:
                    leg_time += route["delay"]
            remaining = leg_time
            # Breaks on the way, by their kind.
            while pending:
                brk = pending[0]
                taken = None
                if brk["kind"] == "window":
                    if time >= (brk["start"] or -math.inf):
                        taken = (time, 0.0)
                    elif kind == "end" or brk["end"] is not None and time + remaining + service > brk["end"]:
                        # Driven toward the next stop until the window opens, waited for at the leg's end if it opens
                        # later (Pro 3.7, measured 2026-10-31): never waited for where the last service ended.
                        taken = (brk["start"], min(remaining, brk["start"] - time))
                elif brk["kind"] == "travel":
                    if since_break + remaining > brk["limit"] + 1e-9:
                        part = max(0.0, brk["limit"] - since_break)
                        taken = (None, part)
                elif brk["kind"] == "work":
                    if work + remaining > brk["limit"] + 1e-9:
                        taken = (None, max(0.0, brk["limit"] - work))
                    elif kind != "end" and work + remaining + service > brk["limit"] + 1e-9:
                        taken = (None, remaining)
                if taken is None:
                    break
                pending.pop(0)
                at, part = taken
                fraction = 0.0
                if part:                                 # taken part-way along the leg
                    travel_total += part
                    cumulative_travel += part
                    work += part
                    since_break += part
                    time += part
                    fraction = part / leg_time if leg_time > 0 else 1.0
                    remaining -= part
                wait = max(0.0, at - time) if at is not None else 0.0     # a window not yet open is waited for
                begin = time + wait
                violation = 0.0
                if brk["kind"] == "window" and brk["end"] is not None and begin > brk["end"]:
                    violation = begin - brk["end"]
                    if brk["max_violation"] is not None and violation > brk["max_violation"] + 1e-9:
                        codes.add(NO_BREAK)
                end = begin + brk["service"]
                breaks_out.append({"break": brk, "after": position, "relative": fraction, "arrive": time, "start": begin,
                                   "depart": end, "wait": wait, "violation": violation, "leg": (leg_from, target),
                                   "cumul_travel": cumulative_travel, "cumul_distance": cumulative_distance +
                                   (leg_distance or 0) * fraction if m.distance_attribute else None,
                                   "from_prev_travel": leg_time * fraction})
                wait_total += wait
                violation_total += violation
                break_service += brk["service"]
                if not brk["paid"]:
                    unpaid += brk["service"]
                else:
                    work += brk["service"]
                since_break = 0.0
                if brk["kind"] == "travel":
                    last_travel_limit = brk["limit"]
                time = end
            if pending and pending[0]["kind"] == "travel" and remaining > pending[0]["limit"] + 1e-9:
                codes.add(BREAK_TRAVEL)
            travel_total += remaining
            cumulative_travel += remaining
            since_break += remaining
            # The final travel time break's limit also bounds the travel from it to the end depot, as its page says.
            if (last_travel_limit is not None and not any(b["kind"] == "travel" for b in pending)
                    and since_break > last_travel_limit + 1e-9):
                codes.add(BREAK_TRAVEL)
            work += remaining
            if m.distance_attribute and leg_distance is not None:
                distance_total += leg_distance
                cumulative_distance += leg_distance
            arrive = time + remaining
            if leg_from is None and kind == "order":
                arrive = time                              # a route with no start depot begins at its first order
            visit = {"kind": kind, "item": item, "place": target, "arrive": arrive, "leg_time": leg_time,
                     "leg_distance": leg_distance if m.distance_attribute else None,
                     "cumul_travel": cumulative_travel, "cumul_distance": cumulative_distance
                     if m.distance_attribute else None, "service": service}
            if kind == "order":
                begin, wait, violation, hard = self._window(self.orders[item]["windows"], arrive)
                if hard:
                    order_codes.setdefault(item, set()).add(HARD_WINDOW)
                if first_wait is None:
                    first_wait = wait
                order_service += service
                position += 1
            elif kind == "end":
                begin, wait, violation = arrive, 0.0, 0.0
                windows = self.depots[route["end_name"]]["windows"] if route["end_name"] else []
                for s, e, _ in windows[:1]:
                    if s is not None and arrive < s:
                        begin, wait = s, s - arrive
                    if e is not None and arrive > e + 1e-9:
                        codes.add(HARD_WINDOW)
                if route["end"] is None:
                    begin, wait, service = time, 0.0, 0.0
                    visit["arrive"] = time
            else:
                begin, wait, violation = arrive, 0.0, 0.0
                renewal_service += service
                position += 1
            visit.update(start=begin, wait=wait, violation=violation, depart=begin + service)
            visits.append(visit)
            wait_total += wait
            violation_total += violation
            work += service
            time = begin + service
            if target is not None:
                leg_from = target
        end_time = time
        if pending and pending[0]["kind"] == "window":
            codes.add(NO_BREAK)
        total_time = end_time - start
        for i in sequence:
            outbound = self.orders[i]["outbound"]
            if outbound is not None and any(self.orders[i]["pickup"]) and end_time > outbound + 1e-9:
                order_codes.setdefault(i, set()).add(INBOUND_OUTBOUND)
        if route["max_time"] is not None and total_time > route["max_time"] + 1e-9:
            codes.add(MAX_TOTAL_TIME)
        if route["max_travel"] is not None and travel_total > route["max_travel"] + 1e-9:
            codes.add(MAX_TRAVEL_TIME)
        if route["max_distance"] is not None and distance_total > route["max_distance"] + 1e-9:
            codes.add(MAX_DISTANCE)
        # Order pairs on this route.
        arrival = {v["item"]: v for v in visits if v["kind"] == "order"}
        excess = 0.0
        for first, second, limit in self.pairs:
            if first in arrival and second in arrival:
                if sequence.index(first) > sequence.index(second):
                    order_codes.setdefault(first, set()).add(PAIR)
                    order_codes.setdefault(second, set()).add(PAIR)
                    continue
                transit = arrival[second]["arrive"] - arrival[first]["depart"]
                if limit is not None and transit > limit + 1e-9:
                    order_codes.setdefault(second, set()).add(MAX_TRANSIT)
                direct = m.time[self.orders[first]["place"]][self.orders[second]["place"]]
                excess += max(0.0, transit - direct)
            elif first in arrival or second in arrival:
                order_codes.setdefault(first if first in arrival else second, set()).add(PAIR)
        paid = total_time - unpaid
        if route["overtime"] is not None:
            regular = route["per_time"] * min(paid, route["overtime"])
            overtime = route["per_overtime"] * max(0.0, paid - route["overtime"])
        else:
            regular, overtime = route["per_time"] * paid, 0.0
        distance_cost = route["per_distance"] * distance_total if m.distance_attribute else 0.0
        cost = route["fixed"] + regular + overtime + distance_cost
        objective = cost + self.window_weight * route["per_time"] * violation_total + \
            self.transit_weight * route["per_time"] * excess + TRAVEL_TIE * travel_total
        zone = route["zone"]
        if zone is not None and not zone[1]:
            factor = (route["per_distance"] if route["per_distance"] else route["per_time"] * self.per_unit /
                      self.per_distance_unit) * self.per_distance_unit
            for i in sequence:
                point = self.orders[i]["point"]
                if point is not None:
                    away = zone[0].distance(point) * self.metres_per_map_unit / self.per_distance_unit
                    objective += factor * away
        return {"codes": codes, "order_codes": order_codes, "objective": objective, "cost": cost, "used": True,
                "visits": visits, "breaks": breaks_out, "start": start, "end": end_time, "total_time": total_time,
                "travel": travel_total, "distance": distance_total if m.distance_attribute else None,
                "wait": wait_total, "violation": violation_total, "order_service": order_service,
                "break_service": break_service, "renewal_service": renewal_service, "regular": regular,
                "overtime": overtime, "distance_cost": distance_cost if m.distance_attribute else None,
                "first_wait": first_wait or 0.0, "segments": segments}

    def _window(self, windows, arrive):
        """(start of service, wait, violation, hard violated) at an order, choosing the window that costs least."""
        if not windows:
            return arrive, 0.0, 0.0, False
        options = []
        for start, end, limit in windows:
            begin = max(arrive, start) if start is not None else arrive
            violation = max(0.0, begin - end) if end is not None else 0.0
            hard = limit is not None and violation > limit + 1e-9
            options.append((hard, violation * self.window_weight + (begin - arrive) * 1e-6, begin, violation))
        hard, _, begin, violation = min(options)
        return begin, begin - arrive, violation, hard

    # -- solving

    def solve(self, iterations=None):
        engine = Engine(self, iterations)
        # Orders kept on a route with the solver's sequence go in first, each where it costs least, feasible or not.
        for group in self.forced:
            r = self.preassigned[group[0]]
            sequence = self.fixed[r]
            options = [(self.plan(r, sequence[:k] + list(group) + sequence[k:])["objective"], k)
                       for k in range(len(sequence) + 1)]
            k = min(options)[1]
            self.fixed[r] = sequence[:k] + list(group) + sequence[k:]
        self.groups = [g for g in self.groups if g not in self.forced]
        solution, unassigned = engine.solve()
        self.solution = solution
        self.unassigned = [i for g in unassigned for i in g]
        return solution

    def why(self, i):
        """The violated constraints that keep an order off every route: the fewest any route's best position gives."""
        best = None
        group = (self.pair_of[i][0], self.pair_of[i][1]) if i in self.pair_of else (i,)
        for r in self.routes:
            sequence = self.solution[r]
            for k in range(len(sequence) + 1):
                trial = sequence[:k] + list(group) + sequence[k:]
                plan = self.plan(r, trial)
                found = set(plan["order_codes"].get(i, set())) | (plan["codes"] - set().union(
                    *[plan["order_codes"].get(j, set()) for j in trial if j != i]))
                if best is None or len(found) < len(best):
                    best = found
        return sorted(best or {UNREACHABLE})


# ---- waste collection -----------------------------------------------------------------------------------------------

class Waste:
    """A waste collection problem: stops grouped into collection units, each served by traversing its street edge."""

    MEASURES = ("Bins_1", "Weight_1", "Volume_1", "Bins_2", "Weight_2", "Volume_2")
    CAPACITIES = ("MaxBins_1", "MaxWeight_1", "MaxVolume_1", "MaxBins_2", "MaxWeight_2", "MaxVolume_2")

    def __init__(self, graph, inputs, time_units="Minutes", distance_units="Miles", route_start_time=None,
                 max_route_total_time=None, stop_collection_mode=None, driving_side="RightSide"):
        from . import _network
        self.graph = graph
        self.per_unit = time_factor(time_units)
        self.distance_word = str(getattr(distance_units, "value", distance_units) or "Miles").upper().replace(" ", "")
        self.per_distance_unit = _network._LINEAR[self.distance_word]
        if inputs.get("RenewalVisits"):
            raise NotBuiltError("arcpy.nax.WasteCollection: preassigned renewal visits (RenewalVisits) are not built in "
                                "grapy; the solver places every renewal visit.")
        default_mode = {"ONESIDE": 1, "ONE_SIDE": 1, "BOTHSIDES": 2, "BOTH_SIDES": 2}.get(
            str(getattr(stop_collection_mode, "name", stop_collection_mode) or "").upper(), None)
        right_hand = str(getattr(driving_side, "name", driving_side)).lower().startswith("right")
        stops, depots, renewals = inputs.get("Stops", []), inputs.get("Depots", []), inputs.get("Renewals", [])
        for row in depots + renewals:
            if row.get("located") is None:
                raise ExecuteError(f"ERROR 030024: '{get(row, 'Name')}' is not located on the network.")
        # Collection units: the stops on one edge, or on one side of it.
        units, unit_of = [], {}
        for row in stops:
            if row.get("located") is None:
                continue
            mode = int(number(get(row, "CollectionMode"), default_mode or 2))
            edge = row["located"][0]
            side = None
            if mode == 1:
                fields = self.graph.location_fields(row["located"], row["point"])
                side = fields["SideOfEdge"]
            key = (edge, side)
            other = [u for u in units if u["edge"] == edge and (u["mode"] != mode)]
            if other:
                raise ExecuteError("ERROR 030000: all stops on a street edge are collected the same way (CollectionMode).")
            if key not in unit_of:
                unit_of[key] = len(units)
                units.append({"edge": edge, "side": side, "mode": mode, "stops": [], "service": 0.0,
                              "load": [0.0] * 6})
            unit = units[unit_of[key]]
            unit["stops"].append(row)
            unit["service"] += number(get(row, "ServiceTime"), 0.0) * self.per_unit
            for k, measure in enumerate(self.MEASURES):
                unit["load"][k] += number(get(row, measure), 0.0)
            row["_unit"] = unit_of[key]
        self.units = units
        # Directions each unit may be traversed in: one side of the street is collected with it on the curb side.
        for unit in units:
            i = unit["edge"]
            if unit["mode"] == 2:
                options = [True, False]
            else:
                curb_is_right = right_hand
                options = [unit["side"] == 1] if curb_is_right else [unit["side"] == 2]
            unit["options"] = [f for f in options if graph.passable(i, f)]
        # Places: depots, renewals and the junctions at each unit's two ends.
        places, self.place_of = [], {}
        for kind, rows in (("Depots", depots), ("Renewals", renewals)):
            for row in rows:
                self.place_of[(kind, row["oid"])] = len(places)
                places.append(((kind, row["oid"]), row["located"][0], row["located"][1]))
        for k, unit in enumerate(units):
            for t in (0.0, 1.0):
                self.place_of[("Unit", k, t)] = len(places)
                places.append((("Unit", k, t), unit["edge"], t))
        self.matrix = Matrix(graph, places)
        self.points = [graph.edges[i]["shape"].interpolate(t, normalized=True) for _, i, t in places]
        self.stop_rows, self.depot_rows = stops, depots
        impedance = graph.impedance
        for unit in units:
            i = unit["edge"]
            unit["traverse"] = {f: graph.cost(impedance, i, f) * self.matrix.per_minute for f in unit["options"]}
            unit["traverse_distance"] = {f: graph.cost(self.matrix.distance_attribute, i, f) * self.matrix.per_metre
                                         if self.matrix.distance_attribute else 0.0 for f in unit["options"]}
        self.depots = {str(get(r, "Name") or "").lower(): self.place_of[("Depots", r["oid"])] for r in depots}
        self.renewal_rows = renewals
        self.renewals = {str(get(r, "Name") or "").lower(): (self.place_of[("Renewals", r["oid"])],
                                                            number(get(r, "Fraction"))) for r in renewals}
        route_renewals = {}
        for row in inputs.get("RouteRenewals", []):
            name = str(get(row, "RenewalName") or "").lower()
            if name not in self.renewals:
                raise ExecuteError(f"ERROR 030000: a route renewal names no renewal '{get(row, 'RenewalName')}'.")
            route_renewals.setdefault(str(get(row, "RouteName") or "").lower(), []).append(
                (*self.renewals[name], number(get(row, "ServiceTime"), 0.0) * self.per_unit, str(get(row, "RenewalName"))))
        today = datetime.date.today()
        self.route_list = []
        starts, ends = set(), set()
        for n, row in enumerate(inputs.get("Routes", [])):
            name = str(get(row, "Name") or f"Route {n + 1}")
            start, end = (str(get(row, f) or "").lower() or None for f in ("StartDepotName", "EndDepotName"))
            for depot in (start, end):
                if depot is not None and depot not in self.depots:
                    raise ExecuteError(f"ERROR 030000: route '{name}' names no depot '{depot}'.")
            starts.add(start)
            ends.add(end)
            begin = get(row, "StartTime") or route_start_time
            if begin is None:
                raise ExecuteError(f"ERROR 030000: route '{name}' needs a start time, on the route or the analysis.")
            if isinstance(begin, datetime.datetime):
                begin = begin.time()
            limit = get(row, "MaxTotalTime")
            limit = number(limit) * self.per_unit if limit is not None else (
                None if max_route_total_time is None else float(max_route_total_time) * self.per_unit)
            if limit is None:
                raise ExecuteError(f"ERROR 030000: route '{name}' needs a maximum total time, on the route or the "
                                   "analysis.")
            capacity = [number(get(row, c), 0.0) for c in self.CAPACITIES]
            rule = int(number(get(row, "AssignmentRule"), 0))
            self.route_list.append({"index": n, "name": name, "key": name.lower(), "row": row,
                                    "start": self.depots[start] if start else None,
                                    "end": self.depots[end] if end else None,
                                    "start_service": number(get(row, "StartDepotServiceTime"), 0.0) * self.per_unit,
                                    "end_service": number(get(row, "EndDepotServiceTime"), 0.0) * self.per_unit,
                                    "begin": moment(begin, today), "max_time": limit,
                                    "max_distance": None if get(row, "MaxTotalDistance") is None else
                                    number(get(row, "MaxTotalDistance")) * self.per_distance_unit,
                                    "overtime": number(get(row, "OvertimeStartTime")),
                                    "capacity": capacity, "include": rule != 2, "closed": rule == 1,
                                    "renewals": route_renewals.get(name.lower(), [])})
        if len(starts) > 1 or len(ends) > 1:
            raise ExecuteError("ERROR 030000: every waste collection route starts and ends at the same depots.")
        self.routes = [r for r, route in enumerate(self.route_list) if route["include"]]
        self.route_index = {route["key"]: r for r, route in enumerate(self.route_list)}
        # Stops preassigned to a route keep it; a unit takes its stops' assignment.
        self.preassigned, self.fixed, self.excluded = {}, {r: [] for r in self.routes}, set()
        kept = {r: [] for r in self.routes}
        for k, unit in enumerate(units):
            names = {str(get(s, "RouteName") or "").lower() for s in unit["stops"] if get(s, "RouteName")}
            rules = {int(number(get(s, "AssignmentRule"), 0)) for s in unit["stops"]}
            if 3 in rules or not unit["options"]:
                self.excluded.add(k)
                continue
            if names and (rules & {1, 2}):
                route = self.route_index.get(sorted(names)[0])
                if route is None or route not in self.fixed:
                    self.excluded.add(k)
                    continue
                self.preassigned[k] = route
                if 1 in rules:
                    sequence = min((number(get(s, "Sequence"), math.inf) for s in unit["stops"]), default=math.inf)
                    kept[route].append((sequence, k))
        for r, items in kept.items():
            self.fixed[r] = [k for _, k in sorted(items)]
        placed = {k for s in self.fixed.values() for k in s}
        self.groups = [(k,) for k in range(len(units)) if k not in self.excluded and k not in placed]
        self.anchor = {}
        for k, unit in enumerate(units):
            rules = {int(number(get(s, "AnchorRule"), 0)) for s in unit["stops"]}
            if 1 in rules:
                self.anchor[k] = "first"
            elif 2 in rules:
                self.anchor[k] = "last"

    # what the engine reads

    def place_point(self, place):
        return self.points[place]

    def allowed(self, r, k):
        route = self.route_list[r]
        if route["closed"] and self.preassigned.get(k) != r:
            return False
        return self.preassigned.get(k, r) == r

    def start_cost(self, r, k):
        start = self.route_list[r]["start"]
        if start is None:
            return 0.0
        return min(self.matrix.time[start][self.place_of[("Unit", k, t)]] for t in (0.0, 1.0))

    def closeness(self, a, b):
        return min(self.matrix.time[self.place_of[("Unit", a, s)]][self.place_of[("Unit", b, t)]]
                   for s in (0.0, 1.0) for t in (0.0, 1.0))

    def revenue(self, k):
        return 0.0

    def evaluate(self, r, sequence):
        plan = self.plan(r, sequence)
        return frozenset(plan["codes"]), plan["objective"]

    def _ends(self, k, forward):
        return (self.place_of[("Unit", k, 0.0)], self.place_of[("Unit", k, 1.0)]) if forward else \
            (self.place_of[("Unit", k, 1.0)], self.place_of[("Unit", k, 0.0)])

    def _directions(self, route, sequence):
        """The cheapest direction of traversal for each unit of a fixed sequence, by dynamic programming."""
        m = self.matrix
        start = route["start"]
        layers = []
        previous = {None: (0.0, None)} if start is None else {start: (0.0, None)}
        for k in sequence:
            unit = self.units[k]
            current = {}
            for forward in unit["options"]:
                entry, exit_ = self._ends(k, forward)
                best = None
                for place, (cost, _) in previous.items():
                    leg = 0.0 if place is None else m.time[place][entry]
                    total = cost + leg + unit["traverse"][forward]
                    if best is None or total < best[0]:
                        best = (total, (place, forward))
                current[exit_] = min(current.get(exit_, (math.inf, None)), best, key=lambda x: x[0])
            layers.append(current)
            previous = current
        end = route["end"]
        best = min(((cost + (m.time[place][end] if end is not None and place is not None else 0.0), place)
                    for place, (cost, _) in previous.items()), default=(0.0, None))
        directions, place = [], best[1]
        for layer, k in zip(reversed(layers), reversed(sequence)):
            _, (place_before, forward) = layer[place]
            directions.append(forward)
            place = place_before
        return list(reversed(directions))

    def activity(self, unit, forward):
        """A collecting RouteLine's Activity: 1 both sides, 2 the left side, 3 the right side of the direction of
        travel."""
        if unit["mode"] == 2:
            return 1
        right_of_travel = (unit["side"] == 1) == forward
        return 3 if right_of_travel else 2

    def _accepts(self, renewal, k):
        fraction = renewal[1]
        load = self.units[k]["load"]
        if fraction is None:
            return True
        return not any(load[3 * (2 - int(fraction)) + j] for j in range(3))

    def plan(self, r, sequence, detail=False):
        route = self.route_list[r]
        if not sequence:
            return {"codes": set(), "order_codes": {}, "objective": 0.0, "used": False}
        codes = set()
        firsts = [n for n, k in enumerate(sequence) if self.anchor.get(k) == "first"]
        lasts = [n for n, k in enumerate(sequence) if self.anchor.get(k) == "last"]
        if firsts and firsts != list(range(len(firsts))) or lasts and lasts != list(range(len(sequence) - len(lasts),
                                                                                             len(sequence))):
            codes.add(ANCHOR)
        m = self.matrix
        directions = self._directions(route, sequence)
        capacity = route["capacity"]
        time = route["begin"] + route["start_service"]
        travel = distance = 0.0
        service = renewal_service = 0.0
        load = [0.0] * 6
        place = route["start"]
        legs = []                    # (activity, item, from place, to place, leg time, leg distance, begin, end, forward)
        renewals = []
        last = ["depot"]

        def go(target):
            nonlocal travel, distance, time
            if place is None or target is None:
                return 0.0, 0.0
            leg, gap = m.time[place][target], m.distance[place][target]
            if not math.isfinite(leg):
                codes.add(UNREACHABLE)
                return 0.0, 0.0
            travel += leg
            distance += gap if m.distance_attribute else 0.0
            time += leg
            return leg, gap

        def renew(before_place, needed=None):
            nonlocal place, time, renewal_service, load
            options = [x for x in route["renewals"] if needed is None or x[1] is None or int(x[1]) in needed]
            if not options:
                codes.add(NO_RENEWAL)
                return
            choice = min(options, key=lambda x: ((m.time[place][x[0]] if place is not None else 0.0) +
                                                 (m.time[x[0]][before_place] if before_place is not None else 0.0), x[3]))
            left = time
            leg, gap = go(choice[0])
            unloaded = list(load)
            if choice[1] is not None:
                keep = int(choice[1])
                unloaded = [v if (j // 3) + 1 == keep else 0.0 for j, v in enumerate(load)]
            renewals.append({"renewal": choice, "from": place, "arrive": time, "depart": time + choice[2],
                             "unloaded": unloaded, "leg": leg, "leg_distance": gap, "left": left})
            legs.append((4, None, place, choice[0], leg, gap, left, time, None))
            time += choice[2]
            renewal_service += choice[2]
            load = [v - u for v, u in zip(load, unloaded)]
            place = choice[0]
            last[0] = "renewal"

        for k, forward in zip(sequence, directions):
            unit = self.units[k]
            entry, exit_ = self._ends(k, forward)
            over = [j for j in range(6) if load[j] + unit["load"][j] > capacity[j] + 1e-9]
            if over:
                if any(unit["load"][j] > capacity[j] + 1e-9 for j in range(6)) or not route["renewals"]:
                    codes.add(CAPACITY)
                else:
                    renew(entry, {j // 3 + 1 for j in over})
            left = time
            leg, gap = go(entry)
            legs.append((5 if last[0] == "unit" else 4, None, place, entry, leg, gap, left, time, None))
            begin = time
            time += unit["traverse"][forward] + unit["service"]
            travel += unit["traverse"][forward]
            distance += unit["traverse_distance"][forward]
            service += unit["service"]
            load = [v + u for v, u in zip(load, unit["load"])]
            legs.append((self.activity(unit, forward), k, entry, exit_, unit["traverse"][forward],
                         unit["traverse_distance"][forward], begin, time, forward))
            place = exit_
            last[0] = "unit"
        if route["renewals"] and any(load):
            renew(route["end"])
        left = time
        leg, gap = go(route["end"])
        legs.append((4, None, place, route["end"], leg, gap, left, time, None))
        end_time = time + route["end_service"]
        total = end_time - route["begin"]
        if total > route["max_time"] + 1e-9:
            codes.add(MAX_TOTAL_TIME)
        if route["max_distance"] is not None and distance > route["max_distance"] + 1e-9:
            codes.add(MAX_DISTANCE)
        objective = ROUTE_WEIGHT + total
        return {"codes": codes, "order_codes": {}, "objective": objective, "used": True, "legs": legs,
                "renewals": renewals, "directions": directions, "total_time": total, "travel": travel,
                "distance": distance if m.distance_attribute else None, "service": service,
                "renewal_service": renewal_service, "end": end_time}

    def solve(self, iterations=None):
        engine = Engine(self, iterations)
        solution, unassigned = engine.solve()
        self.solution = solution
        self.unassigned = [k for g in unassigned for k in g]
        return solution

    def resequence(self, assignment):
        """Each route's units in the best order Or-opt moves find (each unit taken out and put back where it costs
        least, three passes; Or 1976), every unit kept on the route it is
        assigned to — the violations that leaves reported, not avoided (UpdateFleetRoutingResults and
        UpdateFleetRoutingAssignments)."""
        tolerated = {MAX_TOTAL_TIME, MAX_DISTANCE, CAPACITY}

        def score(r, sequence):
            plan = self.plan(r, sequence)
            return plan["objective"] + BIG * len(plan["codes"] - tolerated)

        solution = {r: [] for r in self.routes}
        for r, units in assignment.items():
            best = list(units)
            for _ in range(3):
                for k in list(best):
                    rest = [x for x in best if x != k]
                    best = min((rest[:n] + [k] + rest[n:] for n in range(len(rest) + 1)), key=lambda q: score(r, q))
            solution[r] = best
        self.solution = solution
        assigned = {k for units in assignment.values() for k in units}
        self.unassigned = [k for k in range(len(self.units)) if k not in assigned and k not in self.excluded]
        return solution


# ---- outputs ----------------------------------------------------------------------------------------------------------

_INTERNAL = {"oid", "point", "located", "key", "name", "_lower", "_unit", "SHAPE@", "SHAPE@XY"}


def inputs_of(row):
    """An input row's own field values, without what grapy keeps beside them."""
    return {k: v for k, v in row.items() if k not in _INTERNAL and not str(k).startswith("_")}


def _violations(codes):
    found = sorted(codes)[:4]
    return {f"ViolatedConstraint_{k}": (found[k - 1] if k <= len(found) else None) for k in range(1, 5)}


def _bits(codes):
    return sum(1 << c for c in codes) if codes else None


def _curb(fleet, a, b, arriving, point_side):
    """The side of the vehicle a place is on (1 right, 2 left) as it arrives at (``arriving``) or leaves it, from the
    direction it travels along the edge there; None where the leg has no path."""
    if a is None or b is None or a == b or point_side is None:
        return None
    m = fleet.matrix
    arcs = m.solver.path(m.searches[a], m.places[a][0], m.places[b][0])
    real = [x for x in (arcs or []) if isinstance(x[0], int)]
    if not real:
        return None
    i, t0, t1 = real[-1] if arriving else real[0]
    forward = t1 > t0
    return 1 if (point_side == 1) == forward else 2


def _leg_shape(fleet, a, b, line_shape):
    from shapely.geometry import LineString
    if a is None or b is None or a == b:
        return None
    if line_shape == "STRAIGHT_LINES":
        p, q = fleet.place_point(a), fleet.place_point(b)
        return LineString([(p.x, p.y), (q.x, q.y)]) if p is not None and q is not None else None
    return fleet.matrix.line(a, b)


def _merge(lines):
    import shapely
    from shapely.geometry import LineString
    parts = [line for line in lines if line is not None and not line.is_empty]
    if not parts:
        return None
    coordinates = []
    for line in parts:
        points = list(line.coords)
        coordinates.extend(points if not coordinates or coordinates[-1] != points[0] else points[1:])
    return LineString(coordinates) if len(coordinates) >= 2 else shapely.Point(coordinates[0])


def fleet_outputs(fleet, line_shape="ALONG_NETWORK", schema="Two", sequence_gap=1):
    """The solved problem's output classes, as rows (``SHAPE`` the geometry): for schema "Two" (and the analysis
    layer) Orders, Depots, DepotVisits, Breaks and Routes; for "LMD" the same without breaks; for "One" Stops,
    UnassignedStops and Routes."""
    line_shape = str(line_shape or "ALONG_NETWORK").upper().replace(" ", "_")
    if line_shape in ("TRUESHAPE", "TRUESHAPEWITHMEASURES", "TRUE_LINES_WITH_MEASURES", "TRUE_LINES_WITHOUT_MEASURES"):
        line_shape = "ALONG_NETWORK"
    if line_shape in ("STRAIGHTLINE", "STRAIGHT_LINE"):
        line_shape = "STRAIGHT_LINES"
    if line_shape in ("NOLINE", "NO_LINE", "NONE"):
        line_shape = "NO_LINES"
    u, d = fleet.per_unit, fleet.per_distance_unit
    lmd = schema == "LMD"

    def t(minutes):
        return None if minutes is None else minutes / u

    def dist(metres):
        return None if metres is None else metres / d

    order_rows = {o["index"]: {**inputs_of(o["row"]), "ObjectID": o["row"]["oid"], "RouteName": None,
                               "Sequence": None, "Status": 1 if o["place"] is None else 0, "SHAPE": o["point"]}
                  for o in fleet.orders}
    for o in fleet.orders:
        if o["rule"] == "exclude":
            order_rows[o["index"]]["RouteName"] = o["row"].get("RouteName")
            order_rows[o["index"]]["Sequence"] = o["row"].get("Sequence")
    depot_visits, break_rows, route_rows, stops = [], [], [], []
    for r, route in enumerate(fleet.route_list):
        sequence = fleet.solution.get(r, []) if r in fleet.routes else []
        out = {**inputs_of(route["row"]), "ObjectID": route["row"].get("oid", r + 1), "Name": route["name"],
               "OrderCount": len(sequence), "SHAPE": None}
        if not sequence:
            out.update(_violations(set()))
            out.update({"TotalCost": 0.0 if route["include"] else None, "RenewalCount": 0})
            route_rows.append(out)
            continue
        plan = fleet.plan(r, sequence, detail=True)
        visits, breaks = plan["visits"], plan["breaks"]
        events = []
        count = 0
        for visit in visits:
            if visit["kind"] != "end":
                events.append(("visit", visit))
            if visit["kind"] in ("order", "renewal"):
                count += 1
            if visit["kind"] != "end":
                events += [("break", b) for b in breaks if b["after"] == count]
            else:
                events.append(("visit", visit))
        seq = 0 if lmd else 1
        cumulative_wait = cumulative_violation = 0.0
        legs = []
        previous_place = None
        for kind, event in events:
            if kind == "visit":
                visit = event
                cumulative_wait += visit["wait"]
                cumulative_violation += visit.get("violation", 0.0)
                common = {"FromPrevTravelTime": t(visit.get("leg_time", 0.0)),
                          "FromPrevDistance": dist(visit.get("leg_distance")) if fleet.matrix.distance_attribute else None,
                          "CumulTravelTime": t(visit.get("cumul_travel", 0.0)),
                          "CumulDistance": dist(visit.get("cumul_distance", 0.0)) if fleet.matrix.distance_attribute
                          else None,
                          "CumulTime": t(visit["depart"] - plan["start"]), "ArriveTime": stamp(visit["arrive"]),
                          "DepartTime": stamp(visit["depart"]), "ArriveTimeUTC": None, "DepartTimeUTC": None,
                          "WaitTime": t(visit["wait"]), "CumulWaitTime": t(cumulative_wait),
                          "CumulViolationTime": t(cumulative_violation)}
                if visit["kind"] in ("start", "end", "renewal"):
                    if visit["kind"] == "start":
                        name = route["start_name"]
                    elif visit["kind"] == "end":
                        name = route["end_name"]
                    else:
                        name = visit["item"][2].lower()
                    if visit["kind"] != "start" and visit["place"] is not None and previous_place is not None:
                        legs.append((previous_place, visit["place"]))
                    depot = fleet.depots.get(name) if name else None
                    number_ = seq if not lmd or visit["kind"] != "end" else seq
                    depot_visits.append({"ObjectID": len(depot_visits) + 1,
                                         "DepotName": depot["row"].get("Name") if depot else None,
                                         "RouteName": route["name"], "Sequence": number_,
                                         "VisitType": {"start": 1, "end": 2, "renewal": 3}[visit["kind"]],
                                         "ServiceTime": t(visit["service"]), **common,
                                         "SHAPE": depot["row"].get("point") if depot else None})
                    if visit["place"] is not None:
                        previous_place = visit["place"]
                else:
                    order = fleet.orders[visit["item"]]
                    if previous_place is not None:
                        legs.append((previous_place, visit["place"]))
                    side = get(order["row"], "SideOfEdge")
                    if side is None and order["row"].get("located") is not None:
                        side = fleet.graph.location_fields(order["row"]["located"], order["point"])["SideOfEdge"]
                    following = next((e[1]["place"] for e in events[events.index((kind, event)) + 1:]
                                      if e[0] == "visit" and e[1]["place"] is not None), None)
                    codes = plan["order_codes"].get(visit["item"], set())
                    order_rows[visit["item"]].update({
                        "RouteName": route["name"], "Sequence": seq * (sequence_gap if lmd else 1), **common,
                        "ViolationTime": t(visit["violation"]), **_violations(codes),
                        "Status": 6 if visit["violation"] > 1e-9 else 0,
                        "ArriveCurbApproach": _curb(fleet, previous_place, visit["place"], True, side),
                        "DepartCurbApproach": _curb(fleet, visit["place"], following, False, side)})
                    previous_place = visit["place"]
                seq += 1
            else:
                brk = event
                (a, b), fraction = brk["leg"], brk["relative"]
                shape = None
                if a is not None:
                    leg = _leg_shape(fleet, a, b, "ALONG_NETWORK") if b is not None else None
                    shape = leg.interpolate(fraction, normalized=True) if leg is not None else fleet.place_point(a)
                break_rows.append({**inputs_of(brk["break"]["row"]), "ObjectID": brk["break"]["row"].get("oid"),
                                   "RouteName": route["name"], "Sequence": seq, "RelativePosition": fraction,
                                   "FromPrevTravelTime": t(brk["from_prev_travel"]),
                                   "FromPrevDistance": None, "CumulTravelTime": t(brk["cumul_travel"]),
                                   "CumulDistance": dist(brk["cumul_distance"]),
                                   "CumulTime": t(brk["depart"] - plan["start"]), "ArriveTime": stamp(brk["arrive"]),
                                   "DepartTime": stamp(brk["depart"]), "ArriveTimeUTC": None, "DepartTimeUTC": None,
                                   "WaitTime": t(brk["wait"]), "ViolationTime": t(brk["violation"]),
                                   "CumulWaitTime": t(cumulative_wait + brk["wait"]),
                                   "CumulViolationTime": t(cumulative_violation + brk["violation"]), "SHAPE": shape})
                cumulative_wait += brk["wait"]
                cumulative_violation += brk["violation"]
                seq += 1
        shape = None
        if line_shape != "NO_LINES":
            shape = _merge([_leg_shape(fleet, a, b, line_shape) for a, b in legs])
        out.update(_violations(plan["codes"]))
        out.update({"TotalCost": plan["cost"], "RegularTimeCost": plan["regular"], "OvertimeCost": plan["overtime"],
                    "DistanceCost": plan["distance_cost"], "TotalTime": t(plan["total_time"]),
                    "TotalOrderServiceTime": t(plan["order_service"]), "TotalTravelTime": t(plan["travel"]),
                    "TotalDistance": dist(plan["distance"]), "StartTime": stamp(plan["start"]),
                    "EndTime": stamp(plan["end"]), "TotalWaitTime": t(plan["wait"]),
                    "TotalViolationTime": t(plan["violation"]), "SHAPE": shape})
        out["_legs"] = list(legs)                                     # what the route traversed (ADR-246)
        if not lmd:
            out.update({"TotalBreakServiceTime": t(plan["break_service"]), "StartTimeUTC": None, "EndTimeUTC": None,
                        "RenewalCount": len(plan["segments"] or [1]) - 1,
                        "TotalRenewalServiceTime": t(plan["renewal_service"])})
        route_rows.append(out)
    for i in fleet.unassigned:
        order_rows[i].update(_violations(fleet.why(i)))
    depot_rows = [{**inputs_of(row), "ObjectID": row["oid"], "Status": 0 if row.get("located") else 1,
                   "SHAPE": row.get("point")} for row in fleet.depot_rows]
    if schema == "One":
        for row in order_rows.values():
            if row.get("RouteName") is not None:
                stops.append(row)
        stop_rows = []
        for row in sorted(stops, key=lambda x: (x["RouteName"], x["Sequence"])):
            stop_rows.append({"Name": row.get("Name"), "StopType": 0, "RouteName": row["RouteName"],
                              "Sequence": row["Sequence"], "PickupQuantities": get(row, "PickupQuantities"),
                              "DeliveryQuantities": get(row, "DeliveryQuantities"),
                              **{k: row.get(k) for k in ("FromPrevTravelTime", "FromPrevDistance", "ArriveCurbApproach",
                                                         "DepartCurbApproach", "ArriveTime", "DepartTime",
                                                         "ArriveTimeUTC", "DepartTimeUTC", "WaitTime",
                                                         "ViolationTime")},
                              "ORIG_FID": row["ObjectID"], "SHAPE": row.get("SHAPE")})
        for visit in depot_visits:
            stop_rows.append({"Name": visit["DepotName"], "StopType": 1, "RouteName": visit["RouteName"],
                              "Sequence": visit["Sequence"], **{k: visit.get(k) for k in (
                                  "FromPrevTravelTime", "FromPrevDistance", "ArriveTime", "DepartTime", "ArriveTimeUTC",
                                  "DepartTimeUTC", "WaitTime")}, "ViolationTime": 0.0, "ORIG_FID": None,
                              "SHAPE": visit["SHAPE"]})
        for row in break_rows:
            stop_rows.append({"Name": None, "StopType": 2, "RouteName": row["RouteName"], "Sequence": row["Sequence"],
                              **{k: row.get(k) for k in ("FromPrevTravelTime", "FromPrevDistance", "ArriveTime",
                                                         "DepartTime", "ArriveTimeUTC", "DepartTimeUTC", "WaitTime",
                                                         "ViolationTime")},
                              "ORIG_FID": row["ObjectID"], "SHAPE": row["SHAPE"]})
        stop_rows.sort(key=lambda x: (x["RouteName"], x["Sequence"]))
        for n, row in enumerate(stop_rows, start=1):
            row["ObjectID"] = n
        unassigned = []
        for o in fleet.orders:
            row = order_rows[o["index"]]
            if row.get("RouteName") is None:
                codes = [row.get(f"ViolatedConstraint_{k}") for k in range(1, 5)]
                codes = [c for c in codes if c is not None]
                unassigned.append({"ObjectID": len(unassigned) + 1, "StopType": 0, "Name": o["name"],
                                   "ViolatedConstraints": _bits(codes), **_violations(codes), "Status": row["Status"],
                                   "ORIG_FID": o["row"]["oid"], "SHAPE": o["point"]})
        for row in route_rows:
            codes = [row.get(f"ViolatedConstraint_{k}") for k in range(1, 5)]
            row["ViolatedConstraints"] = _bits([c for c in codes if c is not None])
        return {"Stops": stop_rows, "UnassignedStops": unassigned, "Routes": route_rows}
    for n, row in enumerate(break_rows, start=1):
        row.setdefault("ObjectID", n)
    out = {"Orders": [order_rows[o["index"]] for o in fleet.orders], "Depots": depot_rows, "DepotVisits": depot_visits,
           "Routes": route_rows}
    if lmd:
        for row in depot_visits:
            for key in ("CumulTravelTime", "CumulDistance", "CumulTime", "ArriveTimeUTC", "DepartTimeUTC",
                        "CumulWaitTime", "CumulViolationTime"):
                row.pop(key, None)
        for row in depot_visits:
            if row["VisitType"] == 2:
                orders = sum(1 for o in out["Orders"] if o.get("RouteName") == row["RouteName"])
                row["Sequence"] = (orders + 1) * sequence_gap
    else:
        out["Breaks"] = break_rows
    return out


def waste_outputs(waste, line_shape="ALONG_NETWORK"):
    """A solved waste collection's output classes as rows: Stops, Depots, Renewals, RenewalVisits, Routes, RouteLines.
    Times are times of day, as the page types them."""
    from shapely.geometry import LineString
    u, d = waste.per_unit, waste.per_distance_unit

    def t(minutes):
        return None if minutes is None else minutes / u

    def clock(minutes):
        return None if minutes is None else stamp(minutes).time()

    stop_rows, route_rows, line_rows, visit_rows = [], [], [], []
    stop_out = {}
    for unit in waste.units:
        for row in unit["stops"]:
            stop_out[row["oid"]] = {"RouteName": None, "Sequence": None, "RouteLineID": None, **_violations(set())}
    for r, route in enumerate(waste.route_list):
        sequence = waste.solution.get(r, []) if r in waste.routes else []
        out = {**inputs_of(route["row"]), "ObjectID": route["row"].get("oid", r + 1), "Name": route["name"],
               "StopCount": 0, "RenewalCount": 0, "SHAPE": None}
        if not sequence:
            out.update(_violations(set()))
            route_rows.append(out)
            continue
        plan = waste.plan(r, sequence, detail=True)
        totals = [0.0] * 6
        stop_number = 0
        for n, leg in enumerate(plan["legs"], start=1):
            activity, k, a, b, leg_time, leg_distance, begin, end, forward = leg
            if activity in (1, 2, 3):
                edge = waste.graph.edges[waste.units[k]["edge"]]["shape"]
                shape = edge if forward else LineString(list(edge.coords)[::-1])
            elif line_shape == "STRAIGHT_LINES" and a is not None and b is not None:
                p, q = waste.place_point(a), waste.place_point(b)
                shape = LineString([(p.x, p.y), (q.x, q.y)]) if a != b else None
            else:
                shape = waste.matrix.line(a, b) if a is not None and b is not None and a != b else None
            if activity not in (1, 2, 3) and shape is None:
                continue
            line = {"ObjectID": len(line_rows) + 1, "RouteName": route["name"], "CollectionSequence": None,
                    "Activity": activity, "ArriveTime": clock(begin), "DepartTime": clock(end), "StopCount": None,
                    "ReportGrouping": r + 1, "SHAPE": shape,
                    **{m: None for m in Waste.MEASURES}}
            if activity in (1, 2, 3):
                unit = waste.units[k]
                stops = sorted(unit["stops"], key=lambda s: s["located"][1] if forward else -s["located"][1])
                line["StopCount"] = len(stops)
                for j, measure in enumerate(Waste.MEASURES):
                    line[measure] = unit["load"][j] or None
                    totals[j] += unit["load"][j]
                for row in stops:
                    stop_number += 1
                    stop_out[row["oid"]].update({"RouteName": route["name"], "Sequence": stop_number,
                                                 "RouteLineID": line["ObjectID"]})
            line_rows.append(line)
        for n, line in enumerate([x for x in line_rows if x["RouteName"] == route["name"]], start=1):
            line["CollectionSequence"] = n
        for n, visit in enumerate(plan["renewals"], start=1):
            unloaded = visit["unloaded"]
            visit_rows.append({"ObjectID": len(visit_rows) + 1, "RouteName": route["name"],
                               "RenewalName": visit["renewal"][3], "Sequence": n, "ArriveTime": clock(visit["arrive"]),
                               "DepartTime": clock(visit["depart"]),
                               **{f"Unloaded{m}": (unloaded[j] or 0.0) for j, m in enumerate(Waste.MEASURES)},
                               "SHAPE": waste.place_point(visit["renewal"][0])})
        out.update(_violations(plan["codes"]))
        out.update({"StopCount": stop_number, "RenewalCount": len(plan["renewals"]),
                    **{f"Total{m}": totals[j] for j, m in enumerate(Waste.MEASURES)},
                    "TotalTime": t(plan["total_time"]), "TotalTravelTime": t(plan["travel"]),
                    "TotalStopServiceTime": t(plan["service"]),
                    "TotalDistance": None if plan["distance"] is None else plan["distance"] / d,
                    "EndTime": clock(plan["end"]),
                    "SHAPE": _merge([x["SHAPE"] for x in line_rows if x["RouteName"] == route["name"]])})
        route_rows.append(out)
    for unit_index in waste.unassigned:
        for row in waste.units[unit_index]["stops"]:
            stop_out[row["oid"]].update(_violations({UNREACHABLE} if not waste.units[unit_index]["options"]
                                                    else {CAPACITY}))
    for row in waste.stop_rows:
        located = row.get("located") is not None
        stop_rows.append({**inputs_of(row), "ObjectID": row["oid"], **stop_out.get(row["oid"], {}),
                          "Status": 0 if located else 1, "SHAPE": row.get("point")})
    status = [{**inputs_of(row), "ObjectID": row["oid"], "Status": 0 if row.get("located") else 1,
               "SHAPE": row.get("point")} for row in waste.depot_rows]
    renewals = [{**inputs_of(row), "ObjectID": row["oid"], "Status": 0 if row.get("located") else 1,
                 "SHAPE": row.get("point")} for row in waste.renewal_rows]
    return {"Stops": stop_rows, "Depots": status, "Renewals": renewals, "RenewalVisits": visit_rows,
            "Routes": route_rows, "RouteLines": line_rows}
