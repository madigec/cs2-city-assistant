"""Layout plans for the CityAssistant MCP server (Phase 4.6).

Proposes where to zone (residential, commercial, industrial, office) and where a new service building
helps most (healthcare, fire, police, park, post, education, shelter, welfare), from the map exports:
zoning cells and owned map tiles (map-zones), pollution grids, road traffic and coverage, homes, demand.
The player decides and builds; these are proposals with their reasons.

What comes from the game and what is our own planning rule is labelled in each result:
- zoning cells, owned tiles, coverage, traffic flow, pollution: the game's own numbers;
- pollution at a home costs min(max, pollution / divisor) happiness points per kind (game code
  CitizenHappinessSystem; divisor and caps exported from the game's data);
- a service building covers a road at road distance d with magnitude * (1 - t^2),
  t = clamp(max(d / 2R, (d - 0.6R) / 0.6R)) (game code ServiceCoverageSystem + CoverageJobs, with the
  pathfinding cost taken as the road distance - an approximation);
- how the factors are weighed against each other is a planning rule of thumb, not a game formula.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any

import numpy as np
from matplotlib.patches import Patch
from scipy.sparse import coo_matrix
from scipy.spatial import cKDTree

import maps
import networks

ZONE_GOALS = ["residential", "commercial", "industrial", "office"]
SERVICE_GOALS = networks.COVERAGE
LAYERS = {"zoning": "Zoning"}

# Game code Game.Zones.CellFlags (low byte, as exported).
BLOCKED, ROADSIDE, VISIBLE, OCCUPIED = 1, 4, 8, 32
CELL = 8.0                       # metres per zoning cell (ZoneUtils.CELL_SIZE)
KIND_COLOURS = {"residential": "#5fb246", "commercial": "#3c8fd6", "industrial": "#e0b43a", "office": "#9b59c9"}
BUILDING_KINDS = {1: "residential", 2: "commercial", 3: "industrial", 4: "office", 5: "service"}
# Demand bars (growth.json demandBars, the game's demand panel x 100) per zoning goal.
DEMAND_BARS = {"residential": ("residentialLow", "residentialMedium", "residentialHigh"),
               "commercial": ("commercial",), "industrial": ("industrial",), "office": ("office",)}
LOW_DEMAND = 15                  # a bar at or below this: zoning more mostly stays empty (rule of thumb)

STEEP = 0.10                     # ground steeper than 10 % (rise per metre) counts as steep: cliffs, hillsides
SITE_BIN = 160.0                 # metres: free roadside cells are grouped into squares this size
SITE_MIN_CELLS = 12              # a site needs at least this many free cells (~770 m2)
SITE_SPACING = 450.0             # proposals at least this far apart
NEW_AREA = 400.0                 # side of a proposed new neighbourhood (needs new roads)


# ---------- data ----------

@dataclass
class Zones:
    x: np.ndarray                # cell centres
    z: np.ndarray
    zone: np.ndarray             # index into kinds / names
    state: np.ndarray
    kinds: list[str]             # per zone type: none / residential / commercial / industrial / office
    names: list[str]
    colours: list[str | None]
    tiles: np.ndarray            # (n, 5): min x, min z, max x, max z, owned
    all_unlocked: bool
    buildings: np.ndarray        # (n, 4): x, z, lot radius, kind
    happiness: dict[str, Any]
    coverage_types: list[dict[str, Any]]

    @property
    def free(self) -> np.ndarray:
        """Cells a building could still go on: shown, not blocked, no building on them."""
        return ((self.state & VISIBLE) != 0) & ((self.state & (BLOCKED | OCCUPIED)) == 0)

    def kind_of_cells(self) -> np.ndarray:
        return np.array(self.kinds, dtype=object)[self.zone]

    def owned(self, x: np.ndarray, z: np.ndarray) -> np.ndarray:
        """Is each point on a map tile the city owns (True everywhere with 'unlock all map tiles')."""
        x, z = np.asarray(x, float), np.asarray(z, float)
        if self.all_unlocked or not len(self.tiles):
            return np.ones(x.shape, bool)
        result = np.zeros(x.shape, bool)
        for t in self.tiles[self.tiles[:, 4] > 0]:
            result |= (x >= t[0]) & (x <= t[2]) & (z >= t[1]) & (z <= t[3])
        return result


def parse_zones(raw: dict[str, Any] | None) -> Zones | None:
    if not raw or not isinstance(raw.get("blocks"), dict) or "geometry" not in raw["blocks"]:
        return None
    b = raw["blocks"]
    g = maps._floats(b["geometry"]).reshape(-1, 8)
    import base64
    state = np.frombuffer(base64.b64decode(b["cellState"]), dtype=np.uint8)
    zone = np.frombuffer(base64.b64decode(b["cellZone"]), dtype=np.uint8)
    sx, sy = g[:, 6].astype(int), g[:, 7].astype(int)
    counts = sx * sy
    block = np.repeat(np.arange(len(g)), counts)
    local = np.arange(counts.sum()) - np.repeat(np.cumsum(counts) - counts, counts)
    cx, cy = local % sx[block], local // sx[block]
    along = (sy[block] - 2 * cy - 1) * 4.0
    across = (sx[block] - 2 * cx - 1) * 4.0
    x = g[block, 0] + g[block, 2] * along + g[block, 4] * across
    z = g[block, 1] + g[block, 3] * along + g[block, 5] * across
    types = raw.get("zoneTypes") or []
    tiles_json = raw.get("mapTiles") or {}
    tiles = np.array(tiles_json.get("tiles") or [], float).reshape(-1, 5)
    bjson = raw.get("buildings") or {}
    buildings = maps._floats(bjson["values"]).reshape(-1, 4) if "values" in bjson else np.zeros((0, 4), "<f4")
    return Zones(
        x=x, z=z, zone=zone.astype(int), state=state.astype(int),
        kinds=[t.get("kind", "none") for t in types] or ["none"], names=[t.get("name", "?") for t in types] or ["Unzoned"],
        colours=[t.get("colour") for t in types] or [None],
        tiles=tiles, all_unlocked=bool(tiles_json.get("allUnlocked")), buildings=buildings,
        happiness=raw.get("pollutionHappiness") if isinstance(raw.get("pollutionHappiness"), dict) else {},
        coverage_types=raw.get("coverageBuildingTypes") if isinstance(raw.get("coverageBuildingTypes"), list) else [],
    )


def _need_zones(m: maps.MapData) -> Zones:
    z = getattr(m, "zones", None)
    if z is None:
        raise ValueError("No zoning data in this export: it needs the mod version from 2026-10-30 (map-zones). "
                         "Open the city in the game with the new mod, or play past 00:00 once.")
    return z


def grid_at(m: maps.MapData, name: str, channel: int, x: np.ndarray, z: np.ndarray) -> np.ndarray:
    """Grid values at many points (nearest cell)."""
    a = maps.noise_for_display(m) if name == "noisePollution" else m.grid(name, channel)
    size = a.shape[0]
    c = np.clip(((np.asarray(x) + maps.HALF) / maps.MAP_METRES * size).astype(int), 0, size - 1)
    r = np.clip(((np.asarray(z) + maps.HALF) / maps.MAP_METRES * size).astype(int), 0, size - 1)
    return a[r, c]


class _Roads:
    """Road sample points with their road index, for 'nearest road' lookups."""

    def __init__(self, m: maps.MapData):
        pts = maps._bezier_points(m.roads, steps=7) if len(m.roads) else np.zeros((0, 7, 2))
        self.points = pts.reshape(-1, 2)
        self.road = np.repeat(np.arange(len(m.roads)), pts.shape[1])
        self.tree = cKDTree(self.points) if len(self.points) else None
        major = m.road_major if len(m.road_major) == len(m.roads) else np.zeros(len(m.roads), bool)
        hw = np.repeat(major, pts.shape[1])
        self.highway_tree = cKDTree(self.points[hw]) if hw.any() else None
        self.major = major

    def nearest(self, x: np.ndarray, z: np.ndarray, local_only: bool = False) -> tuple[np.ndarray, np.ndarray]:
        """(distance, road index) of the nearest road (optionally leaving out highways)."""
        if local_only and self.major.any():
            keep = ~self.major[self.road]
            tree = cKDTree(self.points[keep])
            d, i = tree.query(np.column_stack([x, z]))
            return d, self.road[keep][i]
        d, i = self.tree.query(np.column_stack([x, z]))
        return d, self.road[i]


# ---------- factors (each 0 = bad .. 1 = good) ----------

def pollution_points(m: maps.MapData, zones: Zones, x: np.ndarray, z: np.ndarray) -> np.ndarray:
    """Happiness points pollution would cost a household living here (air + ground + noise), game formula."""
    h = zones.happiness or {}
    div = float(h.get("pollutionDivisor") or 600)
    cap_ag, cap_n = float(h.get("maxAirOrGroundPenalty") or 50), float(h.get("maxNoisePenalty") or 15)
    total = np.zeros(len(x))
    for grid, cap in (("airPollution", cap_ag), ("groundPollution", cap_ag), ("noisePollution", cap_n)):
        try:
            total += np.minimum(cap, np.floor(np.maximum(grid_at(m, grid, 0, x, z), 0) / div))
        except ValueError:
            pass
    return total


def _coverage_at(m: maps.MapData, roads: _Roads, road_index: np.ndarray, services: list[str]) -> tuple[np.ndarray, list[str]]:
    """Mean coverage (game scale 0..1) of the given services on each point's nearest road; skipped services
    (warm-up, no data) are listed."""
    values, skipped = [], []
    for s in services:
        try:
            c, game_scale = networks.coverage_shown(m, s)
            if not game_scale:
                skipped.append(s)
                continue
            values.append(np.where(c[road_index] >= 0, c[road_index], 0.0))
        except ValueError:
            skipped.append(s)
    return (np.mean(values, axis=0) if values else np.full(len(road_index), np.nan)), skipped


def _traffic_at(m: maps.MapData, road_index: np.ndarray) -> np.ndarray:
    try:
        t = networks.traffic(m)
    except ValueError:
        return np.full(len(road_index), np.nan)
    return np.minimum(t[road_index, 0], t[road_index, 1])


def _households_within(m: maps.MapData, x: np.ndarray, z: np.ndarray, radius: float, downwind: bool = False) -> np.ndarray:
    """Households within the radius; with downwind=True households the wind blows towards count 3x."""
    if not len(m.homes):
        return np.zeros(len(x))
    tree = cKDTree(m.homes[:, :2])
    lists = tree.query_ball_point(np.column_stack([x, z]), radius)
    out = np.zeros(len(x))
    for k, members in enumerate(lists):
        if not members:
            continue
        hh = m.homes[members, 2]
        if downwind:
            try:
                wx, wz = m.value_at("wind", 0, x[k], z[k]), m.value_at("wind", 1, x[k], z[k])
            except ValueError:
                wx = wz = 0.0
            if wx or wz:
                dx, dz = m.homes[members, 0] - x[k], m.homes[members, 1] - z[k]
                cos = (dx * wx + dz * wz) / np.maximum(np.hypot(dx, dz) * math.hypot(wx, wz), 1e-6)
                hh = hh * np.where(cos > 0.5, 3.0, 1.0)
        out[k] = hh.sum()
    return out


def _buildings_within(zones: Zones, x: np.ndarray, z: np.ndarray, radius: float, kind: int) -> np.ndarray:
    sel = zones.buildings[zones.buildings[:, 3] == kind]
    if not len(sel):
        return np.zeros(len(x))
    tree = cKDTree(sel[:, :2])
    return np.array([len(l) for l in tree.query_ball_point(np.column_stack([x, z]), radius)], float)


def slope_at(m: maps.MapData, x: np.ndarray, z: np.ndarray) -> np.ndarray | None:
    """Ground slope (rise per metre) at each point, from the terrain height grid (56 m cells)."""
    try:
        h = m.grid("terrainHeight")
    except ValueError:
        return None
    cell = maps.MAP_METRES / h.shape[0]
    gz, gx = np.gradient(h, cell)
    slope = np.hypot(gx, gz)
    size = h.shape[0]
    c = np.clip(((np.asarray(x) + maps.HALF) / cell).astype(int), 0, size - 1)
    r = np.clip(((np.asarray(z) + maps.HALF) / cell).astype(int), 0, size - 1)
    return slope[r, c]


def game_suitability(m: maps.MapData, zones: Zones, goal: str) -> tuple[np.ndarray, dict[str, np.ndarray]] | None:
    """The game's own suitability (0 bad .. 1 good, the zoning views' road colour) per road for one goal:
    the mean over the city's zone types of that kind, and each zone type's own values. None = old export."""
    suit = (m.network_raw or {}).get("suitability")
    if not isinstance(suit, dict) or "values" not in suit:
        return None
    names = suit.get("zones") or []
    values = maps._floats(suit["values"])
    if not names or values.size != len(m.roads) * len(names) * 2:
        return None
    values = values.reshape(len(m.roads), len(names), 2)
    kind_of = dict(zip(zones.names, zones.kinds))
    mine = [k for k, n in enumerate(names) if kind_of.get(n) == goal]
    if not mine:
        return None
    per_zone = {}
    for k in mine:
        v = values[:, k, :]
        per_zone[names[k]] = np.where((v >= 0).all(axis=1), v.mean(axis=1), np.nan)
    stack = np.column_stack(list(per_zone.values()))
    known = np.isfinite(stack).any(axis=1)
    mean = np.full(len(stack), np.nan)
    mean[known] = np.nanmean(stack[known], axis=1)
    return mean, per_zone


def _relative(v: np.ndarray) -> np.ndarray:
    """0..1 against the better candidates (90th percentile = 1)."""
    top = float(np.nanpercentile(v, 90)) if len(v) and np.isfinite(v).any() else 0.0
    return np.clip(v / top, 0, 1) if top > 0 else np.zeros(len(v))


def pollution_raw(m: maps.MapData, zones: Zones, x: np.ndarray, z: np.ndarray) -> np.ndarray:
    """Pollution at each point in 'happiness points' before the game rounds them down and caps them (for ranking)."""
    div = float((zones.happiness or {}).get("pollutionDivisor") or 600)
    total = np.zeros(len(x))
    for grid in ("airPollution", "groundPollution", "noisePollution"):
        try:
            total += np.maximum(grid_at(m, grid, 0, x, z), 0) / div
        except ValueError:
            pass
    return total


def score_sites(m: maps.MapData, zones: Zones, roads: _Roads, goal: str, x: np.ndarray, z: np.ndarray,
                cells: np.ndarray | None = None) -> tuple[np.ndarray, list[dict[str, Any]], list[str]]:
    """Score candidate places for one zoning goal. Returns score 0..100, per-place facts, notes.
    cells = free cells per place (bigger sites rank higher); None = a full new area."""
    _, road = roads.nearest(x, z, local_only=True)
    flow = _traffic_at(m, road)
    traffic_ok = np.clip(np.nan_to_num(flow, nan=0.6) / 0.8, 0, 1)
    size = np.ones(len(x)) if cells is None else np.clip(np.asarray(cells, float) / 150, 0, 1)
    notes: list[str] = []
    facts: list[dict[str, Any]] = [{} for _ in range(len(x))]
    for k in range(len(x)):
        if np.isfinite(flow[k]):
            facts[k]["trafficFlowOnAccessRoad"] = round(float(flow[k]), 2)

    if goal == "residential":
        pts = pollution_points(m, zones, x, z)
        cov, skipped = _coverage_at(m, roads, road, ["healthcare", "fire", "police", "education"])
        industry = _buildings_within(zones, x, z, 400, 3)
        f = {"pollution": 1 - np.clip(pollution_raw(m, zones, x, z) / 10, 0, 1), "services": np.nan_to_num(cov, nan=0.5),
             "traffic": traffic_ok, "industryNearby": 1 / (1 + industry / 3), "siteSize": size}
        w = {"pollution": 0.3, "services": 0.25, "traffic": 0.2, "industryNearby": 0.1, "siteSize": 0.15}
        for k in range(len(x)):
            facts[k].update({"pollutionHappinessCost": int(pts[k]), "industrialBuildingsWithin400m": int(industry[k])})
            if np.isfinite(cov[k]):
                facts[k]["serviceCoverage"] = round(float(cov[k]), 2)
        if skipped:
            notes.append(f"Coverage not used for: {', '.join(skipped)} (not rebuilt yet after loading, or no data).")
    elif goal == "commercial":
        hh = _households_within(m, x, z, 800)
        shops = _buildings_within(zones, x, z, 800, 2)
        per_shop = hh / (1 + shops)
        f = {"customersPerShop": _relative(per_shop), "customers": _relative(hh), "traffic": traffic_ok, "siteSize": size}
        w = {"customersPerShop": 0.45, "customers": 0.15, "traffic": 0.3, "siteSize": 0.1}
        for k in range(len(x)):
            facts[k].update({"householdsWithin800m": int(hh[k]), "shopsWithin800m": int(shops[k])})
    elif goal == "industrial":
        hh = _households_within(m, x, z, 1000, downwind=True)
        if roads.highway_tree is not None:
            hd, _ = roads.highway_tree.query(np.column_stack([x, z]))
        else:
            hd = np.full(len(x), np.nan)
        f = {"farFromHomes": 1 / (1 + hh / 200), "highwayAccess": 1 - np.clip((np.nan_to_num(hd, nan=1500) - 300) / 2000, 0, 1),
             "traffic": traffic_ok, "siteSize": size}
        w = {"farFromHomes": 0.4, "highwayAccess": 0.25, "traffic": 0.15, "siteSize": 0.2}
        for k in range(len(x)):
            facts[k]["householdsWithin1km (downwind x3)"] = int(hh[k])
            if np.isfinite(hd[k]):
                facts[k]["metresToHighway"] = int(hd[k])
    elif goal == "office":
        hh = _households_within(m, x, z, 1500)
        cov, skipped = _coverage_at(m, roads, road, ["fire", "police"])
        f = {"workersNearby": _relative(hh), "traffic": traffic_ok, "services": np.nan_to_num(cov, nan=0.5), "siteSize": size}
        w = {"workersNearby": 0.35, "traffic": 0.3, "services": 0.25, "siteSize": 0.1}
        for k in range(len(x)):
            facts[k]["householdsWithin1500m"] = int(hh[k])
            if np.isfinite(cov[k]):
                facts[k]["fireAndPoliceCoverage"] = round(float(cov[k]), 2)
        if skipped:
            notes.append(f"Coverage not used for: {', '.join(skipped)}.")
    else:
        raise ValueError(f"Unknown zoning goal '{goal}'")
    score = sum(w[key] * f[key] for key in w) * 100
    for k in range(len(x)):
        facts[k]["factors"] = {key: round(float(f[key][k]), 2) for key in w}
    game = game_suitability(m, zones, goal)
    if game is not None:
        # The game's own rating of each road for this zone (its road colour in the zoning view). It is capped at
        # full green (InfoviewUtils.GetColor: saturate(score / 256), base score 555), so most roads read 1.0 and it
        # cannot rank them: it works as a gate (red/orange roads lose score), our checks rank the green ones.
        # Verified against the game's zoning view on Mega City 2026-10-30.
        mean, per_zone = game
        suit = np.nan_to_num(mean[road], nan=0.0)
        score = score * suit
        for k in range(len(x)):
            facts[k]["gameSuitability"] = round(float(suit[k]), 2)
            facts[k]["gameSuitabilityByZone"] = {n: (round(float(v[road[k]]), 2) if np.isfinite(v[road[k]]) else None) for n, v in per_zone.items()}
        notes.append("Score: our checks (weights: " + ", ".join(f"{key} {int(w[key] * 100)} %" for key in w) + "; our planning rule) "
                     "x the game's suitability (its road colour in the zoning view, 0 = red .. 1 = green). The game's colour is full "
                     "green on most roads, so it only knocks out poor roads; our checks rank the rest.")
    else:
        notes.append("Weights (our planning rule, not a game formula): " + ", ".join(f"{key} {int(w[key] * 100)} %" for key in w) + ".")
    return score, facts, notes


# ---------- zoning plans ----------

def plan_zoning(m: maps.MapData, goal: str, demand: dict[str, Any] | None, top: int = 3, area: str = "") -> dict[str, Any]:
    zones = _need_zones(m)
    roads = _Roads(m)
    x0, x1, z0, z1 = maps.area_bounds(area) if area else (-maps.HALF, maps.HALF, -maps.HALF, maps.HALF)
    kinds = zones.kind_of_cells()
    in_area = (zones.x >= x0) & (zones.x <= x1) & (zones.z >= z0) & (zones.z <= z1)
    free = zones.free & in_area & zones.owned(zones.x, zones.z)
    zoned_empty = free & (kinds == goal)
    unzoned = free & (kinds == "none")
    result: dict[str, Any] = {"goal": goal, "data": m.when}

    # Demand: is the game asking for this at all?
    bars = {k: (demand or {}).get(k) for k in DEMAND_BARS[goal]}
    known = {k: v for k, v in bars.items() if isinstance(v, (int, float))}
    if known:
        result["demand"] = {"bars": known, "meaning": "the game's demand panel, 0-100"}
        if max(known.values()) <= LOW_DEMAND:
            result["demand"]["warning"] = (f"The game wants little {goal} right now (bar {max(known.values())}): new {goal} zoning "
                                           "would mostly stay empty. Fix what holds demand back first (growth.json demandFactors).")
        if goal == "residential":
            top_bar = max(known.values())
            best = [k.replace("residential", "").lower() for k, v in known.items() if v >= top_bar - 5]
            result["demand"]["suggestedDensity"] = best[0] if len(best) == 1 else f"any ({', '.join(best)} all wanted about equally)"

    # Already zoned but still empty: the game builds there first.
    groups = _bin_cells(zones.x[zoned_empty], zones.z[zoned_empty])
    result["alreadyZonedEmpty"] = {
        "cells": int(zoned_empty.sum()), "squareMetres": int(zoned_empty.sum() * CELL * CELL),
        "biggestPatches": [{"where": m.describe(gx, gz), "square": maps.square_of(gx, gz), "cells": n} for gx, gz, n in groups[:3]],
        "meaning": f"land already zoned {goal} with no building yet",
    }

    # Candidate sites: free cells along existing roads that are unzoned or already zoned for this goal.
    cand = unzoned | zoned_empty
    sites = _sites(zones.x[cand], zones.z[cand], np.flatnonzero(cand))
    proposals = []
    if sites:
        cx = np.array([s["x"] for s in sites])
        cz = np.array([s["z"] for s in sites])
        score, facts, notes = score_sites(m, zones, roads, goal, cx, cz, np.array([len(s["cells"]) for s in sites]))
        cell_slope = slope_at(m, zones.x, zones.z)
        if cell_slope is not None:
            heights = grid_at(m, "terrainHeight", 0, zones.x, zones.z)
            for k, site in enumerate(sites):
                steep = float((cell_slope[site["cells"]] > STEEP).mean())
                rise = float(np.ptp(heights[site["cells"]]))
                facts[k]["steepGroundShare"] = round(steep, 2)
                facts[k]["heightDifferenceMetres"] = round(rise, 1)
                score[k] = -1 if steep > 0.5 else score[k] * (1 - steep)
            notes.append(f"Sites with more than half their cells on ground steeper than {int(STEEP * 100)} % are left out; "
                         "others lose score by their steep share (terrain from the game's height map, 56 m cells).")
        result["notes"] = notes
        order = np.argsort(-score)
        for i in order:
            if len(proposals) >= top or score[i] < 0:
                break
            s = sites[i]
            if any(math.hypot(s["x"] - p["position"][0], s["z"] - p["position"][1]) < SITE_SPACING for p in proposals):
                continue
            cells = s["cells"]
            streets = _streets_of(m, roads, zones.x[cells], zones.z[cells])
            proposals.append({
                "rank": len(proposals) + 1, "kind": "zone along existing roads", "score": int(round(score[i])),
                "where": m.describe(s["x"], s["z"]), "streets": streets, "square": maps.square_of(s["x"], s["z"]),
                "position": [round(s["x"]), round(s["z"])],
                "freeCells": len(cells), "squareMetres": int(len(cells) * CELL * CELL),
                "alreadyZoned": int((kinds[cells] == goal).sum()), "toZone": int((kinds[cells] == "none").sum()),
                **facts[i], "_cells": cells,
            })
    result["proposals"] = proposals

    # New neighbourhoods on open land the city owns (would need new roads).
    result["newAreas"] = _new_areas(m, zones, roads, goal, x0, x1, z0, z1, 2)
    if not zones.all_unlocked and len(zones.tiles):
        result["mapTiles"] = {"owned": int(zones.tiles[:, 4].sum()), "total": len(zones.tiles)}
    result["sources"] = ("Zoning cells, owned tiles, pollution, coverage (game colour scale) and traffic flow: game data. "
                         "Pollution cost in happiness points: game formula. Weights between factors: planning rule of thumb.")
    return result


def _bin_cells(x: np.ndarray, z: np.ndarray, size: float = SITE_BIN) -> list[tuple[float, float, int]]:
    if not len(x):
        return []
    keys = np.floor((x + maps.HALF) / size).astype(int) * 100000 + np.floor((z + maps.HALF) / size).astype(int)
    uniq, inv, counts = np.unique(keys, return_inverse=True, return_counts=True)
    out = []
    for k in np.argsort(-counts):
        sel = inv == k
        out.append((float(x[sel].mean()), float(z[sel].mean()), int(counts[k])))
    return out


def _sites(x: np.ndarray, z: np.ndarray, index: np.ndarray) -> list[dict[str, Any]]:
    """Group free cells into ~160 m squares; each square with enough cells is one candidate site."""
    if not len(x):
        return []
    keys = np.floor((x + maps.HALF) / SITE_BIN).astype(int) * 100000 + np.floor((z + maps.HALF) / SITE_BIN).astype(int)
    uniq, inv = np.unique(keys, return_inverse=True)
    sites = []
    for k in range(len(uniq)):
        sel = np.flatnonzero(inv == k)
        if len(sel) < SITE_MIN_CELLS:
            continue
        sites.append({"x": float(x[sel].mean()), "z": float(z[sel].mean()), "cells": index[sel]})
    return sites


def _streets_of(m: maps.MapData, roads: _Roads, x: np.ndarray, z: np.ndarray) -> list[str]:
    _, road = roads.nearest(x, z, local_only=True)
    names = [m.streets[m.road_street[r]] for r in road if r < len(m.road_street) and m.road_street[r] >= 0]
    counts: dict[str, int] = {}
    for n in names:
        counts[n] = counts.get(n, 0) + 1
    return [n for n, _ in sorted(counts.items(), key=lambda kv: -kv[1])][:3]


def _new_areas(m: maps.MapData, zones: Zones, roads: _Roads, goal: str, x0, x1, z0, z1, top: int) -> list[dict[str, Any]]:
    """Open owned land (no roads, buildings, zoning, water; gentle slope) next to the road network."""
    if roads.tree is None:
        return []
    bx0, bx1, bz0, bz1 = m.city_bounds()
    pad = 1500
    step = NEW_AREA / 2
    xs = np.arange(max(x0, bx0 - pad) + NEW_AREA / 2, min(x1, bx1 + pad) - NEW_AREA / 2, step)
    zs = np.arange(max(z0, bz0 - pad) + NEW_AREA / 2, min(z1, bz1 + pad) - NEW_AREA / 2, step)
    if not len(xs) or not len(zs):
        return []
    gx, gz = (a.ravel() for a in np.meshgrid(xs, zs))
    half = NEW_AREA / 2
    ok = np.ones(len(gx), bool)
    for dx in (-half, 0, half):
        for dz in (-half, 0, half):
            ok &= zones.owned(gx + dx, gz + dz)
    # Nothing built inside: no road point, building or zoning cell within the square (circle approximation).
    radius = half * 1.05
    ok &= np.array([len(l) == 0 for l in roads.tree.query_ball_point(np.column_stack([gx, gz]), radius)])
    if len(zones.buildings):
        btree = cKDTree(zones.buildings[:, :2])
        ok &= np.array([len(l) == 0 for l in btree.query_ball_point(np.column_stack([gx, gz]), radius)])
    if len(zones.x):
        ctree = cKDTree(np.column_stack([zones.x, zones.z]))
        ok &= np.array([len(l) == 0 for l in ctree.query_ball_point(np.column_stack([gx, gz]), radius)])
    # Reachable: an existing road within 350 m of the square's edge.
    d, _ = roads.tree.query(np.column_stack([gx, gz]))
    ok &= d <= half + 350
    if not ok.any():
        return []
    gx, gz, d = gx[ok], gz[ok], d[ok]
    # Water and slope from the grids (56 m cells): sample a 5 x 5 pattern over each square.
    offs = np.linspace(-half * 0.9, half * 0.9, 5)
    ox, oz = (a.ravel() for a in np.meshgrid(offs, offs))
    px, pz = (gx[:, None] + ox[None, :]).ravel(), (gz[:, None] + oz[None, :]).ravel()
    keep = np.ones(len(gx), bool)
    try:
        wet = (grid_at(m, "surfaceWater", 0, px, pz) > 0.3).reshape(len(gx), -1)
        keep &= wet.mean(axis=1) == 0
    except ValueError:
        pass
    rise = np.full(len(gx), np.nan)
    try:
        h = grid_at(m, "terrainHeight", 0, px, pz).reshape(len(gx), -1)
        rise = h.max(axis=1) - h.min(axis=1)
        steep = (slope_at(m, px, pz).reshape(len(gx), -1) > STEEP).mean(axis=1)
        keep &= (rise <= 30) & (steep <= 0.2)
    except ValueError:
        pass
    if not keep.any():
        return []
    gx, gz, d, rise = gx[keep], gz[keep], d[keep], rise[keep]
    score, facts, _ = score_sites(m, zones, roads, goal, gx, gz)
    result = []
    for i in np.argsort(-score):
        if len(result) >= top:
            break
        if any(math.hypot(gx[i] - r["position"][0], gz[i] - r["position"][1]) < NEW_AREA * 1.5 for r in result):
            continue
        facts[i].pop("trafficFlowOnAccessRoad", None)
        entry = {"kind": "new neighbourhood (build new roads)", "score": int(round(score[i])), "where": m.describe(float(gx[i]), float(gz[i])),
                 "square": maps.square_of(float(gx[i]), float(gz[i])), "position": [round(float(gx[i])), round(float(gz[i]))],
                 "sizeMetres": int(NEW_AREA), "metresToNearestRoad": int(max(0, d[i] - half)), **facts[i]}
        if np.isfinite(rise[i]):
            entry["heightDifferenceMetres"] = round(float(rise[i]), 1)
        entry["note"] = "Coverage and traffic are taken from the nearest existing road; new roads there start without service coverage."
        result.append(entry)
    return result


# ---------- service plans ----------

SNAP = 12.0                      # metres: road ends this close count as one junction (Mega City: 152 pieces -> 2)


def _road_graph(m: maps.MapData, snap: float = SNAP):
    """Road network as a graph: nodes = road ends (ends within `snap` metres joined: at junctions the
    curves stop a few metres short of each other), edges = roads with their length."""
    from scipy.sparse.csgraph import connected_components
    ends = np.concatenate([m.roads[:, 0], m.roads[:, 3]])
    pairs = cKDTree(ends).query_pairs(snap, output_type="ndarray")
    links = coo_matrix((np.ones(len(pairs)), (pairs[:, 0], pairs[:, 1])), shape=(len(ends), len(ends)))
    _, node = connected_components(links, directed=False)
    start, end = node[:len(m.roads)], node[len(m.roads):]
    pts = maps._bezier_points(m.roads, steps=8)
    length = np.hypot(*np.diff(pts, axis=1).transpose(2, 0, 1)).sum(axis=1)
    n = int(node.max()) + 1 if len(node) else 0
    graph = coo_matrix((np.concatenate([length, length]), (np.concatenate([start, end]), np.concatenate([end, start]))), shape=(n, n)).tocsr()
    return graph, start, end, length


# How well the coverage model reproduced the game's own coverage on Mega City (2026-10-30): share of roads
# where the rebuilt value is within 0.15 of the game's on its colour scale. Parks and post offices run out of
# capacity (not modelled), so their estimates are rough.
MODEL_CHECK = {"healthcare": 95, "fire": 93, "police": 93, "education": 99, "shelter": 98, "welfare": 80, "park": 36, "post": 32}


def people_per_road(m: maps.MapData, households_per_road: np.ndarray) -> np.ndarray:
    """People on each road for the capacity model: the game's own road density x length when the export has
    it (residents + jobs), else residents estimated from households (city population / households)."""
    density = networks._road_array(m, "density", 1)
    if density is not None:
        pts = maps._bezier_points(m.roads, steps=8)
        length = np.hypot(*np.diff(pts, axis=1).transpose(2, 0, 1)).sum(axis=1)
        return density[:, 0] * length
    total = float(households_per_road.sum())
    per_household = float(getattr(m, "people_per_household", 0) or 0) or 2.5
    return households_per_road * per_household if total else households_per_road


def coverage_added(distance: np.ndarray, range_m: float, magnitude: float, cost: np.ndarray | None = None) -> np.ndarray:
    """Coverage one building gives a road (game formula, CoverageJobs): t = max(cost / 2R, (metres - 0.6R) / 0.6R),
    value = magnitude x (1 - t^2). Without a cost the distance stands in for it.
    A road keeps the highest value any building gives it (ApplyCoverageJob), it does not add them up."""
    cost = distance if cost is None else cost
    t = np.clip(np.maximum(cost / (2 * range_m), (distance - 0.6 * range_m) / (0.6 * range_m)), 0, 1)
    return magnitude * (1 - t * t)


# Services whose coverage travels by car (ServiceCoverageSystem.SetupPathfindMethods); the others walk.
DRIVING_SERVICES = ("healthcare", "fire", "police")


class _Reach:
    """How the game spreads one building's coverage (CoverageJobs + PathUtils.CalculateCost(CoverageParameters)):
    the search follows the CHEAPEST route, where a road costs its length x sqrt(people per metre) for driving
    services (car lanes: NetEdgeDensitySystem sends sqrt(max(0.01, density)) to the pathfinder; 0.1 on empty
    roads) and its plain length for walking ones; the metres along that cheapest route are counted too, and the
    search stops expanding past 1.2 x range metres. So coverage detours around crowded blocks, and a dense
    street 2 km away can be 6 km away 'by the cheap route' (verified with a new clinic, Mega City 2026-10-30)."""

    def __init__(self, m: maps.MapData, service: str, start: np.ndarray, end: np.ndarray, length: np.ndarray,
                 major: np.ndarray):
        density = networks._road_array(m, "density", 1)
        driving = service in DRIVING_SERVICES
        self.start, self.end, self.length = start, end, length
        self.cost = length * np.sqrt(np.maximum(0.01, density[:, 0])) if driving and density is not None else length.copy()
        n = int(max(start.max(), end.max())) + 1 if len(start) else 0
        self.adj: list[list[tuple[int, float, float]]] = [[] for _ in range(n)]
        for i in range(len(length)):
            if not driving and major[i]:
                continue  # no footpaths along highways
            self.adj[start[i]].append((int(end[i]), float(self.cost[i]), float(length[i])))
            self.adj[end[i]].append((int(start[i]), float(self.cost[i]), float(length[i])))

    def from_road(self, r: int, range_m: float) -> tuple[np.ndarray, np.ndarray]:
        """(cost, metres) at both ends of every road ([n, 2] each, inf = not reached) for a building on road r."""
        import heapq
        n = len(self.adj)
        cost, dist = np.full(n, np.inf), np.full(n, np.inf)
        heap = []
        for nd in (int(self.start[r]), int(self.end[r])):
            cost[nd], dist[nd] = self.cost[r] / 2, self.length[r] / 2
            heap.append((cost[nd], nd))
        heapq.heapify(heap)
        limit = 1.2 * range_m
        while heap:
            c, u = heapq.heappop(heap)
            if c > cost[u] or dist[u] >= limit:
                continue
            for v, w, metres in self.adj[u]:
                if c + w < cost[v]:
                    cost[v], dist[v] = c + w, dist[u] + metres
                    heapq.heappush(heap, (cost[v], v))
        ends_cost = np.column_stack([cost[self.start], cost[self.end]])
        ends_dist = np.column_stack([dist[self.start], dist[self.end]])
        ends_cost[r], ends_dist[r] = 0.0, 0.0
        return ends_cost, ends_dist

    def coverage(self, r: int, range_m: float, magnitude: float) -> np.ndarray:
        """Coverage per road from one building on road r (mean of the two ends, as the export gives it)."""
        c, d = self.from_road(r, range_m)
        v = coverage_added(np.nan_to_num(d, posinf=1e12), range_m, magnitude, np.nan_to_num(c, posinf=1e12))
        return v.mean(axis=1)


def strength_factor(m: maps.MapData, service: str, roads: "_Roads") -> float:
    """How strong this city's buildings of the service work compared with their listed magnitude (efficiency:
    budget, staff, city bonuses), measured from the game's coverage on each existing building's own road. A road
    next to a stronger building reads higher, so only buildings near the lowest ratio count. 1.0 if unknown."""
    game_name = networks.COVERAGE_GAME_NAMES[networks.COVERAGE.index(service)]
    types = {t["name"]: t for t in m.zones.coverage_types if t.get("service") == game_name and t.get("magnitude")}
    blds = [lm for lm in m.landmarks if lm.get("type") in types]
    if not blds:
        return 1.0
    raw = networks.coverage(m, service)
    _, r = roads.nearest(np.array([b["x"] for b in blds]), np.array([b["z"] for b in blds]))
    ratio = np.array([raw[int(j)] / float(types[b["type"]]["magnitude"]) for j, b in zip(r, blds)])
    ratio = ratio[ratio > 0]
    if not len(ratio):
        return 1.0
    return float(np.clip(np.median(ratio[ratio <= 1.25 * ratio.min()]), 0.3, 2.0))


def coverage_with_capacity(add: np.ndarray, current: np.ndarray, length: np.ndarray,
                           people: np.ndarray, capacity: float, has_coverage: np.ndarray | None = None) -> np.ndarray:
    """New coverage of every road when one building with this capacity is added (game code ApplyCoverageJob):
    roads are served strongest value first; each road the building improves uses up capacity in proportion to
    the improvement x length x sqrt(people per metre) (NetEdgeDensitySystem: residents + jobs per metre);
    as capacity runs out the building's coverage fades: x (1 - (0.99 x used share)^8). A road keeps the
    best value any one building gives it. Roads without coverage (highways) take nothing and use nothing.
    Solved in three passes instead of road by road."""
    if has_coverage is not None:
        add = np.where(has_coverage, add, 0.0)
    if not capacity or capacity <= 0:
        return np.maximum(current, add)
    weight = length * np.sqrt(np.maximum(0.01, people / np.maximum(length, 1.0)))
    order = np.argsort(-add, kind="stable")
    a, c, w = add[order], current[order], weight[order]
    fade = np.ones(len(a))
    for _ in range(3):
        value = a * fade
        gained = np.clip(value - c, 0, None)
        share = np.where(a > 0, np.clip(gained / np.maximum(a, 1e-9), 0, 1), 0)
        used_before = np.concatenate([[0.0], np.cumsum(share * w)[:-1]])
        remaining = capacity - used_before
        fade = np.where(remaining > 0, 1 - (0.99 * np.clip(1 - remaining / capacity, 0, 1)) ** 8, 0.0)
    result = np.empty(len(a))
    result[order] = np.maximum(c, a * fade)
    return result


def _pick_building(zones: Zones, service: str, building: str = "") -> tuple[dict[str, Any] | None, list[dict[str, Any]]]:
    game_name = networks.COVERAGE_GAME_NAMES[networks.COVERAGE.index(service)]
    options = [t for t in zones.coverage_types if t.get("service") == game_name and not t.get("upgrade") and t.get("range")]
    unlocked = [t for t in options if not t.get("locked")]
    if building:
        named = [t for t in options if building.lower() in t["name"].lower()]
        if not named:
            raise ValueError(f"No {service} building type matching '{building}'. Types: {', '.join(t['name'] for t in options)}")
        return named[0], options
    pool = unlocked or options
    if not pool:
        return None, options
    # The smallest unlocked one: the cheapest way to fill gaps (bigger ones are listed as options).
    return min(pool, key=lambda t: (t["range"], t.get("capacity") or 0)), options


def plan_service(m: maps.MapData, service: str, top: int = 3, area: str = "", building: str = "") -> dict[str, Any]:
    zones = _need_zones(m)
    if service not in SERVICE_GOALS:
        raise ValueError(f"Unknown service '{service}'. Use one of: {', '.join(SERVICE_GOALS)}")
    c, game_scale = networks.coverage_shown(m, service)
    if not game_scale:
        raise ValueError("This export has no colour ranges for coverage (older mod version).")
    rng = networks._game_range(m, "coverage", networks.COVERAGE_GAME_NAMES[networks.COVERAGE.index(service)])
    raw = networks.coverage(m, service)
    btype, options = _pick_building(zones, service, building)
    if btype is None:
        raise ValueError(f"The export lists no {service} building types.")
    roads = _Roads(m)
    _, start, end, length = _road_graph(m)
    # Households per road (each home on its nearest local road).
    hh_road = np.zeros(len(m.roads))
    if len(m.homes):
        _, home_road = roads.nearest(m.homes[:, 0], m.homes[:, 1], local_only=True)
        np.add.at(hh_road, home_road, m.homes[:, 2])
    current = np.where(raw >= 0, raw, 0.0)
    shown = lambda v: networks.as_shown(v, rng)  # noqa: E731
    people = people_per_road(m, hh_road)
    capacity = float(btype.get("capacity") or 0)

    x0, x1, z0, z1 = maps.area_bounds(area) if area else (-maps.HALF, maps.HALF, -maps.HALF, maps.HALF)
    mids = maps._bezier_points(m.roads, steps=3)[:, 1]
    cand = np.flatnonzero(~roads.major & (mids[:, 0] >= x0) & (mids[:, 0] <= x1) & (mids[:, 1] >= z0) & (mids[:, 1] <= z1)
                          & zones.owned(mids[:, 0], mids[:, 1]))
    # Only roads with somebody poorly served within reach are worth testing.
    reach = 1.2 * float(btype["range"])
    need = hh_road * (1 - shown(current))
    if not len(cand) or need.sum() <= 0:
        return {"goal": service, "building": btype, "proposals": [], "note": "No households sit on poorly covered roads."}
    tree = cKDTree(mids)
    useful = np.array([need[tree.query_ball_point(mids[i], reach)].sum() > 0 for i in cand])
    cand = cand[useful]
    spread = _Reach(m, service, start, end, length, roads.major)
    strength = strength_factor(m, service, roads)
    magnitude = float(btype["magnitude"]) * strength
    has_coverage = raw >= 0
    # Free space for the lot: free zoning cells within 60 m of the road (a service building can replace
    # empty zoning; built-up land would need bulldozing).
    lot = btype.get("lotCells") or [4, 4]
    free_tree = cKDTree(np.column_stack([zones.x[zones.free], zones.z[zones.free]])) if zones.free.any() else None
    added_cache: dict[int, np.ndarray] = {}

    lot_cells = lot[0] * lot[1]
    room = np.array([len(free_tree.query_ball_point(mids[i], 60)) if free_tree is not None else 0 for i in cand])
    room_of = {int(i): int(r) for i, r in zip(cand, room)}
    proposals = []
    for _ in range(top):
        best, best_room = None, None
        tried = []
        for i in cand:
            if any(math.hypot(mids[i, 0] - p["position"][0], mids[i, 1] - p["position"][1]) < reach * 0.8 for p in proposals):
                continue
            if int(i) not in added_cache:
                added_cache[int(i)] = spread.coverage(int(i), float(btype["range"]), magnitude)
            new = coverage_with_capacity(added_cache[int(i)], current, length, people, capacity, has_coverage)
            gain = (shown(new) - shown(current)) * hh_road
            total = float(gain.sum())
            tried.append((total, int(i)))
            if best is None or total > best[0]:
                best = (total, i, new, gain)
        if best is None or best[0] <= 0:
            break
        total, i, new, gain = best
        # Where the best road has no free land: the best spot within 800 m that has room for the lot.
        if room_of[int(i)] < lot_cells:
            near = [(t, j) for t, j in tried if room_of[j] >= lot_cells and math.hypot(*(mids[j] - mids[i])) <= 800]
            if near:
                t, j = max(near)
                best_room = {"where": m.describe(float(mids[j, 0]), float(mids[j, 1])),
                             "position": [round(float(mids[j, 0])), round(float(mids[j, 1]))],
                             "metresFromBest": int(math.hypot(*(mids[j] - mids[i]))), "freeCellsWithin60m": room_of[j],
                             "shareOfBestBenefit": round(t / total, 2) if total > 0 else 0}
        x, z = (float(v) for v in mids[i])
        helped = gain > 0.05 * hh_road
        before = float(np.average(shown(current)[helped], weights=hh_road[helped])) if helped.any() and hh_road[helped].sum() > 0 else 0.0
        after = float(np.average(shown(new)[helped], weights=hh_road[helped])) if helped.any() and hh_road[helped].sum() > 0 else 0.0
        free_cells = len(free_tree.query_ball_point([x, z], 60)) if free_tree is not None else 0
        poor_now = helped & (shown(current) < 0.5)
        poor_after = helped & (shown(new) < 0.5)
        proposals.append({
            "rank": len(proposals) + 1, "where": m.describe(x, z), "street": m.streets[m.road_street[i]] if m.road_street[i] >= 0 else None,
            "square": maps.square_of(x, z), "position": [round(x), round(z)],
            "householdsHelped": int(hh_road[helped].sum()),
            "theirCoverageNow": round(before, 2), "theirCoverageAfter (estimate)": round(after, 2),
            "householdsInBottomHalfOfScaleNow": int(hh_road[poor_now].sum()),
            "householdsInBottomHalfAfter (estimate)": int(hh_road[poor_after].sum()),
            "freeCellsWithin60m": free_cells,
            "space": "room on empty or unzoned land" if free_cells >= lot_cells else
                     "little free land: you may have to bulldoze" + (", or use spotWithRoom" if best_room else ""),
            **({"spotWithRoom": best_room} if best_room else {}),
        })
        current = new
    existing = [lm for lm in m.landmarks if any(w.lower() in lm.get("type", "").lower() for w in networks.COVERAGE_BUILDINGS.get(service, ()))]
    return {
        "goal": service, "data": m.when,
        "building": {"type": btype["name"], "range": btype["range"], "capacity": btype.get("capacity"), "magnitude": btype["magnitude"],
                     "lotCells": lot, "locked": btype.get("locked"),
                     "reachMetres": int(reach), "why": "the smallest unlocked type; pass building=... for another",
                     "strengthFactor": round(strength, 2),
                     "strengthFactorMeaning": "how strong this city's existing buildings of this service work vs their listed "
                                              "magnitude (measured on their own roads); the new building is assumed the same"},
        "otherTypes": [{"type": t["name"], "range": t["range"], "capacity": t.get("capacity"), "locked": t.get("locked")} for t in options if t is not btype][:8],
        "existingBuildings": len(existing),
        "_existing": [(lm["x"], lm["z"]) for lm in existing],
        "proposals": proposals,
        "scale": "coverage on the game's colour scale: 0 = red, 1 = green",
        "modelCheck": (f"Rebuilding today's {service} coverage from the existing buildings with this model matched the game on "
                       f"{MODEL_CHECK.get(service, '?')} % of roads (Mega City, 2026-10-30)"
                       + ("; capacity runs out fast for this service, so treat the estimates as rough." if MODEL_CHECK.get(service, 0) < 70 else ".")
                       + (" New-building test (Medical Clinic, Mega City 2026-10-30, measured 1 h and 6 h after building): the estimate "
                          "found 46 of the 55 roads that really improved (2 false), average error on changed roads 0.04 on the colour "
                          "scale, and predicted exactly the households left in the bottom half (4,490)." if service == "healthcare" else "")),
        "sources": ("Coverage now, building types and the coverage rules: game data and code (CoverageJobs, ServiceCoverageSystem): "
                    "coverage follows the cheapest route, where crowded roads cost more for driving services (health, fire, police), "
                    "and fades with the metres along that route; a road keeps the best value any one building gives it; capacity "
                    "runs out strongest-first using the game's road density. The strength factor is measured from this city's "
                    "own buildings. Households: game data."),
        "_reach": reach,
    }


# ---------- drawing ----------

def draw_zoning(ax, m: maps.MapData, x0: float, x1: float, z0: float, z1: float, faint: bool = False) -> str:
    """Zoning cells as the game colours them: full colour = built, pale = zoned but empty, grey = free
    unzoned roadside; map tiles the city does not own are shaded. Returns a footnote."""
    zones = getattr(m, "zones", None)
    if zones is None:
        return " No zoning data (older mod version)."
    px = max(CELL, (x1 - x0) / 1400)
    w, h = int(math.ceil((x1 - x0) / px)), int(math.ceil((z1 - z0) / px))
    img = np.zeros((h, w, 4))
    vis = ((zones.state & VISIBLE) != 0) & (zones.x >= x0) & (zones.x < x1) & (zones.z >= z0) & (zones.z < z1)
    cols = ((zones.x[vis] - x0) / px).astype(int)
    rows = ((zones.z[vis] - z0) / px).astype(int)
    zone = zones.zone[vis]
    occupied = (zones.state[vis] & OCCUPIED) != 0
    blocked = (zones.state[vis] & BLOCKED) != 0
    import matplotlib.colors as mcolors
    palette = []
    for k, kind in enumerate(zones.kinds):
        colour = zones.colours[k] or KIND_COLOURS.get(kind, "#bbbbbb")
        palette.append(mcolors.to_rgb(colour) if kind != "none" else mcolors.to_rgb("#9a9a9a"))
    palette = np.array(palette)
    rgb = palette[zone]
    alpha = np.where(np.array(zones.kinds, dtype=object)[zone] == "none", 0.28, np.where(occupied, 0.85, 0.4))
    alpha = np.where(blocked, 0.0, alpha) * (0.55 if faint else 1.0)
    img[rows, cols, :3] = rgb
    img[rows, cols, 3] = alpha
    ax.imshow(img, extent=[x0, x0 + w * px, z0, z0 + h * px], origin="lower", zorder=2.5, interpolation="nearest")
    if not zones.all_unlocked:
        for t in zones.tiles[zones.tiles[:, 4] == 0]:
            if t[2] >= x0 and t[0] <= x1 and t[3] >= z0 and t[1] <= z1:
                ax.add_patch(maps.plt.Rectangle((t[0], t[1]), t[2] - t[0], t[3] - t[1], facecolor="#000000", alpha=0.12,
                                                edgecolor="none", zorder=2.6))
    if not faint:
        handles = [Patch(facecolor=KIND_COLOURS[k], label=k) for k in ZONE_GOALS]
        handles += [Patch(facecolor="#9a9a9a", alpha=0.4, label="free, not zoned")]
        if not zones.all_unlocked:
            handles.append(Patch(facecolor="#000000", alpha=0.15, label="tile not owned"))
        ax.legend(handles=handles, loc="upper right", fontsize=7.5, framealpha=0.85)
    return " Zoning: strong colour = built, pale = zoned but empty, grey = free but not zoned."


def draw_plan(ax, m: maps.MapData, plan: dict[str, Any], x0, x1, z0, z1) -> str:
    """Numbered proposals on the map (outlined cells for zoning, a reach circle for services)."""
    goal = plan["goal"]
    colour = KIND_COLOURS.get(goal, "#c0392b")
    zones = getattr(m, "zones", None)
    for p in plan.get("proposals", []):
        x, z = p["position"]
        cells = p.get("_cells")
        if cells is not None and zones is not None and len(cells):
            ax.scatter(zones.x[cells], zones.z[cells], s=6, marker="s", c=colour, edgecolors="black", linewidths=0.3, zorder=8.5)
        if "_reach" in plan:
            ax.add_patch(maps.plt.Circle((x, z), plan["_reach"], fill=False, edgecolor=colour, linestyle="--", linewidth=1.3, zorder=8.5))
            ax.scatter([x], [z], s=220, marker="P", c=colour, edgecolors="white", linewidths=1.0, zorder=9)
        ax.annotate(str(p["rank"]), (x, z), xytext=(9, 9), textcoords="offset points", fontsize=12, weight="bold", color="white", zorder=10,
                    bbox=dict(boxstyle="circle,pad=0.25", facecolor=colour, edgecolor="black", linewidth=0.8))
    for k, a in enumerate(plan.get("newAreas", []), start=1):
        x, z = a["position"]
        half = a["sizeMetres"] / 2
        ax.add_patch(maps.plt.Rectangle((x - half, z - half), 2 * half, 2 * half, fill=True, facecolor=colour, alpha=0.18,
                                        edgecolor=colour, linewidth=2.0, linestyle="--", zorder=8.4))
        ax.annotate(f"N{k}", (x, z), ha="center", va="center", fontsize=11, weight="bold", color="black", zorder=10,
                    bbox=dict(facecolor="white", alpha=0.85, edgecolor=colour, pad=1.5))
    existing = plan.get("_existing") or []
    if existing:
        ex = np.array(existing)
        ax.scatter(ex[:, 0], ex[:, 1], s=70, marker="P", c="white", edgecolors=colour, linewidths=1.2, zorder=8.6)
    if "_reach" in plan:
        return f" White plus = existing {goal} buildings. Plus = proposed {goal} building, dashed circle = its approximate reach along roads."
    return f" Numbered = proposed {goal} zoning (free cells marked); N = new neighbourhood on open land (needs new roads)."


def public(plan: dict[str, Any]) -> dict[str, Any]:
    """The plan without drawing-only fields."""
    out = {k: v for k, v in plan.items() if not k.startswith("_")}
    out["proposals"] = [{k: v for k, v in p.items() if not k.startswith("_")} for p in plan.get("proposals", [])]
    return out
