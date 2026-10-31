# Status thresholds (starting guesses)

These turn numbers into fine / watch / problem. They are starting guesses from the design plan, to be tuned as we play; when a threshold produces a status that feels wrong for the city, say so and record the adjustment as a lesson in the journal.

General rules:
- The **worst** rule that fires sets the status.
- If the data for a domain is missing or comes only from a `load` export with known lag, say so in the one-liner and lower your confidence rather than the status.
- Small cities (under about 1,000 citizens) swing a lot month to month; prefer "watch" over "problem" unless the trend over two or more months agrees.

## Problem icons (all domains)

The game's warning icons (problems.json) are the game's own list of what goes wrong. Use them two ways:

1. **The Problems line** at the top of every report: the most serious types first (priority MajorProblem and up), then the most common, each with its count and where (district or worst place). Say "none worth mentioning" when only a few low-priority icons exist.
2. **In the domain ratings.** Each icon type belongs to the domain that fixes it. Rate that domain at least **problem** when a type of priority MajorProblem or higher sits on more than 1 % of buildings, or any type on more than 5 %; at least **watch** when a type of priority Problem or higher sits on more than 0.5 % of buildings, or 5 or more MajorProblem-and-up icons exist. These bands are guesses (not from the game), to be tuned with real cities. Read each type's `title` and `description`, and place it by meaning:
   - Growth and land use: high rent, abandoned, condemned, collapsed, waiting for level-up materials, zoned buildings without road access.
   - Economy and industry: no customers, no inputs / input too expensive, not enough workers.
   - Public services: garbage piling up, landfill or cemetery full, waiting for ambulance or hearse, crime scene, building on fire / burned down, out of supplies, mail piling up, building turned off.
   - Utilities: no electricity, no water, no sewage, dirty water, battery empty, power or pump bottlenecks.
   - Road traffic: traffic bottleneck, dead end, accident, no connection to the outside, lost road connection.
   - Public transport: line short of vehicles, route not found.
   - Environment: pollution icons on homes.
   - A type that fits nowhere: mention it in the Problems line and say which domain you counted it in.

An icon is a symptom, not a cause: say what the game shows, then use the domain's data to explain why (e.g. garbage icons + garbage rate above processing = not enough garbage capacity; garbage icons with spare capacity = trucks can't reach, check traffic).

## 1. Finance
Use `finance.json` summary. "Runway" = cash / monthly deficit.

| Status | Rule |
|---|---|
| problem | balance < 0 and runway < 12 months; or loan growing two months in a row while balance < 0 |
| watch | balance < 0 with runway >= 12 months; or balance between 0 and 5 % of income; or one expense line growing faster than total income over the last months |
| fine | balance >= 5 % of income |
| (skip) | `unlimitedMoney` is true: report "unlimited money - not rated" |

## 2. Growth and land use
| Status | Rule |
|---|---|
| problem | homelessness rate > 3 %; or population fell two months in a row |
| watch | homelessness rate > 1 %; residential building demand high (>= 70) but population flat for two months (zoned land missing?); or movingAwayHouseholds above 2 % of households |
| fine | otherwise |

For any zone type with low demand, name its strongest negative `demandFactors` entry (growth.json) as the reason - it is the game's own explanation.

## 3. Economy and industry
| Status | Rule |
|---|---|
| problem | unemployment > 15 % in two consecutive months (compare the `00h` snapshots); or a company profitability gauge (economy.json `companies`) below 0 two months in a row |
| watch | unemployment > 8 %; or > 15 % only once / only during fast growth (> 20 % population growth that month); or open positions at one education level above 10 % of its workplaces while unemployment > 8 % (education mismatch) |
| fine | unemployment <= 8 % |

## 4. Public services
Use services.json, one sub-rating per service; the worst sets the domain status. Also note any service with `budgetPercent` below 100 (efficiency is reduced) or above 100 (paying extra).

| Status | Rule |
|---|---|
| problem | any availability gauge below -0.2 (clearly over capacity); garbage `garbageRate` above `processingRate`; cemetery or landfill over 90 % full; students above capacity at any school level |
| watch | any availability gauge between -0.2 and 0.1; cemetery or landfill over 70 % full |
| fine | otherwise |

Skip a service whose numbers are all 0 in a warm-up export (seen for post in Mega City) instead of calling it a problem. Never rate telecom from services.json: the game leaves that value at 0 (see data-reference section 5). Use `map_hotspots("telecom")`: its `summary.shareBelowBaselinePercent` is the share of households whose internet makes them unhappy. Guessed bands (not from the game): problem above 25 %, watch 5-25 %, fine below 5 %. Second check: the `Telecom` happiness factor in wellbeing.json.

## 5. Utilities
Reserve = (production or capacity - consumption) / consumption.

| Status | Rule |
|---|---|
| problem | fulfilled < consumption for electricity, fresh water or sewage (someone is without service) |
| watch | reserve under 15 % for any of them; or importing electricity/water costs more than 5 % of income |
| fine | all reserves >= 15 % and everything fulfilled |

## 6. Road traffic
traffic.json covers only the city-wide flow chart and parking, not jams at specific junctions (that needs the map layer, Phase 4). Rate **watch** if `parkingAvailability` or `bikeParkingAvailability` is below 0; otherwise report the flow figures informationally and rely on the player for congestion hotspots.

## 7. Public transport
transport.json: report per transport type the lines and passengers. **Watch** if a type has lines but almost no passengers (money spent for little use); otherwise informational until the passenger numbers are verified.

## 8. Environment
environment.json gauges. Until their scale is verified: **watch** if a pollution type appears among the top negative `happinessFactors` (wellbeing.json); **problem** if it is the strongest negative factor. Otherwise fine.

## 9. Citizens and wellbeing
Happiness (wellbeing.json `averageHappiness`) and health (services.json `averageHealth`) use a 0-100 scale where 50 is neutral. Name the top two negative `happinessFactors` as the main causes.

| Status | Rule |
|---|---|
| problem | average happiness < 40 or average health < 40 |
| watch | either below 50, or happiness fell 5+ points since last month |
| fine | both >= 50 |

## 10. Progression
Informational: report milestone, progress to the next one, and whether progress is moving. "Watch" only if progress has not moved for three months while the city is growing slowly (growth may be stalling just before an unlock).
