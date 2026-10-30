# CS2 City Assistant

A city advisor for Cities: Skylines II, built from three parts:

| Part | Folder | What it does |
|---|---|---|
| Game mod (C#) | `CityAssistant/` | Exports the city's numbers (finance, services, utilities, growth, traffic, ...) and map layers (pollution, networks, coverage, zoning) to JSON four times per in-game month, answers live requests on `http://127.0.0.1:51842`, and makes approved co-pilot changes (taxes, budgets, fees, loan, city policies). |
| MCP server (Python) | `CityAssistantMCP/` | Gives Claude tools over that data: live numbers, history, maps, hotspots, layout plans, and the propose/apply pair for co-pilot changes. |
| Claude skill | `.claude/skills/cs2-city-assistant/` | How Claude reads the data, rates each city domain, writes reports and keeps the per-city journal and charter. Packaged as `dist/cs2-city-assistant.skill`. |

The full design and build phases are in `CS2 City Assistant — Design Plan.md`.

## Setup

- Mod: needs the Cities: Skylines II modding toolchain (`dotnet new csiimod` template, `CSII_*` environment variables). `dotnet build CityAssistant/CityAssistant.csproj` builds and deploys it to the local mods folder. Build only while the game is closed (or add `-p:ModPublisherCommand=Update` to compile without deploying).
- MCP server: `python -m venv CityAssistantMCP/.venv`, then `CityAssistantMCP/.venv/Scripts/pip install -r CityAssistantMCP/requirements.txt`. `.mcp.json` registers it with Claude Code.
- Data: the mod writes to `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityAssistant\` (one folder per city: latest exports, `history/`, `journal.md`, `actions.jsonl`).

## Safety of co-pilot changes

Changes go through `POST /act` only, with a random key the mod writes to its data folder at each game start; requests from web pages are refused. Each change is one setting, checked against the game's own limits, refused if the value moved since it was proposed, approved by the player in chat, confirmed again by Claude Code's permission prompt (`.claude/settings.json`), and logged in the city journal and `actions.jsonl`.

The decompiled game code used for research is not part of this repository.
