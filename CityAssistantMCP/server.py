"""CityAssistant MCP server (Phase 3).

Gives Claude query tools over the CityAssistant mod's data instead of raw file reading:
live numbers from the running game (the mod's endpoint at http://127.0.0.1:51842) and the
exported files (latest export, history snapshots, journal) when the game is closed.

Everything here is read-only. Units and the meaning of each field are documented in the
cs2-city-assistant skill (references/data-reference.md); the data is the same in both sources.
"""

from __future__ import annotations

import json
import os
import re
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

from mcp.server.mcpserver import Image, MCPServer
from mcp.server.mcpserver.exceptions import ToolError

import journal
import maps
import networks
import plans

LIVE_URL = os.environ.get("CITYASSISTANT_LIVE_URL", "http://127.0.0.1:51842")
DATA_DIR = Path(
    os.environ.get(
        "CITYASSISTANT_DATA",
        Path.home() / "AppData" / "LocalLow" / "Colossal Order" / "Cities Skylines II" / "ModsData" / "CityAssistant",
    )
)

# Same order as the mod's ExportCatalog.
DOMAINS = [
    "overview", "finance", "services", "utilities", "growth", "economy",
    "traffic", "transport", "environment", "wellbeing", "districts",
]
HOURS = ["00h", "06h", "12h", "18h"]

mcp = MCPServer(
    name="city-assistant",
    instructions=(
        "Data from the player's Cities: Skylines II city, exported by the CityAssistant mod. Everything is "
        "read-only except the co-pilot pair propose_change / apply_change (taxes incl. per product, service "
        "budgets, fees, loan, city policies): "
        "propose, show the player 'from -> to' with reason and expected effect, and call apply_change only after "
        "the player's explicit yes to that one change. Never chain changes on one approval. The city journal "
        "(read_journal, create_journal, add_to_journal, edit_journal, archive_journal) is Claude's memory per city. "
        "Start with game_status (is the game open, which city, game date). get_city_data gives live numbers "
        "while that city is open, else the latest export. Use get_history for trends instead of opening "
        "snapshots one by one. Field meanings and units: the cs2-city-assistant skill's data-reference.md. "
        "Data marked warmingUp (right after loading a save) is not settled; never treat it as a trend. "
        "Maps: get_map draws a layer (pollution, wind, land value, ...) with roads, water, districts and a "
        "lettered grid of the game's map tiles; map_hotspots lists the worst places; find_place and "
        "describe_place translate between names and places. plan_layout proposes where to zone or where a new "
        "service building helps most, with a map. The game never shows the tile letters: always "
        "tell the player places by street, district and landmark (the 'where' text), not only 'K20'."
    ),
)


# ---------- helpers ----------

def _live(path: str) -> dict[str, Any] | None:
    """GET from the mod's live endpoint; None when the game (or the endpoint) is not running."""
    try:
        with urllib.request.urlopen(LIVE_URL + path, timeout=20) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        try:
            return json.loads(e.read().decode("utf-8"))
        except Exception:
            return {"error": f"HTTP {e.code}"}
    except (urllib.error.URLError, OSError, TimeoutError):
        return None


def _read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _cities_dir() -> Path:
    return DATA_DIR / "cities"


def _city_names() -> list[str]:
    folder = _cities_dir()
    return sorted(p.name for p in folder.iterdir() if p.is_dir()) if folder.is_dir() else []


def _resolve_city(city: str) -> str:
    """The named city, or a sensible default: the city open in the game, else the most recently exported."""
    names = _city_names()
    if city:
        for name in names:
            if name.lower() == city.lower():
                return name
        raise ToolError(f"No data for city '{city}'. Known cities: {', '.join(names) or 'none'}")
    status = _live("/status")
    if status and status.get("cityOpen") and status.get("city") in names:
        return status["city"]
    if not names:
        raise ToolError(f"No city data found in {_cities_dir()}")
    return max(names, key=lambda n: (_cities_dir() / n / "overview.json").stat().st_mtime
               if (_cities_dir() / n / "overview.json").exists() else 0)


def _check_domain(domain: str) -> str:
    domain = domain.strip().lower()
    if domain not in DOMAINS:
        raise ToolError(f"Unknown domain '{domain}'. Use one of: {', '.join(DOMAINS)}")
    return domain


def _snapshots(city: str) -> list[tuple[str, str]]:
    """(month, hour) of every saved snapshot, oldest first. Abandoned runs are not history."""
    history = _cities_dir() / city / "history"
    result = []
    if history.is_dir():
        for month in sorted(p.name for p in history.iterdir() if p.is_dir() and p.name != "abandoned"):
            for hour in HOURS:
                if any((history / month).glob(f"{hour}_*.json")):
                    result.append((month, hour))
    return result


def _pick(data: Any, path: str) -> Any:
    """Follow a dotted path: 'population.population', 'demandBars.industrial', 'educationData.values.0',
    or 'name=Coleridge Glen' / 'factor=Tax' to pick a list entry by one of its fields."""
    current = data
    for part in path.split("."):
        if current is None:
            return None
        if isinstance(current, list):
            if "=" in part:
                key, value = part.split("=", 1)
                current = next((item for item in current if isinstance(item, dict) and str(item.get(key)) == value), None)
            elif part.lstrip("-").isdigit() and -len(current) <= int(part) < len(current):
                current = current[int(part)]
            else:
                return None
        elif isinstance(current, dict):
            current = current.get(part)
        else:
            return None
    return current


# ---------- tools ----------

@mcp.tool()
def game_status() -> dict[str, Any]:
    """Is the game running with a city open? Returns the city, game month and clock, and whether it is paused."""
    status = _live("/status")
    if status is None:
        return {"gameRunning": False, "note": "Game not running (or the mod's live endpoint is off). File data still works."}
    return {"gameRunning": True, **status}


@mcp.tool()
def list_cities() -> list[dict[str, Any]]:
    """Every city the mod has exported, with its latest export, the snapshot range in history, and whether it has a journal."""
    result = []
    for name in _city_names():
        folder = _cities_dir() / name
        entry: dict[str, Any] = {"city": name, "hasJournal": (folder / "journal.md").exists()}
        overview = folder / "overview.json"
        if overview.exists():
            header = _read_json(overview)
            entry["latestExport"] = {k: header.get(k) for k in ("gameMonth", "gameClock", "trigger", "warmingUp", "exportedAt")}
        snapshots = _snapshots(name)
        entry["snapshotCount"] = len(snapshots)
        if snapshots:
            entry["firstSnapshot"] = " ".join(snapshots[0])
            entry["lastSnapshot"] = " ".join(snapshots[-1])
        result.append(entry)
    return result


@mcp.tool()
def get_city_data(domain: str, city: str = "") -> dict[str, Any]:
    """Current numbers for one domain: overview, finance, services, utilities, growth, economy, traffic,
    transport, environment, wellbeing or districts. Live from the game when that city is open in it
    (trigger 'live'), otherwise the latest exported file. 'city' defaults to the city open in the game."""
    domain = _check_domain(domain)
    city = _resolve_city(city)
    status = _live("/status")
    if status and status.get("cityOpen") and status.get("city") == city:
        data = _live(f"/live/{domain}")
        if data is not None and "error" not in data:
            return {"source": "live from the running game", **data}
    path = _cities_dir() / city / f"{domain}.json"
    if not path.exists():
        raise ToolError(f"No {domain} export for '{city}' yet (older mod version, or the game never exported it).")
    return {"source": "latest exported file (game closed or another city open)", **_read_json(path)}


@mcp.tool()
def list_snapshots(city: str = "") -> dict[str, Any]:
    """Which history snapshots exist for a city: game months and, per month, the hours saved (00h/06h/12h/18h)."""
    city = _resolve_city(city)
    months: dict[str, list[str]] = {}
    for month, hour in _snapshots(city):
        months.setdefault(month, []).append(hour)
    return {"city": city, "months": months}


@mcp.tool()
def get_snapshot(domain: str, month: str, hour: str = "00h", city: str = "") -> dict[str, Any]:
    """One saved history snapshot, e.g. domain 'finance', month '2030-09', hour '06h'."""
    domain = _check_domain(domain)
    city = _resolve_city(city)
    path = _cities_dir() / city / "history" / month / f"{hour}_{domain}.json"
    if not path.exists():
        raise ToolError(f"No {hour} {domain} snapshot for {month} in '{city}'. Use list_snapshots to see what exists.")
    return _read_json(path)


@mcp.tool()
def get_history(domain: str, fields: list[str], hour: str = "00h", last_months: int = 12,
                include_warmup: bool = False, city: str = "") -> dict[str, Any]:
    """A trend table: chosen fields from the same snapshot hour across months (compare like with like).
    'fields' are dotted paths into that domain's file, e.g. domain 'overview' with
    ['population.population', 'money.balance'], or 'districts' with ['districts.name=Coleridge Glen.residents.residentCount'].
    Use hour 'all' for every snapshot. Warm-up snapshots are left out unless include_warmup is true."""
    domain = _check_domain(domain)
    city = _resolve_city(city)
    snapshots = [(m, h) for m, h in _snapshots(city) if hour == "all" or h == hour]
    months = sorted({m for m, _ in snapshots})[-max(1, last_months):]
    rows, skipped = [], 0
    for month, h in snapshots:
        if month not in months:
            continue
        path = _cities_dir() / city / "history" / month / f"{h}_{domain}.json"
        if not path.exists():
            continue
        data = _read_json(path)
        if data.get("warmingUp") and not include_warmup:
            skipped += 1
            continue
        row = {"month": month, "hour": h}
        for field in fields:
            row[field] = _pick(data, field)
        rows.append(row)
    return {"city": city, "domain": domain, "hour": hour, "rows": rows, "warmUpSnapshotsLeftOut": skipped}


def _journal_path(city: str, must_exist: bool = True) -> tuple[str, Path]:
    """(city name, journal path). A city the mod has not exported yet (charter interview before founding) gets
    the folder name the mod will use."""
    try:
        city = _resolve_city(city)
    except ToolError:
        if must_exist or not city:
            raise
    return city, _cities_dir() / journal.safe_folder_name(city) / "journal.md"


def _current_game_month() -> str:
    status = _live("/status")
    if status and status.get("cityOpen") and status.get("gameMonth"):
        return status["gameMonth"]
    return ""


@mcp.tool()
def read_journal(city: str = "", include_archive: bool = False) -> dict[str, Any]:
    """The city journal (charter, decisions, plans, lessons, session notes) as markdown. include_archive also
    returns journal-archive.md (old finished decisions and session notes); read it only when the past matters."""
    city, path = _journal_path(city)
    if not path.exists():
        return {"city": city, "journal": None,
                "note": "No journal yet: this is a new city. Run the charter interview, then create_journal."}
    result = {"city": city, "journal": path.read_text(encoding="utf-8"), "lines": len(path.read_text(encoding="utf-8").splitlines())}
    archive = path.with_name("journal-archive.md")
    if include_archive and archive.exists():
        result["archive"] = archive.read_text(encoding="utf-8")
    elif archive.exists():
        result["archiveExists"] = True
    return result


@mcp.tool()
def create_journal(city: str, financial_risk: str = "", growth_pace: str = "", citizens_vs_cost: str = "",
                   layout_style: str = "", tie_breaker: str = "") -> dict[str, Any]:
    """Start the journal of a NEW city with the player's charter-interview answers (empty = 'not asked yet';
    tie_breaker 'default' = the standard domain order). Works before the mod has exported the city."""
    city, path = _journal_path(city, must_exist=False)
    month = _current_game_month() or "before founding"
    try:
        journal.create(path, city, month, {"Financial risk": financial_risk, "Growth pace": growth_pace,
                                           "Citizens vs. cost": citizens_vs_cost, "Layout style": layout_style,
                                           "Tie-breaker": tie_breaker})
    except ValueError as e:
        raise ToolError(str(e))
    return {"city": city, "created": str(path), "journal": path.read_text(encoding="utf-8")}


@mcp.tool()
def add_to_journal(section: str, text: str, city: str = "") -> dict[str, Any]:
    """Add one entry at the top of a journal section: 'Session notes', 'Lessons' or 'Plans' (text = the bullet,
    e.g. '**2031-04 00h, session 3.** ...'), or 'Decision log' (text = a table row
    '| Game month | Decision | Reason | Expected effect | Check in | Outcome |', Outcome empty until checked).
    Co-pilot changes add their own decision row; don't add it twice."""
    city, path = _journal_path(city)
    try:
        journal.add_entry(path, section, text)
    except ValueError as e:
        raise ToolError(str(e))
    return {"city": city, "added": section, "journalLines": len(path.read_text(encoding="utf-8").splitlines()),
            "undo": "journal.previous.md holds the version before this change"}


@mcp.tool()
def edit_journal(old_text: str, new_text: str, city: str = "") -> dict[str, Any]:
    """Change one exact piece of the journal: a charter row, a decision's empty Outcome cell (quote the whole
    row), a finished plan. old_text must appear exactly once."""
    city, path = _journal_path(city)
    try:
        journal.edit(path, old_text, new_text)
    except ValueError as e:
        raise ToolError(str(e))
    return {"city": city, "edited": True, "undo": "journal.previous.md holds the version before this change"}


@mcp.tool()
def archive_journal(city: str = "", older_than_months: int = 12, keep_session_notes: int = 10) -> dict[str, Any]:
    """Keep the journal short: move finished decisions (Outcome filled, not "Pending") older than `older_than_months` game
    months and all but the newest `keep_session_notes` session notes into journal-archive.md. Charter, open
    decisions, plans and lessons stay. Use at a new in-game year or when the journal passes ~300 lines."""
    city, path = _journal_path(city)
    month = _current_game_month()
    if not month:
        overview = _cities_dir() / journal.safe_folder_name(city) / "overview.json"
        month = _read_json(overview).get("gameMonth", "") if overview.exists() else ""
    try:
        moved = journal.archive(path, month, older_than_months, keep_session_notes)
    except ValueError as e:
        raise ToolError(str(e))
    return {"city": city, "gameMonth": month, **moved, "journalLines": len(path.read_text(encoding="utf-8").splitlines())}


# ---------- maps (Phase 4) ----------

_MAP_CACHE: dict[str, tuple[float, maps.MapData]] = {}


def _map_data(city: str) -> maps.MapData:
    """Live map data while the city is open in the game (cached for a minute), else the files the mod
    saves at 00:00 each game day."""
    status = _live("/status")
    if status and status.get("cityOpen") and status.get("city") == city:
        cached = _MAP_CACHE.get(city)
        if cached and time.time() - cached[0] < 60:
            return cached[1]
        parts = {name: _live(f"/map/{name}") for name in ("grids", "network", "buildings")}
        if all(p is not None and "error" not in p for p in parts.values()):
            utilities = _live("/map/utilities")  # newer mod versions only
            zones = _live("/map/zones")
            data = maps.MapData.from_exports(parts["grids"], parts["network"], parts["buildings"],
                                             utilities if utilities and "error" not in utilities else None,
                                             zones if zones and "error" not in zones else None)
            _MAP_CACHE[city] = (time.time(), data)
            return data
    folder = _cities_dir() / city
    files = [folder / f"map-{n}.json" for n in ("grids", "network", "buildings")]
    if not all(f.exists() for f in files):
        raise ToolError(f"No map data for '{city}' yet: open it in the game (live), or play past 00:00 once so the mod saves the map.")
    utilities = folder / "map-utilities.json"
    zones = folder / "map-zones.json"
    return maps.MapData.from_exports(*(_read_json(f) for f in files), _read_json(utilities) if utilities.exists() else None,
                                     _read_json(zones) if zones.exists() else None)


@mcp.tool()
def get_map(layer: str = "base", area: str = "", city: str = "", mark_sources: bool = True, marks: list[str] | None = None) -> Image:
    """Draw a map of the city as an image: roads, water, district outlines and the game's map tiles as a
    lettered grid, with one layer on top: base, air, ground, noise, water-pollution, groundwater,
    groundwater-pollution, land-value, population or wind (grids); traffic, electricity, water, sewage, or
    coverage-healthcare / -fire / -police / -park / -post / -education / -shelter / -welfare (drawn on the
    roads and lines); zoning (zoned cells by type, built or empty, free roadside land, tiles not owned). Polluting buildings are marked on pollution layers; wind arrows on air and wind;
    bottlenecks and buildings short of power or water on the utility layers. 'area' zooms in: one tile
    ('D4') or two corners ('C3:F6').
    The image is also saved in the city's maps folder."""
    city = _resolve_city(city)
    data = _map_data(city)
    try:
        png = maps.render(data, layer.strip().lower(), area, mark_sources, marks)
    except ValueError as e:
        raise ToolError(str(e))
    folder = _cities_dir() / city / "maps"
    folder.mkdir(exist_ok=True)
    stamp = re.sub(r"[^0-9A-Za-z-]+", "_", data.when).strip("_")
    (folder / f"{layer}_{area.replace(':', '-') or 'city'}_{stamp}.png").write_bytes(png)
    return Image(data=png, format="png")


@mcp.tool()
def map_hotspots(layer: str, top: int = 10, area: str = "", city: str = "") -> dict[str, Any]:
    """The worst places on one map layer, each with a 'where' in street names. Grid layers (air, ground,
    noise, water-pollution, groundwater-pollution, land-value, population): separate peaks with value,
    homes and households within 300 m, the wind, and the strongest polluting buildings within 2 km.
    traffic: the most jammed busy roads with their worst time of day. coverage-<service>: places where many
    households sit on poorly covered roads. electricity: bottleneck and disconnected lines, then clusters
    of buildings short of power. water / sewage: clusters of buildings short of water / sewage."""
    city = _resolve_city(city)
    data = _map_data(city)
    layer = layer.strip().lower()
    try:
        if layer in networks.LAYERS:
            spots = networks.hotspots(data, layer, max(1, min(top, 30)), area)
        else:
            spots = maps.hotspots(data, layer, max(1, min(top, 30)), area)
    except ValueError as e:
        raise ToolError(str(e))
    return {"city": city, "layer": layer, "data": data.when, "hotspots": spots,
            "note": "Values are game units; compare places with each other, not with other cities."}



def _demand_bars(city: str) -> dict[str, Any] | None:
    """The demand panel's bars (growth export), live when the city is open, else the latest file."""
    status = _live("/status")
    if status and status.get("cityOpen") and status.get("city") == city:
        growth = _live("/live/growth")
        if growth and "error" not in growth:
            return growth.get("demandBars")
    path = _cities_dir() / city / "growth.json"
    return _read_json(path).get("demandBars") if path.exists() else None


def _plan_view(data: maps.MapData, plan: dict[str, Any]) -> tuple[float, float, float, float]:
    """Zoom the plan map onto its proposals (with room around them), at least 2.5 km across."""
    pts = [p["position"] for p in plan.get("proposals", [])] + [a["position"] for a in plan.get("newAreas", [])]
    if not pts:
        return data.city_bounds()
    pad = 700.0  # reach circles of service plans may run off the edge; street names matter more
    xs, zs = [p[0] for p in pts], [p[1] for p in pts]
    x0, x1, z0, z1 = min(xs) - pad, max(xs) + pad, min(zs) - pad, max(zs) + pad
    side = max(2500.0, x1 - x0, z1 - z0)
    cx, cz = (x0 + x1) / 2, (z0 + z1) / 2
    return cx - side / 2, cx + side / 2, cz - side / 2, cz + side / 2


@mcp.tool(structured_output=False)
def plan_layout(goal: str, top: int = 3, area: str = "", building: str = "", city: str = "") -> list[Any]:
    """Propose where to build, with a map. goal = residential, commercial, industrial or office (where to
    zone: free land along existing roads, ranked by pollution, services, traffic, customers or distance from
    homes, plus open land the city owns for a new neighbourhood), or a service: healthcare, fire, police,
    park, post, education, shelter, welfare (where one new building of that kind reaches the most
    poorly covered households, using the game's coverage formula; 'building' picks a type by name, e.g.
    'hospital'). Also reports the demand bar and land already zoned but still empty. 'area' limits the
    search to tiles ('H10:M16'). Returns the plan (each proposal with a 'where' in street names, its facts
    and the reasons) and the map (numbered proposals). The player decides and builds."""
    city = _resolve_city(city)
    data = _map_data(city)
    goal = goal.strip().lower()
    top = max(1, min(top, 8))
    try:
        if goal in plans.ZONE_GOALS:
            plan = plans.plan_zoning(data, goal, _demand_bars(city), top, area)
        elif goal in plans.SERVICE_GOALS:
            plan = plans.plan_service(data, goal, top, area, building)
        else:
            raise ValueError(f"Unknown goal '{goal}'. Use one of: {', '.join(plans.ZONE_GOALS + plans.SERVICE_GOALS)}")
        png = maps.render(data, "zoning", mark_sources=False, plan=plan, bounds=_plan_view(data, plan))
    except ValueError as e:
        raise ToolError(str(e))
    folder = _cities_dir() / city / "maps"
    folder.mkdir(exist_ok=True)
    stamp = re.sub(r"[^0-9A-Za-z-]+", "_", data.when).strip("_")
    (folder / f"plan-{goal}_{area.replace(':', '-') or 'city'}_{stamp}.png").write_bytes(png)
    return [{"city": city, **plans.public(plan)}, Image(data=png, format="png")]


@mcp.tool()
def find_place(name: str, city: str = "") -> dict[str, Any]:
    """Find streets, landmarks (service and signature buildings, e.g. 'Central Bank') and districts by
    name, so a place the player mentions can be put on the map. Returns positions, map tile and a
    'where' description."""
    city = _resolve_city(city)
    data = _map_data(city)
    found = data.find(name)
    if not found:
        raise ToolError(f"Nothing called '{name}' in {city}. Streets: {len(data.streets)}, landmarks: {len(data.landmarks)}.")
    return {"city": city, "found": found[:25], "more": max(0, len(found) - 25)}


@mcp.tool()
def describe_place(place: str, city: str = "") -> dict[str, Any]:
    """Say where a place is in terms the player sees in the game (street and cross street, district,
    nearest landmark). 'place' is a map tile ('K20', its centre) or world metres ('-348,-4828')."""
    city = _resolve_city(city)
    data = _map_data(city)
    try:
        x, z, _ = maps.parse_mark(place + " ")
    except ValueError as e:
        raise ToolError(str(e))
    return {"city": city, "tile": maps.square_of(x, z), "position": [round(x), round(z)], "where": data.describe(x, z)}


# ---------- co-pilot changes (Phase 5) ----------
# One numeric setting at a time, only after the player approved it in chat: propose_change shows what would
# change, apply_change makes the approved change and writes it into the journal. The mod refuses anything
# outside the game's own limits, and refuses a change whose starting value moved since the proposal.

_PROPOSALS: dict[str, dict[str, Any]] = {}
_PROPOSAL_LIFETIME_S = 30 * 60
_EDUCATION = {"uneducated": "uneducated", "poorlyEducated": "poorly educated", "educated": "educated",
              "wellEducated": "well educated", "highlyEducated": "highly educated"}


def _act(params: dict[str, Any]) -> tuple[int, dict[str, Any]]:
    """POST /act on the mod's live endpoint with its key; (HTTP status, answer)."""
    key_file = DATA_DIR / "endpoint-key.txt"
    if not key_file.exists():
        raise ToolError("The mod's key file is missing: start the game with the CityAssistant mod (a version with co-pilot changes).")
    query = urllib.parse.urlencode({k: v for k, v in params.items() if v is not None and v != ""})
    request = urllib.request.Request(f"{LIVE_URL}/act?{query}", data=b"", method="POST",
                                     headers={"X-CityAssistant-Key": key_file.read_text(encoding="utf-8").strip()})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read().decode("utf-8"))
        except Exception:
            return e.code, {"error": f"HTTP {e.code}"}
    except (urllib.error.URLError, OSError, TimeoutError):
        raise ToolError("The game is not running (or the mod's live endpoint is off).")


def _policy_title(target: str) -> str:
    finance = _live("/live/finance") or {}
    for p in finance.get("cityPolicies") or []:
        if p.get("name", "").lower() == target.lower():
            return p.get("title") or target
    return target


def _label(action: str, target: str) -> str:
    if action == "tax.resource":
        zone, _, product = target.partition(":")
        return f"{zone} tax on {product}"
    if action == "policy":
        return f"Policy '{_policy_title(target)}'"
    if action == "policy.slider":
        return f"Policy '{_policy_title(target)}' slider"
    if action == "tax.main":
        return "Main tax rate"
    if action == "tax.zone":
        return f"{target} tax"
    if action == "tax.education":
        return f"Residential tax for {_EDUCATION.get(target, target)} citizens"
    if action == "budget":
        return f"{target} budget"
    if action == "fee":
        return f"{target} fee"
    return "Loan"


def _fmt(value: Any, unit: str) -> str:
    if value is None:
        return "?"
    if unit.startswith("on/off"):
        return "on" if float(value) > 0.5 else "off"
    if unit.startswith("policy slider"):
        return f"{value:g}"
    if unit.startswith("percent"):
        return f"{value:g} %"
    if unit.startswith("money per unit"):
        return f"{value:g}"
    return f"{int(round(value)):,}"


def _journal_decision(city: str, row: list[str]) -> str:
    """Put one row at the top of the journal's decision log (a city without a journal gets one, charter
    'not asked yet')."""
    path = _cities_dir() / journal.safe_folder_name(city) / "journal.md"
    if not path.exists():
        journal.create(path, city, row[0], {})
    journal.add_entry(path, "Decision log", journal.decision_row(*row))
    return str(path)


@mcp.tool()
def propose_change(action: str, to: float, target: str = "", reason: str = "", expected_effect: str = "",
                   check_in: str = "") -> dict[str, Any]:
    """Step 1 of a co-pilot change: check one setting and prepare the change, WITHOUT changing anything.
    action: tax.main (all taxes' base rate), tax.zone (target Residential/Commercial/Industrial/Office),
    tax.education (target uneducated/poorlyEducated/educated/wellEducated/highlyEducated, residential),
    tax.resource (target 'Commercial:Food' etc., names from finance taxes.byResource), budget (target =
    service name from finance serviceBudgets, 50-150 %), fee (target = name from finance serviceFees),
    loan (to = the new TOTAL loan), policy (target = city policy name from finance cityPolicies, to 1 = on,
    0 = off), policy.slider (same target, to = slider value; the policy must already be on).
    Give the reason, the expected effect and when to check it.
    Then show the player the 'approval' text and wait for an explicit yes before apply_change.
    One change per approval; never bundle several."""
    status = _live("/status")
    if not status or not status.get("cityOpen"):
        raise ToolError("No city is open in the game: changes are only possible while the city runs.")
    code, answer = _act({"action": action, "target": target, "to": to, "dryRun": 1})
    if code != 200 or answer.get("error"):
        raise ToolError(answer.get("error") or f"The mod refused (HTTP {code}).")
    unit = answer.get("unit", "")
    current = answer.get("current")
    label = _label(action, target)
    if current is not None and abs(float(current) - float(to)) < 1e-9:
        return {"unchanged": True, "note": f"{label} is already {_fmt(current, unit)}; nothing to change."}
    pid = os.urandom(3).hex()
    proposal = {"id": pid, "city": status["city"], "action": action, "target": target, "from": current, "to": to,
                "unit": unit, "allowed": answer.get("allowedMinMax"), "label": label, "reason": reason,
                "expectedEffect": expected_effect, "checkIn": check_in, "gameMonth": answer.get("gameMonth"),
                "gameClock": answer.get("gameClock"), "created": time.time()}
    _PROPOSALS[pid] = proposal
    return {
        "proposalId": pid,
        "approval": f"{label}: {_fmt(current, unit)} -> {_fmt(to, unit)}",
        "allowedRange": [_fmt(v, unit) for v in (answer.get("allowedMinMax") or [])],
        "reason": reason, "expectedEffect": expected_effect, "checkIn": check_in,
        "city": status["city"], "gameDate": f"{answer.get('gameMonth')} {answer.get('gameClock')}",
        "next": "Show the player the approval line, reason and expected effect, and wait for an explicit yes. "
                "Only then call apply_change with this proposalId. Valid for 30 minutes.",
    }


@mcp.tool()
def apply_change(proposal_id: str) -> dict[str, Any]:
    """Step 2 of a co-pilot change: make ONE change the player has explicitly approved in chat (from
    propose_change). Refused if the game's value moved since the proposal. The change is written to the
    city journal's decision log (and the mod's actions.jsonl)."""
    p = _PROPOSALS.get(proposal_id)
    if p is None:
        raise ToolError("Unknown proposal id: propose the change again.")
    if time.time() - p["created"] > _PROPOSAL_LIFETIME_S:
        _PROPOSALS.pop(proposal_id, None)
        raise ToolError("This proposal is older than 30 minutes: propose it again so the player sees current numbers.")
    status = _live("/status")
    if not status or status.get("city") != p["city"]:
        raise ToolError(f"The game does not have {p['city']} open any more.")
    code, answer = _act({"action": p["action"], "target": p["target"], "from": p["from"], "to": p["to"]})
    if code != 200 or not answer.get("applied"):
        raise ToolError(answer.get("error") or f"The mod refused (HTTP {code}).")
    _PROPOSALS.pop(proposal_id, None)
    after = answer.get("valueAfter")
    if p["action"] in ("loan", "policy", "policy.slider") and after is not None and abs(float(after) - float(p["to"])) > 1e-3:
        time.sleep(1.5)  # the game applies loan and policy changes on its next update
        code2, check = _act({"action": p["action"], "target": p["target"], "dryRun": 1})
        if code2 == 200:
            after = check.get("current", after)
    unit = p["unit"]
    decision = f"{p['label']} {_fmt(p['from'], unit)} -> {_fmt(after, unit)} (co-pilot, approved {time.strftime('%Y-%m-%d %H:%M')})"
    journal = _journal_decision(p["city"], [answer.get("gameMonth", p["gameMonth"]), decision, p["reason"],
                                            p["expectedEffect"], p["checkIn"], ""])
    result = {"applied": True, "change": f"{p['label']}: {_fmt(p['from'], unit)} -> {_fmt(after, unit)}",
              "gameDate": f"{answer.get('gameMonth')} {answer.get('gameClock')}", "journal": journal,
              "undo": f"propose_change(action='{p['action']}', target='{p['target']}', to={p['from']})"}
    if answer.get("note") and (after is None or abs(float(after) - float(p["to"])) > 1e-3):
        result["note"] = answer["note"]
    if "taxesAfter" in answer:
        moved = {k: [answer["taxesBefore"].get(k), v] for k, v in answer["taxesAfter"].items()
                 if answer["taxesBefore"].get(k) != v and k != {"tax.main": "mainRate", "tax.zone": p["target"],
                                                                 "tax.education": "residential." + p["target"]}.get(p["action"])}
        if moved:
            result["otherTaxRatesThatMoved"] = moved
    return result


if __name__ == "__main__":
    mcp.run("stdio")
