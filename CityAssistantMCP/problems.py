"""The game's warning icons (problems.json from the mod): counts per icon type, where they cluster, and a
map layer. One icon = one entity the game draws over a building, road, line or vehicle."""
from __future__ import annotations

import base64
from typing import Any

import numpy as np

PRIORITY_ORDER = ["Info", "Problem", "Warning", "MajorProblem", "Error", "FatalProblem"]
CLUSTER_METRES = 300.0   # icons closer than this count as one place
TITLE = "Problem icons"


def positions(entry: dict[str, Any]) -> np.ndarray:
    raw = entry.get("positions") or ""
    if not raw:
        return np.zeros((0, 2), "<f4")
    return np.frombuffer(base64.b64decode(raw), dtype="<f4").reshape(-1, 2)


def types(problems_export: dict[str, Any]) -> list[dict[str, Any]]:
    section = problems_export.get("problems") or {}
    if "error" in section:
        raise ValueError(f"The mod could not read the problem icons: {section['error']}")
    return [t for t in section.get("types", []) if t.get("count", 0) > 0]


def places(points: np.ndarray, top: int) -> list[tuple[float, float, int]]:
    """The densest spots: (x, z, icons within CLUSTER_METRES), separate peaks, most icons first. Seeds come
    from the busiest cell of a CLUSTER_METRES grid, so the cost stays low with thousands of icons."""
    result = []
    left = points.copy()
    while len(left) and len(result) < top:
        keys = np.floor(left / CLUSTER_METRES).astype(int)
        _, inverse, counts = np.unique(keys, axis=0, return_inverse=True, return_counts=True)
        seed = left[inverse.ravel() == int(np.argmax(counts))].mean(0)
        near = np.hypot(left[:, 0] - seed[0], left[:, 1] - seed[1]) < CLUSTER_METRES
        if not near.any():
            near = inverse.ravel() == int(np.argmax(counts))
        cx, cz = left[near].mean(0)
        result.append((float(cx), float(cz), int(near.sum())))
        left = left[~near]
    return result


def digest(problems_export: dict[str, Any], m: Any = None, places_per_type: int = 3) -> dict[str, Any]:
    """problems.json without the raw positions: per icon type the count, priority, the districts with most
    of them and the worst places in street names (when map data is available)."""
    section = problems_export.get("problems") or {}
    header = {k: v for k, v in problems_export.items() if k != "problems"}
    if "error" in section:
        return {**header, "problems": section}
    out_types = []
    for t in types(problems_export):
        pts = positions(t)
        entry = {k: t.get(k) for k in ("title", "type", "priority", "count", "hiddenCount", "description")}
        if section.get("buildingCount"):
            entry["percentOfBuildings"] = round(100 * t["count"] / section["buildingCount"], 2)
        if m is not None and len(pts):
            districts: dict[str, int] = {}
            for x, z in pts:
                name = m.district_of(float(x), float(z)) or "(no district)"
                districts[name] = districts.get(name, 0) + 1
            entry["byDistrict"] = dict(sorted(districts.items(), key=lambda kv: -kv[1])[:5])
            entry["worstPlaces"] = [{"where": m.describe(x, z), "icons": n, "position": [round(x), round(z)]}
                                    for x, z, n in places(pts, places_per_type)]
        out_types.append(entry)
    return {**header, "problems": {
        "total": section.get("total", 0),
        "buildingCount": section.get("buildingCount"),
        "byPriority": section.get("byPriority", {}),
        "priorityScale": section.get("priorityScale"),
        "types": out_types,
    }}


def hotspots(problems_export: dict[str, Any], m: Any, top: int = 10, area_bounds: tuple | None = None,
             min_priority: str = "Problem") -> list[dict[str, Any]]:
    """Places with the most warning icons of any type (priority Problem and up), each with its mix of types."""
    floor = PRIORITY_ORDER.index(min_priority)
    pts_all, labels = [], []
    for t in types(problems_export):
        if PRIORITY_ORDER.index(t.get("priority") or "Info") < floor:
            continue
        pts = positions(t)
        pts_all.append(pts)
        labels += [t.get("title") or t.get("type")] * len(pts)
    if not pts_all:
        return []
    pts = np.concatenate(pts_all)
    labels_arr = np.array(labels, dtype=object)
    if area_bounds:
        x0, x1, z0, z1 = area_bounds
        keep = (pts[:, 0] >= x0) & (pts[:, 0] <= x1) & (pts[:, 1] >= z0) & (pts[:, 1] <= z1)
        pts, labels_arr = pts[keep], labels_arr[keep]
    result = []
    for x, z, _ in places(pts, top):
        near = np.hypot(pts[:, 0] - x, pts[:, 1] - z) < CLUSTER_METRES
        mix: dict[str, int] = {}
        for label in labels_arr[near]:
            mix[label] = mix.get(label, 0) + 1
        result.append({"where": m.describe(x, z), "district": m.district_of(x, z), "icons": int(near.sum()),
                       "types": dict(sorted(mix.items(), key=lambda kv: -kv[1])), "position": [round(x), round(z)]})
    return result


def draw(ax: Any, fig: Any, problems_export: dict[str, Any], x0: float, x1: float, z0: float, z1: float,
         only: str = "") -> None:
    """Icons as dots, one colour per type (the 8 commonest in view), bigger for more serious ones."""
    import matplotlib.pyplot as plt
    shown = []
    for t in types(problems_export):
        if only and only.lower() not in (t.get("type", "").lower(), (t.get("title") or "").lower()):
            continue
        pts = positions(t)
        inside = (pts[:, 0] >= x0) & (pts[:, 0] <= x1) & (pts[:, 1] >= z0) & (pts[:, 1] <= z1)
        if inside.any():
            shown.append((int(inside.sum()), t, pts[inside]))
    shown.sort(key=lambda s: -s[0])
    colours = plt.get_cmap("tab10").colors
    for i, (n, t, pts) in enumerate(shown):
        rank = PRIORITY_ORDER.index(t.get("priority") or "Info")
        colour = colours[i % 10] if i < 8 else (0.5, 0.5, 0.5)
        label = f"{t.get('title') or t.get('type')} ({n})" if i < 8 else None
        ax.scatter(pts[:, 0], pts[:, 1], s=6 + 5 * rank, color=colour, edgecolors="black", linewidths=0.3,
                   alpha=0.85, zorder=6, label=label)
    if len(shown) > 8:
        ax.scatter([], [], s=10, color=(0.5, 0.5, 0.5), label=f"{len(shown) - 8} other types ({sum(s[0] for s in shown[8:])})")
    if shown:
        ax.legend(loc="upper left", fontsize=7, framealpha=0.85)
