"""Map drawing for the CityAssistant MCP server (Phase 4).

Turns the mod's map exports (map-grids, map-network, map-buildings) into annotated PNG maps and
hotspot lists. Coordinates are the game's world metres: x west->east, z south->north, map centre (0, 0),
the whole map from -7168 to +7168. The lettered grid on the maps follows the game's 23 x 23 map tiles
(columns A-W west to east, rows 1-23 north to south), but the game never shows those labels: tell the
player places by street names, district and landmarks (describe()), which the game does show.
"""

from __future__ import annotations

import base64
import io
import math
import textwrap
from dataclasses import dataclass, field
from typing import Any

import matplotlib

matplotlib.use("Agg")  # no screen needed
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
from matplotlib.collections import LineCollection  # noqa: E402
from matplotlib.path import Path as PolygonPath  # noqa: E402

MAP_METRES = 14336.0
HALF = MAP_METRES / 2
TILES = 23
TILE = MAP_METRES / TILES
LETTERS = "ABCDEFGHIJKLMNOPQRSTUVW"

# layer name -> (grid, channel, colour map, title, which pollution the sources give off)
LAYERS: dict[str, tuple[str | None, int, str, str, str | None]] = {
    "base": (None, 0, "", "Base map", None),
    "air": ("airPollution", 0, "YlOrRd", "Air pollution", "air"),
    "ground": ("groundPollution", 0, "YlOrBr", "Ground pollution", "ground"),
    "noise": ("noisePollution", 0, "RdPu", "Noise pollution", "noise"),
    "water-pollution": ("surfaceWater", 1, "YlGn", "Surface water pollution", None),
    "groundwater": ("groundWater", 0, "Blues", "Groundwater amount", None),
    "groundwater-pollution": ("groundWater", 1, "YlOrBr", "Groundwater pollution", None),
    "land-value": ("landValue", 0, "viridis", "Land value (raw game value)", None),
    "population": ("population", 0, "Purples", "Residents per cell", None),
    "wind": ("wind", 0, "", "Wind", None),
}
SOURCE_CHANNEL = {"air": 2, "ground": 3, "noise": 4}

# Game noise overflow. BuildingPollutionAddSystem.AddSingle adds a building's noise share for each grid cell
# as a 16-bit number without a cap: a share above 32,767 wraps to a negative number, and the game's 3x3
# smoothing (NoisePollutionSystem) then shows the loudest buildings as quiet or even negative spots. The game
# shows the same wrong values. Settings: PollutionPrefab code defaults (asset values not checked).
NOISE_MULTIPLIER = 100.0
NOISE_RADIUS = 200.0
POLLUTION_MAX_RADIUS = 200.0     # largest of the ground/air/noise radii, scales the game's distance weights
NOISE_UPDATES_PER_DAY = 128
# Share at which a building counts as overflowing. With the defaults above, the incinerators that show
# negative noise in Mega City come out at 30,000-32,800, so the real settings run a bit higher than the
# defaults; 27,000 also catches the cargo harbours and signature factories that show holes.
NOISE_OVERFLOW_SHARE = 27000


# ---------- places ----------

def square_of(x: float, z: float) -> str:
    """The map tile a point lies in, e.g. 'C5'."""
    col = int(min(TILES - 1, max(0, (x + HALF) // TILE)))
    row = int(min(TILES - 1, max(0, (HALF - z) // TILE)))
    return f"{LETTERS[col]}{row + 1}"


def square_bounds(label: str) -> tuple[float, float, float, float]:
    """(x0, x1, z0, z1) of a map tile label such as 'C5'."""
    label = label.strip().upper()
    if not label or label[0] not in LETTERS or not label[1:].isdigit() or not 1 <= int(label[1:]) <= TILES:
        raise ValueError(f"'{label}' is not a map tile: use a letter A-W and a number 1-{TILES}, e.g. C5")
    col, row = LETTERS.index(label[0]), int(label[1:]) - 1
    x0 = -HALF + col * TILE
    z1 = HALF - row * TILE
    return x0, x0 + TILE, z1 - TILE, z1


def area_bounds(area: str) -> tuple[float, float, float, float]:
    """'C3:F6' (two corner tiles) or 'D4' (one tile) -> (x0, x1, z0, z1)."""
    parts = [p for p in area.replace(" ", "").split(":") if p]
    boxes = [square_bounds(p) for p in parts]
    return (min(b[0] for b in boxes), max(b[1] for b in boxes), min(b[2] for b in boxes), max(b[3] for b in boxes))


def compass(dx: float, dz: float) -> str:
    """Direction a vector points to, as a compass word."""
    if abs(dx) < 1e-6 and abs(dz) < 1e-6:
        return "calm"
    names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"]
    angle = (math.degrees(math.atan2(dx, dz)) + 360) % 360
    return names[int((angle + 22.5) // 45) % 8]


# ---------- data ----------

def _floats(b64: str) -> np.ndarray:
    return np.frombuffer(base64.b64decode(b64), dtype="<f4")


@dataclass
class MapData:
    city: str
    when: str
    grids: dict[str, Any]
    roads: np.ndarray            # (n, 4, 2) Bezier control points
    road_major: np.ndarray       # (n,) bool: highways drawn thicker
    districts: list[dict[str, Any]]
    sources: np.ndarray          # (n, 5): x, z, air, ground, noise
    source_types: list[str]
    homes: np.ndarray            # (n, 3): x, z, households
    road_street: np.ndarray      # (n,) index into streets, -1 = unnamed
    streets: list[str]
    landmarks: list[dict[str, Any]]
    network_raw: dict[str, Any] = field(default_factory=dict)   # the export's roads section (traffic, coverage)
    utilities: dict[str, Any] = field(default_factory=dict)     # map-utilities export (power and water networks)
    network_raw_parent: dict[str, Any] = field(default_factory=dict)  # the whole map-network export (colour ranges)
    zones: Any = None                                           # plans.Zones from the map-zones export (layout plans)

    @staticmethod
    def from_exports(grids: dict, network: dict, buildings: dict, utilities: dict | None = None, zones: dict | None = None) -> "MapData":
        import plans  # zoning data is parsed by the planning module
        roads_json = network.get("roads") or {}
        if "curves" in roads_json:
            roads = _floats(roads_json["curves"]).reshape(-1, 4, 2)
            types = roads_json.get("types", [])
            major = np.array([("highway" in types[t].lower()) for t in roads_json.get("typeOfRoad", [])], dtype=bool)
        else:
            roads, major = np.zeros((0, 4, 2), "<f4"), np.zeros(0, bool)
        src = buildings.get("pollutionSources") or {}
        sources = _floats(src["values"]).reshape(-1, 5) if "values" in src else np.zeros((0, 5), "<f4")
        source_types = [src["types"][t] for t in src.get("typeOfSource", [])] if "types" in src else []
        homes_json = buildings.get("homes") or {}
        homes = _floats(homes_json["values"]).reshape(-1, 3) if "values" in homes_json else np.zeros((0, 3), "<f4")
        districts = network.get("districts") if isinstance(network.get("districts"), list) else []
        streets = roads_json.get("streets", [])
        road_street = np.array(roads_json.get("streetOfRoad", [-1] * len(roads)), dtype=int)
        landmarks_json = network.get("landmarks") or {}
        landmarks = landmarks_json.get("list", []) if isinstance(landmarks_json, dict) else []
        return MapData(
            city=grids.get("city", "?"),
            when=f"{grids.get('gameMonth', '?')} {grids.get('gameClock', '')} ({grids.get('trigger', '?')})",
            grids=grids.get("grids", {}),
            roads=roads, road_major=major, districts=districts,
            sources=sources, source_types=source_types, homes=homes,
            road_street=road_street, streets=streets, landmarks=landmarks,
            network_raw=roads_json, utilities=utilities or {}, network_raw_parent=network, zones=plans.parse_zones(zones),
        )

    def grid(self, name: str, channel: int = 0) -> np.ndarray:
        """A grid as a 2D array indexed [row, column], row 0 = south."""
        g = self.grids.get(name)
        if not g or "values" not in g:
            raise ValueError(f"Grid '{name}' is not in this export ({(g or {}).get('error', 'missing')}).")
        size, channels = g["size"], g["channels"]
        return _floats(g["values"]).reshape(size, size, channels)[:, :, channel]

    def value_at(self, name: str, channel: int, x: float, z: float) -> float:
        a = self.grid(name, channel)
        size = a.shape[0]
        c = int(min(size - 1, max(0, (x + HALF) / MAP_METRES * size)))
        r = int(min(size - 1, max(0, (z + HALF) / MAP_METRES * size)))
        return float(a[r, c])

    def district_of(self, x: float, z: float) -> str | None:
        for d in self.districts:
            outline = d.get("outline") or []
            if len(outline) >= 3 and PolygonPath(np.array(outline)).contains_point((x, z)):
                return d.get("name")
        return None

    def street_points(self) -> tuple[np.ndarray, np.ndarray]:
        """Points along every named street (about every 1/5 of a segment) and the street index of each."""
        if not hasattr(self, "_street_cache"):
            named = self.road_street >= 0 if len(self.road_street) == len(self.roads) else np.zeros(len(self.roads), bool)
            pts = _bezier_points(self.roads[named], steps=6)
            idx = np.repeat(self.road_street[named], pts.shape[1])
            self._street_cache = (pts.reshape(-1, 2), idx)
        return self._street_cache

    def describe(self, x: float, z: float) -> str:
        """A place in words the player can find in the game: street (and the nearest cross street),
        district, and the nearest landmark. Example: 'Rosemead Street at Belmont Street, Coleridge Glen,
        250 m north-east of Central Bank'."""
        parts = []
        pts, idx = self.street_points()
        if len(pts):
            d = np.hypot(pts[:, 0] - x, pts[:, 1] - z)
            first = int(np.argmin(d))
            street = self.streets[idx[first]]
            others = d.copy()
            names = np.array(self.streets, dtype=object)
            others[names[idx] == street] = np.inf  # a street can be split into pieces that share its name
            second = int(np.argmin(others))
            if d[first] > 400:
                parts.append(f"{int(d[first])} m from {street}")
            elif others[second] < 250:
                parts.append(f"{street} near {self.streets[idx[second]]}")
            else:
                parts.append(street)
        district = self.district_of(x, z)
        if district:
            parts.append(district)
        best = None
        for lm in self.landmarks:
            dist = math.hypot(lm["x"] - x, lm["z"] - z)
            if best is None or dist < best[0]:
                best = (dist, lm)
        if best and best[0] < 800:
            if best[0] < 60:
                parts.append(f"at {best[1]['name']}")
            else:
                parts.append(f"{int(round(best[0], -1))} m {compass(x - best[1]['x'], z - best[1]['z'])} of {best[1]['name']}")
        return ", ".join(parts) if parts else f"tile {square_of(x, z)}"

    def find(self, query: str) -> list[dict[str, Any]]:
        """Streets, landmarks and districts whose name contains the query (case-insensitive)."""
        q = query.strip().lower()
        found = []
        pts, idx = self.street_points()
        for s, name in enumerate(self.streets):
            if q in name.lower():
                mine = pts[idx == s]
                if len(mine):
                    found.append({"kind": "street", "name": name, "x": round(float(mine[:, 0].mean())), "z": round(float(mine[:, 1].mean())),
                                  "from": square_of(*mine[0]), "to": square_of(*mine[-1])})
        for lm in self.landmarks:
            if q in lm["name"].lower() or q in lm.get("type", "").lower():
                found.append({"kind": "landmark", "name": lm["name"], "type": lm.get("type"), "x": lm["x"], "z": lm["z"]})
        for d in self.districts:
            if q in d.get("name", "").lower() and d.get("outline"):
                o = np.array(d["outline"])
                found.append({"kind": "district", "name": d["name"], "x": round(float(o[:, 0].mean())), "z": round(float(o[:, 1].mean()))})
        for f in found:
            f["tile"] = square_of(f["x"], f["z"])
            if f["kind"] != "district":
                f["where"] = self.describe(f["x"], f["z"])
        return found

    def city_bounds(self) -> tuple[float, float, float, float]:
        """The built-up area (roads), widened to whole map tiles."""
        if len(self.roads) == 0:
            return -HALF, HALF, -HALF, HALF
        pts = self.roads.reshape(-1, 2)
        x0, x1 = np.percentile(pts[:, 0], [0.2, 99.8])
        z0, z1 = np.percentile(pts[:, 1], [0.2, 99.8])
        snap = lambda v, up: -HALF + (math.ceil if up else math.floor)((v + HALF) / TILE) * TILE  # noqa: E731
        return snap(x0, False), snap(x1, True), snap(z0, False), snap(z1, True)


# ---------- game noise overflow ----------

def _biggest_noise_share(x: float, z: float, noise: float, size: int) -> tuple[float, int, int]:
    """The game's share of one building's noise for its loudest cell (AddSingle), and that cell (row, col)."""
    cell = MAP_METRES / size
    c0, c1 = max(0, math.floor((x + HALF - NOISE_RADIUS) / cell)), min(size - 1, math.ceil((x + HALF + NOISE_RADIUS) / cell))
    r0, r1 = max(0, math.floor((z + HALF - NOISE_RADIUS) / cell)), min(size - 1, math.ceil((z + HALF + NOISE_RADIUS) / cell))
    cx = -HALF + (np.arange(c0, c1 + 1) + 0.5) * cell
    cz = -HALF + (np.arange(r0, r1 + 1) + 0.5) * cell
    d2 = (cz[:, None] - z) ** 2 + (cx[None, :] - x) ** 2
    weights = np.where(d2 < NOISE_RADIUS ** 2, 1.0 / np.maximum(20.0, d2), 0.0)  # 1 / max(20, distance^2)
    r, c = np.unravel_index(int(np.argmax(weights)), weights.shape)
    share = NOISE_MULTIPLIER * noise * weights[r, c] / (weights.sum() * NOISE_UPDATES_PER_DAY)
    return float(share), r0 + int(r), c0 + int(c)


def noise_overflows(m: MapData) -> list[dict[str, Any]]:
    """Places where the game's noise value overflowed: buildings so loud that their own cell shows up quiet or
    negative. Each must pass both checks: the game's formula puts it over NOISE_OVERFLOW_SHARE, and the grid
    shows the damage (its cell is negative or under half of the loudest cell two cells out). Negative cells
    with no such building nearby are listed too. 'cells' = the 3x3 cells (row, col) whose values are wrong."""
    if not hasattr(m, "_overflow_cache"):
        try:
            values = m.grid("noisePollution")
        except ValueError:
            m._overflow_cache = []
            return m._overflow_cache
        size = values.shape[0]
        found: list[dict[str, Any]] = []
        covered = np.zeros(values.shape, bool)
        noise = m.sources[:, SOURCE_CHANNEL["noise"]] if len(m.sources) else np.zeros(0)
        # Only buildings loud enough to reach the limit even with all their noise in one cell are worth testing.
        for i in np.flatnonzero(noise * NOISE_MULTIPLIER / NOISE_UPDATES_PER_DAY >= NOISE_OVERFLOW_SHARE):
            x, z = float(m.sources[i, 0]), float(m.sources[i, 1])
            share, r, c = _biggest_noise_share(x, z, float(noise[i]), size)
            if share < NOISE_OVERFLOW_SHARE:
                continue
            ring = values[max(0, r - 2):r + 3, max(0, c - 2):c + 3].copy()
            ring[1:-1, 1:-1] = -np.inf
            if not (values[r, c] < 0 or values[r, c] < 0.5 * ring.max()):
                continue
            rows, cols = slice(max(0, r - 1), r + 2), slice(max(0, c - 1), c + 2)
            covered[rows, cols] = True
            found.append({"x": x, "z": z, "building": m.source_types[i] if i < len(m.source_types) else "?",
                          "share": round(share), "cells": [(rr, cc) for rr in range(rows.start, rows.stop) for cc in range(cols.start, cols.stop)]})
        cell = MAP_METRES / size
        for r, c in np.argwhere((values < 0) & ~covered):
            found.append({"x": -HALF + (c + 0.5) * cell, "z": -HALF + (r + 0.5) * cell, "building": None, "share": None,
                          "cells": [(int(r), int(c))]})
        m._overflow_cache = found
    return m._overflow_cache


def noise_for_display(m: MapData) -> np.ndarray:
    """The noise grid with overflowed cells set to the game's maximum (32,767), so they read as 'loudest'."""
    values = m.grid("noisePollution").copy()
    for o in noise_overflows(m):
        for r, c in o["cells"]:
            values[r, c] = 32767
    return values


# ---------- drawing ----------

def _bezier_points(curves: np.ndarray, steps: int = 6) -> np.ndarray:
    t = np.linspace(0, 1, steps)[None, :, None]
    a, b, c, d = (curves[:, i][:, None, :] for i in range(4))
    return ((1 - t) ** 3) * a + 3 * ((1 - t) ** 2) * t * b + 3 * (1 - t) * (t ** 2) * c + (t ** 3) * d


def parse_mark(spec: str) -> tuple[float, float, str]:
    """'K20 New coal plant' (tile centre) or '-348,-4828 New coal plant' (world metres) -> (x, z, label)."""
    head, _, label = spec.strip().partition(" ")
    if "," in head:
        x, z = (float(v) for v in head.split(",", 1))
    else:
        bx0, bx1, bz0, bz1 = square_bounds(head)
        x, z = (bx0 + bx1) / 2, (bz0 + bz1) / 2
    return x, z, label.strip()


def render(m: MapData, layer: str, area: str = "", mark_sources: bool = True, marks: list[str] | None = None,
           plan: dict[str, Any] | None = None, bounds: tuple[float, float, float, float] | None = None) -> bytes:
    import networks  # network layers (traffic, coverage, power, water) live in their own module
    import plans  # zoning layer and layout plans

    if layer not in LAYERS and layer not in networks.LAYERS and layer not in plans.LAYERS:
        raise ValueError(f"Unknown layer '{layer}'. Use one of: {', '.join(list(LAYERS) + list(networks.LAYERS) + list(plans.LAYERS))}")
    grid_name, channel, cmap, title, source_kind = LAYERS.get(layer) or (None, 0, "", networks.LAYERS.get(layer) or plans.LAYERS[layer], None)
    if plan:
        title = f"Plan: {plan['goal']}"
    x0, x1, z0, z1 = bounds or (area_bounds(area) if area else m.city_bounds())
    footnote = ""

    fig, ax = plt.subplots(figsize=(10, 10 * (z1 - z0) / max(1.0, x1 - x0)), dpi=110)
    ax.set_facecolor("#f4f1ea")
    full = [-HALF, HALF, -HALF, HALF]

    # Water (sea, rivers, lakes) underneath everything.
    try:
        depth = m.grid("surfaceWater", 0)
        ax.imshow(np.ma.masked_less(depth, 0.5), extent=full, origin="lower", cmap="Blues", vmin=-3, vmax=12, alpha=0.55, zorder=1)
    except ValueError:
        pass

    # The chosen layer as a heat map (cells with 0 stay see-through).
    if grid_name and layer != "wind":
        values = noise_for_display(m) if layer == "noise" else m.grid(grid_name, channel)
        if layer == "water-pollution":
            values = np.where(m.grid("surfaceWater", 0) > 0.5, values, 0)
        shown = np.ma.masked_less_equal(values, 0)
        # Colour scale from the cells in view, so a zoomed map shows its own contrast.
        size = values.shape[0]
        centres = -HALF + (np.arange(size) + 0.5) * MAP_METRES / size
        cols = (centres >= x0) & (centres <= x1)
        rows = (centres >= z0) & (centres <= z1)
        in_view = shown[np.ix_(rows, cols)]
        if in_view.count():
            vmax = float(np.percentile(in_view.compressed(), 99)) or float(in_view.max())
            image = ax.imshow(shown, extent=full, origin="lower", cmap=cmap, vmin=0, vmax=vmax, alpha=0.7, zorder=2, interpolation="bilinear")
            bar = fig.colorbar(image, ax=ax, fraction=0.035, pad=0.02)
            bar.set_label(f"{title} (game units)")

    # Roads (or, on a network layer, the network drawn on the roads and lines themselves).
    if layer in networks.LAYERS:
        networks.draw(ax, fig, m, layer, x0, x1, z0, z1)
    elif len(m.roads):
        lines = _bezier_points(m.roads)
        keep = ((lines[:, :, 0].max(1) >= x0) & (lines[:, :, 0].min(1) <= x1) & (lines[:, :, 1].max(1) >= z0) & (lines[:, :, 1].min(1) <= z1))
        widths = np.where(m.road_major, 1.4, 0.45)[keep]
        ax.add_collection(LineCollection(lines[keep], colors="#4a4a4a", linewidths=widths, zorder=3))
    if layer == "zoning":
        footnote += plans.draw_zoning(ax, m, x0, x1, z0, z1, faint=plan is not None)

    # Wind arrows on air pollution and wind maps (wind blows in the arrow's direction).
    if layer in ("wind", "air"):
        try:
            wx, wz = m.grid("wind", 0), m.grid("wind", 1)
            size = wx.shape[0]
            centres = -HALF + (np.arange(size) + 0.5) * MAP_METRES / size
            step = max(1, int(round((x1 - x0) / (MAP_METRES / size) / 14)))
            cx, cz = np.meshgrid(centres[::step], centres[::step])
            u, v = wx[::step, ::step], wz[::step, ::step]
            # Arrow length in map metres: the strongest wind in view gets 80 % of the arrow spacing.
            spacing = step * MAP_METRES / size
            in_view = (cx >= x0) & (cx <= x1) & (cz >= z0) & (cz <= z1)
            strongest = float(np.hypot(u, v)[in_view].max()) if in_view.any() else 1.0
            ax.quiver(cx, cz, u, v, color="#1b5e8a", alpha=0.8, zorder=5, width=0.003,
                      angles="xy", scale_units="xy", scale=max(strongest, 1e-6) / (0.8 * spacing))
        except ValueError:
            pass

    # Polluting buildings, sized by how much they give off. Only the ones that matter on this map: at least
    # 3 % of the strongest in view, at most 120 markers; the 6 strongest get a name (each place named once).
    if mark_sources and source_kind and len(m.sources):
        amount = m.sources[:, SOURCE_CHANNEL[source_kind]]
        inside = (amount > 0) & (m.sources[:, 0] >= x0) & (m.sources[:, 0] <= x1) & (m.sources[:, 1] >= z0) & (m.sources[:, 1] <= z1)
        if inside.any():
            strongest = amount[inside].max()
            order = [i for i in np.argsort(-np.where(inside, amount, -1)) if inside[i] and amount[i] >= 0.03 * strongest][:120]
            sizes = 14 + 170 * np.sqrt(amount[order] / strongest)
            ax.scatter(m.sources[order, 0], m.sources[order, 1], s=sizes, marker="^", c="#b00020", edgecolors="white", linewidths=0.6, zorder=6)
            named: list[tuple[float, float]] = []
            spacing = (x1 - x0) / 12
            for i in order:
                x, z = float(m.sources[i, 0]), float(m.sources[i, 1])
                if len(named) >= 6 or any(math.hypot(x - a, z - b) < spacing for a, b in named):
                    continue
                named.append((x, z))
                name = _short(m.source_types[i]) if i < len(m.source_types) else "source"
                ax.annotate(f"{name} ({square_of(x, z)})", (x, z), xytext=(6, 6), textcoords="offset points", fontsize=7.5, color="#7a0015", zorder=7,
                            bbox=dict(facecolor="white", alpha=0.7, edgecolor="none", pad=0.5))

    # Noise the game could not count (overflow): hatched box over the wrong cells, drawn at the top colour.
    if layer == "noise":
        overflows = [o for o in noise_overflows(m) if x0 <= o["x"] <= x1 and z0 <= o["z"] <= z1]
        size = m.grid("noisePollution").shape[0]
        cell = MAP_METRES / size
        labelled: list[tuple[float, float]] = []
        for o in overflows:
            rows = [r for r, _ in o["cells"]]
            cols = [c for _, c in o["cells"]]
            bx, bz = -HALF + min(cols) * cell, -HALF + min(rows) * cell
            ax.add_patch(plt.Rectangle((bx, bz), (max(cols) - min(cols) + 1) * cell, (max(rows) - min(rows) + 1) * cell,
                                       fill=False, hatch="////", edgecolor="black", linewidth=1.0, zorder=7))
            if any(math.hypot(bx - a, bz - b) < (x1 - x0) / 8 for a, b in labelled):
                continue  # a nearby box already carries the label
            labelled.append((bx, bz))
            ax.annotate("too loud to measure", (bx, bz), xytext=(0, -9), textcoords="offset points", fontsize=7, color="black", zorder=9,
                        bbox=dict(facecolor="white", alpha=0.8, edgecolor="none", pad=0.4))
        if overflows:
            footnote = " Hatched = too loud to measure (game number overflow)."

    # Street names and landmarks when zoomed in far enough to read them (at most ~5 km across).
    if x1 - x0 <= (7000 if plan else 5200):
        pts, idx = m.street_points()
        inside = (pts[:, 0] >= x0) & (pts[:, 0] <= x1) & (pts[:, 1] >= z0) & (pts[:, 1] <= z1)
        for s in np.unique(idx[inside]):
            mine = pts[inside & (idx == s)]
            if len(mine) < 4:
                continue
            px, pz = mine[len(mine) // 2]
            ax.text(px, pz, m.streets[s], fontsize=6.5, color="#2b2b2b", ha="center", va="center", zorder=8,
                    bbox=dict(facecolor="#f4f1ea", alpha=0.75, edgecolor="none", pad=0.3))
        # Landmarks: a small square each; names only where they don't crowd (at most 25, each name once,
        # spaced apart), signature buildings first, so dense districts stay readable.
        labelled: list[tuple[float, float]] = []
        names_used: set[str] = set()
        spacing = (x1 - x0) / 9
        ordered = sorted(m.landmarks, key=lambda lm: lm.get("kind") != "signature")
        for lm in ordered:
            if not (x0 <= lm["x"] <= x1 and z0 <= lm["z"] <= z1):
                continue
            ax.scatter([lm["x"]], [lm["z"]], s=14, marker="s", c="#1f4e79", alpha=0.7, zorder=8)
            name = lm["name"]
            if (len(labelled) < 25 and name not in names_used
                    and all(math.hypot(lm["x"] - a, lm["z"] - b) > spacing for a, b in labelled)):
                labelled.append((lm["x"], lm["z"]))
                names_used.add(name)
                ax.annotate(name, (lm["x"], lm["z"]), xytext=(4, -9), textcoords="offset points", fontsize=6.5, color="#1f4e79", zorder=8)

    # Districts.
    for d in m.districts:
        outline = np.array(d.get("outline") or [])
        if len(outline) >= 3:
            closed = np.vstack([outline, outline[:1]])
            ax.plot(closed[:, 0], closed[:, 1], color="#5b2c83", linestyle="--", linewidth=1.2, zorder=4)
            cx, cz = outline.mean(axis=0)
            if x0 <= cx <= x1 and z0 <= cz <= z1:
                ax.text(cx, cz, d.get("name", ""), color="#5b2c83", fontsize=9, ha="center", weight="bold", zorder=8,
                        bbox=dict(facecolor="white", alpha=0.6, edgecolor="none", pad=1))

    # Places Claude points out (proposals, problems): green stars with their labels.
    for spec in marks or []:
        x, z, label = parse_mark(spec)
        ax.scatter([x], [z], s=260, marker="*", c="#1a7f37", edgecolors="white", linewidths=1.0, zorder=9)
        if label:
            ax.annotate(label, (x, z), xytext=(8, -12), textcoords="offset points", fontsize=8.5, color="#0d4d1f", weight="bold", zorder=9,
                        bbox=dict(facecolor="white", alpha=0.8, edgecolor="#1a7f37", pad=1.5))

    if plan:
        footnote += plans.draw_plan(ax, m, plan, x0, x1, z0, z1)

    # Map-tile grid with labels (A-W west to east, 1-23 north to south).
    for k in range(TILES + 1):
        v = -HALF + k * TILE
        if x0 - 1 <= v <= x1 + 1:
            ax.axvline(v, color="#999999", linewidth=0.4, zorder=4)
        if z0 - 1 <= v <= z1 + 1:
            ax.axhline(v, color="#999999", linewidth=0.4, zorder=4)
    cols = [c for c in range(TILES) if -HALF + (c + 0.5) * TILE >= x0 and -HALF + (c + 0.5) * TILE <= x1]
    rows = [r for r in range(TILES) if HALF - (r + 0.5) * TILE >= z0 and HALF - (r + 0.5) * TILE <= z1]
    ax.set_xticks([-HALF + (c + 0.5) * TILE for c in cols], [LETTERS[c] for c in cols])
    ax.set_yticks([HALF - (r + 0.5) * TILE for r in rows], [str(r + 1) for r in rows])
    ax.tick_params(length=0, labelsize=9, labeltop=True)

    ax.set_xlim(x0, x1)
    ax.set_ylim(z0, z1)
    ax.set_aspect("equal")
    ax.set_title(f"{m.city} - {title} - {m.when}", fontsize=11)
    legend = "N up. Squares = game map tiles (~623 m)." + (" Triangles = main polluting buildings (size = amount)." if source_kind and mark_sources else "")
    ax.text(0.01, 0.01, "\n".join(textwrap.wrap(legend + footnote, 150)), transform=ax.transAxes, fontsize=7, color="#555555", zorder=9,
            bbox=dict(facecolor="white", alpha=0.6, edgecolor="none", pad=1))
    buffer = io.BytesIO()
    fig.savefig(buffer, format="png", bbox_inches="tight")
    plt.close(fig)
    return buffer.getvalue()


def _short(prefab_name: str) -> str:
    """'IndustrialManufacturing01 Lvl3' style names, shortened for map labels."""
    return prefab_name.replace("_", " ")[:28]


# ---------- hotspots ----------

def hotspots(m: MapData, layer: str, top: int = 10, area: str = "") -> list[dict[str, Any]]:
    """The worst places on one layer: separate peaks, each with its map tile, district, value, homes hit
    nearby, the wind there and the strongest polluting buildings around it."""
    grid_name, channel, _, title, source_kind = LAYERS.get(layer, (None, 0, "", "", None))
    if not grid_name or layer == "wind":
        raise ValueError("Hotspots need a value layer: air, ground, noise, water-pollution, groundwater-pollution, land-value or population.")
    values = noise_for_display(m) if layer == "noise" else m.grid(grid_name, channel).copy()
    overflowed = {cell: o for o in noise_overflows(m) for cell in o["cells"]} if layer == "noise" else {}
    if layer == "water-pollution":
        values = np.where(m.grid("surfaceWater", 0) > 0.5, values, 0)
    size = values.shape[0]
    cell = MAP_METRES / size
    centres = -HALF + (np.arange(size) + 0.5) * cell
    if area:
        x0, x1, z0, z1 = area_bounds(area)
        mask = (centres[None, :] >= x0) & (centres[None, :] <= x1) & (centres[:, None] >= z0) & (centres[:, None] <= z1)
        values = np.where(mask, values, 0)

    result = []
    radius = max(2, int(round(400 / cell)))  # peaks at least ~400 m apart
    work = values.copy()
    for _ in range(top):
        r, c = np.unravel_index(int(np.argmax(work)), work.shape)
        peak = float(work[r, c])
        if peak <= 0:
            break
        work[max(0, r - radius):r + radius + 1, max(0, c - radius):c + radius + 1] = 0
        x, z = float(centres[c]), float(centres[r])
        overflow = overflowed.get((int(r), int(c)))
        if overflow:  # point at the building itself, not a corner of its damaged cells
            x, z = float(overflow["x"]), float(overflow["z"])
        entry: dict[str, Any] = {
            "where": m.describe(x, z),
            "square": square_of(x, z),
            "district": m.district_of(x, z),
            "value": round(peak, 2),
            "position": [round(x), round(z)],
        }
        if overflow:
            entry["value"] = "too loud to measure"
            entry["note"] = "The game's noise number overflowed here (shows as quiet or negative in the game); the real noise is above the scale."
        if len(m.homes):
            near = np.hypot(m.homes[:, 0] - x, m.homes[:, 1] - z) < 300
            entry["homesWithin300m"] = int(near.sum())
            entry["householdsWithin300m"] = int(m.homes[near, 2].sum())
        try:
            entry["windBlowsTo"] = compass(m.value_at("wind", 0, x, z), m.value_at("wind", 1, x, z))
        except ValueError:
            pass
        if source_kind and len(m.sources):
            amount = m.sources[:, SOURCE_CHANNEL[source_kind]]
            distance = np.hypot(m.sources[:, 0] - x, m.sources[:, 1] - z)
            near = (amount > 0) & (distance < 2000)
            order = np.argsort(-(np.where(near, amount / np.maximum(distance, 100), 0)))[:3]
            entry["strongestSourcesNearby"] = [
                {"building": m.source_types[i] if i < len(m.source_types) else "?", "givesOff": round(float(amount[i]), 1),
                 "where": m.describe(float(m.sources[i, 0]), float(m.sources[i, 1])),
                 "distanceMetres": int(distance[i]), "square": square_of(float(m.sources[i, 0]), float(m.sources[i, 1])),
                 "direction": compass(float(m.sources[i, 0]) - x, float(m.sources[i, 1]) - z)}
                for i in order if near[i]
            ]
        result.append(entry)
    return result
