"""Network map layers for the CityAssistant MCP server (Phase 4.3-4.5).

Layers drawn on the lines themselves rather than as grids:
  traffic            roads coloured by the game's Traffic Flow value (0 = jammed, 1 = free), width = volume
  coverage-<service> roads coloured by service coverage (healthcare, fire, police, park, post,
                     education, shelter, welfare), with that service's buildings marked
  electricity        power lines and cables by load (flow / capacity), bottlenecks in red, buildings short
                     of power marked, power plants labelled
  water, sewage      pipes by load, buildings short of water / sewage marked, pumps / outlets labelled
Each layer also has a hotspot list (worst places with street names) for map_hotspots.
Source of every formula: game code 1.6.1f1 (NetColorSystem, NetUtils.GetTrafficFlowSpeed, CoverageService).
"""

from __future__ import annotations

import math
from typing import Any

import numpy as np
from matplotlib.collections import LineCollection

import maps

COVERAGE = ["healthcare", "fire", "police", "park", "post", "education", "shelter", "welfare"]
# The same services by their game names (Game.Net.CoverageService), as the colour ranges are keyed.
COVERAGE_GAME_NAMES = ["Healthcare", "FireRescue", "Police", "Park", "PostService", "Education", "EmergencyShelter", "Welfare"]
# Words in service building type names, to mark the buildings that give each coverage.
COVERAGE_BUILDINGS = {
    "healthcare": ("Hospital", "Clinic"), "fire": ("Fire",), "police": ("Police",), "park": ("Park", "Plaza", "Garden"),
    "post": ("Post",), "education": ("School", "College", "University"), "shelter": ("Shelter",), "welfare": ("Welfare",),
}
LAYERS = {"traffic": "Traffic flow", "electricity": "Electricity network", "water": "Fresh water network", "sewage": "Sewage network"}
LAYERS.update({f"coverage-{s}": f"{s.capitalize()} coverage" for s in COVERAGE})
TIMES = ["00-06 h", "06-12 h", "12-18 h", "18-24 h"]

# Flags of ElectricityFlowEdge (game code ElectricityFlowEdgeFlags). The game's Electricity view only
# highlights bottlenecks; "disconnected" is set on many lines that simply carry nothing (train and subway
# tracks, roads without consumers), so it is shown faintly and never reported as a problem on its own.
BOTTLENECK, BEYOND_BOTTLENECK, DISCONNECTED = 4, 8, 16
# Capacities at or above this are the game's "unlimited" placeholder (seen: 1,073,741,824 on roads and pipes).
UNLIMITED = 1e9
# A building counts as short of power or water when it gets less than this share of what it wants
# (tiny gaps, a few units, come and go every update).
SHORT_SHARE = 0.9


# ---------- data ----------

def _road_array(m: maps.MapData, key: str, width: int) -> np.ndarray | None:
    raw = m.network_raw.get(key)
    if not raw:
        return None
    a = maps._floats(raw)
    return a.reshape(-1, width) if a.size == len(m.roads) * width else None


def traffic(m: maps.MapData) -> np.ndarray:
    a = _road_array(m, "traffic", 7)
    if a is None:
        raise ValueError("No traffic data in this export (needs the mod version from 2026-10-29 evening).")
    return a


def coverage(m: maps.MapData, service: str) -> np.ndarray:
    a = _road_array(m, "coverage", 16)
    if a is None:
        raise ValueError("No coverage data in this export (needs the mod version from 2026-10-29 evening).")
    k = COVERAGE.index(service)
    return a[:, 2 * k:2 * k + 2].mean(axis=1)


def _game_range(m: maps.MapData, group: str, key: str) -> tuple[float, float] | None:
    """The red-to-green range the game's view uses for this value (exported by newer mod versions)."""
    ranges = ((m.network_raw_parent or {}).get("colourRanges") or {}).get(group) or {}
    r = ranges.get(key)
    return (float(r[0]), float(r[1])) if r and r[1] != r[0] else None


def as_shown(values: np.ndarray, rng: tuple[float, float] | None) -> np.ndarray:
    """0 = the game's red end, 1 = its green end (InfoviewUtils.GetColor)."""
    if rng is None:
        return values
    return np.clip((values - rng[0]) / (rng[1] - rng[0]), 0, 1)


def coverage_shown(m: maps.MapData, service: str) -> tuple[np.ndarray, bool]:
    """Coverage per road on the game's own scale (0 bad .. 1 good) when the range is known, else raw."""
    c = coverage(m, service)
    # Right after a save loads, the game rebuilds coverage service by service; until then whole services
    # read 0 on (nearly) every road. Seen 2026-10-29: healthcare 0 on 790 of 1,463 roads, shelters and
    # welfare on all of them, before the first unpause. Don't report gaps from that.
    if (m.network_raw_parent or {}).get("warmingUp") and (c[c >= 0] == 0).mean() > 0.3:
        raise ValueError(f"{service.capitalize()} coverage is not rebuilt yet after loading the save (warm-up): "
                         "unpause for a game hour or so, then ask again.")
    rng = _game_range(m, "coverage", COVERAGE_GAME_NAMES[COVERAGE.index(service)])
    return (np.where(c >= 0, as_shown(c, rng), -1.0), rng is not None)


def _lines(part: dict, width: int) -> tuple[np.ndarray, np.ndarray]:
    if not part or "curves" not in part:
        raise ValueError(f"No network data in this export ({(part or {}).get('error', 'missing')}).")
    return maps._floats(part["curves"]).reshape(-1, 4, 2), maps._floats(part["values"]).reshape(-1, width)


def _problems(part: dict, width: int) -> np.ndarray:
    raw = (part or {}).get("problemBuildings")
    return maps._floats(raw).reshape(-1, width) if raw else np.zeros((0, width), "<f4")


# ---------- drawing ----------

def draw(ax, fig, m: maps.MapData, layer: str, x0: float, x1: float, z0: float, z1: float) -> None:
    """Draws the network layer instead of the plain grey roads."""
    lines = maps._bezier_points(m.roads) if len(m.roads) else np.zeros((0, 6, 2))
    keep = _in_view(lines, x0, x1, z0, z1)
    if layer == "traffic":
        t = traffic(m)
        flow = as_shown(np.minimum(t[:, 0], t[:, 1]), _game_range(m, "netStatus", "TrafficFlow"))
        volume = t[:, 6]
        widths = 0.6 + 3.0 * np.sqrt(volume / max(1e-6, float(np.percentile(volume[keep], 98)) if keep.any() else 1.0)).clip(0, 1)
        col = LineCollection(lines[keep], array=flow[keep], cmap="RdYlGn", linewidths=widths[keep], zorder=3)
        col.set_clim(0, 1)
        ax.add_collection(col)
        bar = fig.colorbar(col, ax=ax, fraction=0.035, pad=0.02)
        bar.set_label("Traffic flow (0 = jammed, 1 = free; worse direction). Width = traffic volume")
        return

    if layer.startswith("coverage-"):
        service = layer.split("-", 1)[1]
        c, game_scale = coverage_shown(m, service)
        has = keep & (c >= 0)
        vmax = 1.0 if game_scale else (float(np.percentile(c[has], 98)) if has.any() else 1.0)
        ax.add_collection(LineCollection(lines[keep & ~has], colors="#bbbbbb", linewidths=0.4, zorder=3))
        col = LineCollection(lines[has], array=c[has], cmap="RdYlGn" if game_scale else "viridis", linewidths=1.6, zorder=3)
        col.set_clim(0, max(vmax, 1e-6))
        ax.add_collection(col)
        bar = fig.colorbar(col, ax=ax, fraction=0.035, pad=0.02)
        bar.set_label(f"{service.capitalize()} coverage, game's scale (0 = bad, 1 = good)" if game_scale
                      else f"{service.capitalize()} coverage on each road (game units, higher = better)")
        words = COVERAGE_BUILDINGS[service]
        for lm in m.landmarks:
            if any(w.lower() in lm.get("type", "").lower() for w in words) and x0 <= lm["x"] <= x1 and z0 <= lm["z"] <= z1:
                ax.scatter([lm["x"]], [lm["z"]], s=60, marker="P", c="#d1495b", edgecolors="white", linewidths=0.8, zorder=7)
        return

    # Utilities: grey roads underneath, the network on top.
    ax.add_collection(LineCollection(lines[keep], colors="#9a9a9a", linewidths=0.4, zorder=3))
    if layer == "electricity":
        part = m.utilities.get("electricityNetwork") or {}
        curves, values = _lines(part, 3)
        pts = maps._bezier_points(curves)
        k = _in_view(pts, x0, x1, z0, z1)
        load = np.where(values[:, 0] > 0, values[:, 1] / np.maximum(values[:, 0], 1), 0)
        flags = values[:, 2].astype(int)
        col = LineCollection(pts[k], array=load[k].clip(0, 1), cmap="YlOrBr", linewidths=1.3, zorder=4)
        col.set_clim(0, 1)
        ax.add_collection(col)
        idle = k & ((flags & DISCONNECTED) != 0) & ((flags & (BOTTLENECK | BEYOND_BOTTLENECK)) == 0)
        if idle.any():
            ax.add_collection(LineCollection(pts[idle], colors="#b0b0b0", linewidths=0.8, linestyles="dashed", zorder=4, label=f"no flow ({int(idle.sum())})"))
        for flag, colour, width, label in ((BEYOND_BOTTLENECK, "#f28e2b", 2.2, "beyond a bottleneck"), (BOTTLENECK, "#d62728", 3.0, "bottleneck")):
            sel = k & ((flags & flag) != 0)
            if sel.any():
                ax.add_collection(LineCollection(pts[sel], colors=colour, linewidths=width, zorder=5, label=f"{label} ({int(sel.sum())})"))
        bar = fig.colorbar(col, ax=ax, fraction=0.035, pad=0.02)
        bar.set_label("Load of each line (flow / capacity)")
        problems = _problems(m.utilities.get("electricityBuildings"), 5)
        _mark_problems(ax, problems, x0, x1, z0, z1, lambda p: p[:, 3] < SHORT_SHARE * p[:, 2], "short of power")
        for p in (m.utilities.get("electricityBuildings") or {}).get("producers", []):
            _mark_producer(ax, p, x0, x1, z0, z1, "#f2c14e", f"{p.get('lastProduction', 0)}/{p.get('capacity', 0)}")
        if ax.get_legend_handles_labels()[0]:
            ax.legend(loc="lower right", fontsize=7)
        return

    if layer in ("water", "sewage"):
        part = m.utilities.get("waterNetwork") or {}
        curves, values = _lines(part, 5)
        pts = maps._bezier_points(curves)
        k = _in_view(pts, x0, x1, z0, z1)
        cap, flow = (values[:, 0], values[:, 1]) if layer == "water" else (values[:, 2], values[:, 3])
        what = "water" if layer == "water" else "sewage"
        # Most roads and pipes have the game's "unlimited" capacity, so show how much flows; real limits
        # (capacity below the placeholder) get a red outline when nearly full.
        has = k & (flow > 0)
        top = float(np.percentile(flow[has], 98)) if has.any() else 1.0
        col = LineCollection(pts[has], array=(flow[has] / max(top, 1.0)).clip(0, 1), cmap="Blues" if layer == "water" else "copper_r", linewidths=1.3, zorder=4)
        col.set_clim(0, 1)
        ax.add_collection(col)
        limited = k & (cap > 0) & (cap < UNLIMITED) & (flow >= 0.9 * cap)
        if limited.any():
            ax.add_collection(LineCollection(pts[limited], colors="#d62728", linewidths=2.6, zorder=5, label=f"nearly full pipe ({int(limited.sum())})"))
        bar = fig.colorbar(col, ax=ax, fraction=0.035, pad=0.02)
        bar.set_label(f"{what.capitalize()} flowing through each pipe (share of the busiest)")
        problems = _problems(m.utilities.get("waterBuildings"), 6)
        column = 3 if layer == "water" else 4
        _mark_problems(ax, problems, x0, x1, z0, z1, lambda p: p[:, column] < SHORT_SHARE * p[:, 2], f"short of {layer}")
        key = "pumps" if layer == "water" else "sewageOutlets"
        for p in (m.utilities.get("waterBuildings") or {}).get(key, []):
            done = p.get("lastProduction", p.get("lastProcessed", 0))
            _mark_producer(ax, p, x0, x1, z0, z1, "#4e79a7" if layer == "water" else "#9c755f", f"{done}/{p.get('capacity', 0)}")
        if ax.get_legend_handles_labels()[0]:
            ax.legend(loc="lower right", fontsize=7)
        return
    raise ValueError(f"Unknown network layer '{layer}'")


def _in_view(lines: np.ndarray, x0: float, x1: float, z0: float, z1: float) -> np.ndarray:
    if not len(lines):
        return np.zeros(0, bool)
    return (lines[:, :, 0].max(1) >= x0) & (lines[:, :, 0].min(1) <= x1) & (lines[:, :, 1].max(1) >= z0) & (lines[:, :, 1].min(1) <= z1)


def _mark_problems(ax, problems: np.ndarray, x0, x1, z0, z1, short, label: str) -> None:
    if not len(problems):
        return
    sel = short(problems) & (problems[:, 0] >= x0) & (problems[:, 0] <= x1) & (problems[:, 1] >= z0) & (problems[:, 1] <= z1)
    if sel.any():
        ax.scatter(problems[sel, 0], problems[sel, 1], s=18, marker="x", c="#c00000", linewidths=1.0, zorder=7, label=f"{label} ({int(sel.sum())})")


def _mark_producer(ax, p: dict, x0, x1, z0, z1, colour: str, text: str) -> None:
    if x0 <= p["x"] <= x1 and z0 <= p["z"] <= z1:
        ax.scatter([p["x"]], [p["z"]], s=70, marker="s", c=colour, edgecolors="black", linewidths=0.6, zorder=7)
        ax.annotate(f"{maps._short(p.get('type', ''))} {text}", (p["x"], p["z"]), xytext=(5, 5), textcoords="offset points", fontsize=6.5, zorder=8,
                    bbox=dict(facecolor="white", alpha=0.7, edgecolor="none", pad=0.4))


# ---------- hotspots ----------

def hotspots(m: maps.MapData, layer: str, top: int = 10, area: str = "") -> list[dict[str, Any]]:
    x0, x1, z0, z1 = maps.area_bounds(area) if area else (-maps.HALF, maps.HALF, -maps.HALF, maps.HALF)
    mids = maps._bezier_points(m.roads, steps=3)[:, 1] if len(m.roads) else np.zeros((0, 2))
    in_area = (mids[:, 0] >= x0) & (mids[:, 0] <= x1) & (mids[:, 1] >= z0) & (mids[:, 1] <= z1) if len(mids) else np.zeros(0, bool)

    if layer == "traffic":
        t = traffic(m)
        flow = np.minimum(t[:, 0], t[:, 1])
        volume = t[:, 6]
        score = (1 - flow) * np.sqrt(np.maximum(volume, 0))
        score[~in_area] = -1
        result, seen = [], set()
        for i in np.argsort(-score):
            if score[i] <= 0 or len(result) >= top:
                break
            street = m.streets[m.road_street[i]] if m.road_street[i] >= 0 else None
            x, z = (float(v) for v in mids[i])
            if any(math.hypot(x - r["position"][0], z - r["position"][1]) < 300 for r in result):
                continue
            worst = int(np.argmin(t[i, 2:6]))
            result.append({
                "where": m.describe(x, z), "street": street, "square": maps.square_of(x, z), "position": [round(x), round(z)],
                "flow": round(float(flow[i]), 2), "flowForward": round(float(t[i, 0]), 2), "flowBackward": round(float(t[i, 1]), 2),
                "worstTime": TIMES[worst], "speedShareAtWorstTime": round(float(t[i, 2 + worst]), 2), "volume": round(float(volume[i])),
            })
            seen.add(street)
        return result

    if layer.startswith("coverage-"):
        service = layer.split("-", 1)[1]
        c, game_scale = coverage_shown(m, service)
        if not len(m.homes):
            return []
        # Each home takes the coverage of its nearest road; the gaps are where many households sit on poorly covered roads.
        valid = c >= 0
        rx, rz = mids[valid, 0], mids[valid, 1]
        rc = c[valid]
        home_cov = np.array([rc[int(np.argmin(np.hypot(rx - hx, rz - hz)))] for hx, hz, _ in m.homes]) if len(rc) else np.zeros(len(m.homes))
        # "Good" is the game's green end when its scale is known, else what the better-covered homes get.
        good = 1.0 if game_scale else (float(np.percentile(home_cov, 75)) if len(home_cov) else 0.0)
        cells: dict[tuple[int, int], list[int]] = {}
        for j, (hx, hz, _) in enumerate(m.homes):
            if x0 <= hx <= x1 and z0 <= hz <= z1:
                cells.setdefault((int((hx + maps.HALF) // 400), int((hz + maps.HALF) // 400)), []).append(j)
        ranked = []
        for members in cells.values():
            households = float(m.homes[members, 2].sum())
            cov = float(np.average(home_cov[members], weights=np.maximum(m.homes[members, 2], 1)))
            ranked.append((households * max(0.0, 1 - cov / max(good, 1e-6)), members, households, cov))
        ranked.sort(key=lambda r: -r[0])
        result = []
        for score, members, households, cov in ranked[:top]:
            if score <= 0:
                break
            x, z = (float(v) for v in m.homes[members, :2].mean(axis=0))
            entry = {"where": m.describe(x, z), "square": maps.square_of(x, z), "position": [round(x), round(z)],
                     "households": int(households), "coverage": round(cov, 3)}
            entry.update({"scale": "game's colour scale, 0 = red, 1 = green"} if game_scale else {"cityTypicalCoverage": round(good, 3)})
            result.append(entry)
        return result

    if layer == "electricity":
        curves, values = _lines(m.utilities.get("electricityNetwork") or {}, 3)
        flags = values[:, 2].astype(int)
        mid = maps._bezier_points(curves, steps=3)[:, 1]
        result = []
        for flag, label in ((BOTTLENECK, "bottleneck"), (BEYOND_BOTTLENECK, "beyond a bottleneck")):
            for i in np.flatnonzero((flags & flag) != 0):
                x, z = (float(v) for v in mid[i])
                if not (x0 <= x <= x1 and z0 <= z <= z1) or any(math.hypot(x - r["position"][0], z - r["position"][1]) < 300 for r in result):
                    continue
                result.append({"problem": label, "where": m.describe(x, z), "square": maps.square_of(x, z), "position": [round(x), round(z)],
                               "capacity": int(values[i, 0]), "flow": int(values[i, 1])})
                if len(result) >= top:
                    return result
        return result + _shortage_clusters(m, _problems(m.utilities.get("electricityBuildings"), 5), 3, "short of power",
                                           x0, x1, z0, z1, top - len(result))

    if layer in ("water", "sewage"):
        column = 3 if layer == "water" else 4
        return _shortage_clusters(m, _problems(m.utilities.get("waterBuildings"), 6), column, f"short of {layer}", x0, x1, z0, z1, top)
    raise ValueError(f"Unknown network layer '{layer}'")


def _shortage_clusters(m, problems: np.ndarray, column: int, label: str, x0, x1, z0, z1, top: int) -> list[dict[str, Any]]:
    """Places where buildings get less than they want (column = the 'fulfilled' column), grouped in 400 m cells."""
    if top <= 0 or not len(problems):
        return []
    sel = (problems[:, column] < SHORT_SHARE * problems[:, 2]) & (problems[:, 0] >= x0) & (problems[:, 0] <= x1) & (problems[:, 1] >= z0) & (problems[:, 1] <= z1)
    p = problems[sel]
    cells: dict[tuple[int, int], list[int]] = {}
    for j, (x, z) in enumerate(p[:, :2]):
        cells.setdefault((int((x + maps.HALF) // 400), int((z + maps.HALF) // 400)), []).append(j)
    result = []
    for members in sorted(cells.values(), key=len, reverse=True)[:top]:
        x, z = (float(v) for v in p[members, :2].mean(axis=0))
        result.append({"problem": label, "buildings": len(members), "where": m.describe(x, z), "square": maps.square_of(x, z),
                       "position": [round(x), round(z)], "missing": round(float((p[members, 2] - p[members, column]).sum()))})
    return result
