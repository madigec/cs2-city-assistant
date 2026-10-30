# CS2 City Assistant — Design Plan

Oct 17, 2026 · @Laszlo

## Purpose and scope

The assistant reads a vanilla Cities: Skylines II city's statistics, diagnoses problems and recommends decisions; later it can also make approved numeric changes.

- **Game:** vanilla CS2. The only mod is our own data mod; achievements do not matter.
- **Roles:** Advisor by default, Co-pilot for approved changes. No autopilot.
- **Coverage:** ten domains from finance to progression, plus a spatial layer for map-based problems.
- **Out of scope:** drawing roads, zoning and placing buildings. Claude plans these; the player builds them.
- **Who does what:** Claude writes the mod, the server and the playbooks; the player installs, builds and tests.

## City charter

The charter is a short list of priorities that every recommendation is judged against. It starts balanced, because there are no fixed priorities yet, and gets refined as we learn what the city and the player want.

| Question | Options | Starting value |
| --- | --- | --- |
| Financial risk | No debt ever · loans for planned investments · debt for fast growth | Loans for planned investments only |
| Growth pace | Slow and stable · steady · maximum | Steady |
| Citizens vs. cost | Services first · balanced · efficiency first | Balanced |
| Layout style | Realistic · mixed · pure efficiency | Open question |
| Tie-breaker | Domain order in the next section | Top to bottom |

When two goals conflict and the charter does not settle it, Claude names the trade-off and asks rather than choosing silently.

## Domains and health scores

The city is split into ten domains, reviewed in this order; each gets a status of **fine**, **watch** or **problem**, so the whole city reads at a glance.

| # | Domain | What Claude watches | Typical warning sign |
| --- | --- | --- | --- |
| 1 | Finance | Balance, income and expenses by category; taxes by zone, education level and product; loans; utility imports and exports | Monthly deficit, or debt growing faster than income |
| 2 | Growth and land use | Demand per zone type, density, vacancy, rent, land value, homelessness, map expansion | High demand with empty lots, or rising homelessness |
| 3 | Economy and industry | Jobs vs. workers, education match, companies, resources, specialized industry, cargo | Labour shortage or unemployment, companies closing |
| 4 | Public services | Coverage and capacity: health, deathcare, education, police, fire, garbage, post, telecom, administration | Facilities over capacity, uncollected garbage |
| 5 | Utilities | Power, water and sewage: production vs. consumption, reserve margin | Reserve under about 10–15%, buildings without supply |
| 6 | Road traffic | Congestion hotspots, junctions, trip times, parking, freight | Recurring jams at the same junctions |
| 7 | Public transport | Lines, ridership, coverage, waiting times, cost per passenger | Overcrowded or empty lines, long waits |
| 8 | Environment | Air, ground, water and noise pollution; green spaces | Pollution spreading into residential areas |
| 9 | Citizens and wellbeing | Happiness factors, health, crime, leisure, population structure | Falling happiness, people leaving |
| 10 | Progression | Milestones, XP, development points, next unlocks | Growth stalling just before a key unlock |

The exact thresholds for fine, watch and problem are set in the skill and tuned as we play; the numbers above are starting guesses.

### Domain template

Every domain is deepened with the same seven points, so the analysis stays consistent and the mod's data requirements follow from the domains.

1. **Sub-areas:** the parts of the domain, e.g. Finance → income, expenses, debt, reserves.
2. **Leading and lagging metrics:** leading ones warn early (demand dropping), lagging ones confirm (income fell).
3. **Scaling:** per-capita or per-area values, so thresholds work at 10,000 and at 200,000 citizens.
4. **Diagnostic tree:** from a symptom to its likely causes, checked in order.
5. **Levers:** what can be changed, by whom (player or co-pilot), and the typical delay before an effect shows.
6. **Links:** which domains feed this one and which ones it affects.
7. **Data needed:** the exact values the mod must export for this domain.

### How the domains depend on each other

Problems rarely sit where the symptom shows, so Claude follows these links back to the root cause.

&#91;embedded content: dependency map · 10 domains\]

| From | To | Link |
| --- | --- | --- |
| Public services (education) | Economy and industry | Educated workers fill office and high-tech jobs |
| Economy and industry | Growth and land use | Jobs attract residents; shops need customers and workers |
| Utilities | Growth, Economy | Buildings without power or water decline |
| Road traffic | Economy, Public services | Stuck deliveries and service vehicles hurt companies and coverage |
| Public transport | Road traffic | Riders take cars off the roads |
| Environment | Citizens, Growth | Pollution harms health and lowers land value |
| Citizens and wellbeing | Growth | Happy, healthy citizens raise residential demand |
| Growth | Progression | Population growth earns XP and milestones |
| Progression | Foundations | Milestones unlock new services, roads and transit |

### Deep-dive status

- [ ] Finance (next)
- [ ] Growth and land use
- [ ] Economy and industry
- [ ] Public services
- [ ] Utilities
- [ ] Road traffic
- [ ] Public transport
- [ ] Environment
- [ ] Citizens and wellbeing
- [ ] Progression

## Data model

Data is layered so Claude reads a small overview first and drills down only where the overview flags a problem; reading everything every time would be slow and noisy.

| Layer | Contents | When Claude reads it |
| --- | --- | --- |
| 1. Overview | A few dozen key numbers and the ten domain statuses | Every session, first |
| 2. Domain detail | Full breakdown of one domain (e.g. every budget line, every service's capacity) | When that domain is on watch or problem |
| 3. History | Monthly snapshots of the overview and domain numbers | To see trends and check past decisions |
| 4. Spatial | Per-district figures, hotspot lists, grids and networks (next section) | For anything that depends on *where* |

- **Snapshot timing:** once per in-game month, plus on demand when Claude asks.
- **Format:** JSON, one file per layer, with a timestamp and city name in each.
- **Retention:** history kept for the life of the save; older months can be thinned to quarterly.
- **Districts:** the player's own districts are the main unit for per-area figures, so naming districts in the game makes the data much more useful.

## Spatial layer

Map problems are handled by exporting the game's own simulation grids and networks as numbers; Claude analyses them and draws its own annotated maps, with screenshots only to check the result.

The game simulates many map effects on grids of cells, and the coloured overlays are renderings of those numbers. Which grids the mod can read must be confirmed in the game code; the candidates are air, ground and noise pollution, wind, land value and resource coverage.

| Task | Data used | What Claude delivers |
| --- | --- | --- |
| Pollution clouds | Pollution grids, wind, positions of polluting buildings | Which sources hit which homes; where to move or place industry so wind carries it away |
| Power, water, sewage | Network segments with flow and capacity, producers, consumers | Bottlenecks, cut-off areas, reserve margin; proposed new routes drawn on the map |
| Service coverage | Building positions, coverage ranges, capacity | Gaps and overlaps; where the next school, clinic or depot belongs |
| Traffic | Road segments with congestion and flow | Top hotspots and their likely cause; junction or route proposals |
| Zoning and layout | Road network, existing zones, land value, demand | A plan drawing: what to zone where, what to build; the player places it |

- **Shared coordinates:** maps use a labelled grid (A1, B2, …) plus district names, so we can point at exact places in conversation.
- **Rendered maps:** Claude generates images from the data: heatmaps, hotspots marked, proposals drawn on top.
- **Comparisons:** the same map from two dates side by side, to see whether a problem is spreading or shrinking.
- **Screenshots:** through computer use, to check that the data picture matches what the game shows.

## Decision loop and report format

Every session runs the same loop, and every decision is checked a few in-game months later, so the advice improves instead of repeating.

&#91;embedded content: decision loop · 6 steps\]

The player always makes the decision; Claude only acts in the co-pilot role, after approval.

Each report contains:

- **Status board:** the ten domains with fine, watch or problem and one line each.
- **Top issues, ranked by urgency:** now, this year, or strategic.
- **Per recommendation:** what to do, why, expected effect, cost, confidence, and when we check it.
- **Open decisions** still waiting for the player.

## City journal

The journal is one file per city save that Claude reads at the start of every session; it turns one-off advice into strategy across sessions.

- **Charter:** the current priorities, with the date each was last changed.
- **Decision log:** in-game date, decision, reason, expected effect, and the outcome once reviewed.
- **Plans:** longer projects in progress, e.g. "metro line east once income allows".
- **Lessons:** what worked or surprised us in this city, e.g. "demand reacts to tax changes within about two months".

The journal lives in the same folder as the exports. Lessons that hold for any city move into the skill, so every future city benefits.

## Autonomy levels and permissions

Claude works as an advisor by default and becomes a co-pilot only for a short list of numeric settings, each change approved in chat first.

| Level | Claude can | Needs approval |
| --- | --- | --- |
| Advisor | Read all data, write reports and plans, draw maps | Nothing: it changes nothing in the game |
| Co-pilot | Change tax rates, service budgets, loans; possibly city policies | Every single change |
| Autopilot | Not built | — |

Co-pilot rules:

- One change per approval, stated as *from → to* with the expected effect.
- Every change is written to the journal, so it can be reviewed and undone.
- No building, demolishing, zoning or road work. Those stay with the player.
- Which actions the mod exposes is decided per action; anything not on the list cannot be done, by design.

## Architecture

Six parts: a mod inside the game exports data, an MCP server turns it into tools, and Claude combines it with a skill and a city journal.

&#91;embedded content: system architecture · 6 parts\]

Until Phase 3, the MCP server is skipped: Claude reads the exported files directly from a folder connected through the desktop app.

| Part | Built with | Role |
| --- | --- | --- |
| Data mod | C#, CS2 modding toolchain | Reads game data; later applies approved changes |
| Data export | JSON files, later a local endpoint | Hands data out of the game without slowing it |
| MCP server | Python | Query tools such as overview, budget, coverage, history |
| Skill | Text instructions | How to diagnose each domain, thresholds, report format |
| City journal | Text file per save | Memory of charter, decisions and outcomes |

## Build phases and roles

The build runs in six phases, each useful on its own, so the assistant helps from Phase 1 instead of after months of work.

1. **Phase 0 – Setup.** Install the CS2 modding toolchain and build a tiny test mod that writes one line to a file.
   - Done when: the test file appears after loading a save.
2. **Phase 1 – Overview and finance.** The mod exports the overview and the finance detail monthly; Claude reads them from a connected folder. First version of the skill and the journal.
   - Done when: Claude produces a full city report from real data.
3. **Phase 2 – All domains and history.** Detail exports for the other nine domains, per-district figures and monthly history.
   - Done when: every domain has a status and Claude can show trends.
4. **Phase 3 – MCP server.** A Python server gives Claude query tools instead of reading files.
   - Done when: Claude fetches live data on request from the desktop app.
5. **Phase 4 – Spatial layer.** Grids, networks and hotspot lists exported; Claude renders annotated maps.
   - Done when: a pollution or utility problem is solved from the data and maps.
6. **Phase 5 – Co-pilot actions.** The mod accepts the approved list of numeric changes.
   - Done when: an approved tax or budget change happens in-game and lands in the journal.

**Optional, after Phase 5 – Text search (RAG).** Only if it proves useful: a searchable collection of CS2 mechanics notes and patch notes, and search over a long city journal, so Claude retrieves the relevant passages instead of reading everything. May never be needed.

**Roles:** Claude writes all code, the skill and the step-by-step install guides. The player runs the installs and builds, plays, and reports what happens. No programming knowledge is assumed.

## Risks, limits and open questions

The biggest unknown is how much of the game's internal data a mod can reach cleanly; Phase 0 and 1 answer that before anything large is built.

| Risk or limit | Effect | Mitigation |
| --- | --- | --- |
| Game internals are undocumented | Some data may be hard to find or read | Start with the overview; confirm each data source before relying on it |
| Game updates | The mod can break after a patch | Keep the mod small; fix after updates before playing |
| Export cost | Large grids every frame would slow the game | Export monthly or on demand only |
| Estimates, not simulation | Pollution plume and traffic predictions are approximate | Treat them as guidance; verify with the review step |
| Image reading | Claude is not pixel-perfect on screenshots | Prefer data and Claude's own rendered maps |
| Layout work | Precise building via clicks is slow and unreliable | Claude plans, the player builds |

Open questions:

- [ ] Which grids and networks can the mod actually read?
- [ ] Where should the export folder live on the PC?
- [ ] Layout style for the charter: realistic, mixed or pure efficiency?
- [ ] Which city policies, if any, belong on the co-pilot list?
