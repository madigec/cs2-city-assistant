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

Added with the 2026-10-31 exports (bands are guesses, not from the game):
- **Why people leave** (growth.json `moveAway`): whenever people leave, name the biggest reason in `citizensMovedAwayByReason` (NoSuitableProperty = no home they can afford or fits them, NotHappy, NoMoney, NoAdults). Leaving is almost zero above about 30 happiness (game code), so NotHappy points at a happiness problem, NoSuitableProperty and NoMoney at housing and jobs.
- **Housing** (growth.json `homes`, `zonedBuildings`): **problem** if abandoned + collapsed buildings exceed 2 % of a zone's buildings, or residential `halfwayToAbandonment` above 5 %; **watch** if free homes of a density below 2 % of its homes while that density's demand bar is high (people want to move in but nothing is free), or abandoned buildings exist at all, or `conditionFalling` above 10 % of a zone's buildings. Free homes far above demand (more than 20 % empty) is a different problem: too much zoned for the people who want to come. Homeless with free shelter places = they choose not to use them (leaving soon); without = build shelters or homes.

## 3. Economy and industry
| Status | Rule |
|---|---|
| problem | unemployment > 15 % in two consecutive months (compare the `00h` snapshots); or a company profitability gauge (economy.json `companies`) below 0 two months in a row; or in `companiesByProduct` a product with 5+ companies whose `profitPercent` is below 35 (well under breaking even) |
| watch | unemployment > 8 %; or > 15 % only once / only during fast growth (> 20 % population growth that month); or open positions at one education level above 10 % of its workplaces while unemployment > 8 % (education mismatch) |
| fine | unemployment <= 8 % |

**Company health** (economy.json `companiesByProduct`, added 2026-10-31; bands guessed): **watch** when a product with 5+ companies has `profitPercent` below 50 (losing money on average), staffing below 70 % of `maxWorkers`, or a `leaveChancePerGameDayPercent` above 20 % (companies of that product will keep leaving). Before proposing any product or zone tax change, read that product's row: above 10 % tax, every point raises the leave chance (finance.md, "Taxes and companies leaving").

## 4. Trade and tourism
Informational unless something stands out (bands guessed): **watch** if a product the city's own industry needs is mostly imported while the city could make it (economy.json `production`: `ImportExport` strongly negative, `productionCapacity` low - an opening for that industry), if `commuters.commuterHouseholds` is above 10 % of households (jobs the city's education does not cover), or if `tourismDetails.hotelRoomsUsedPercent` is above 95 (tourists leave when no room is free); **problem** only when trade costs show up as a money problem in Finance. Name the biggest imports and exports, the top attractions by `where`, and the tourism weather effect.

## 5. Public services
Use services.json, one sub-rating per service; the worst sets the domain status. Also note any service with `budgetPercent` below 100 (efficiency is reduced) or above 100 (paying extra).

| Status | Rule |
|---|---|
| problem | any availability gauge below -0.2 (clearly over capacity); garbage `garbageRate` above `processingRate`; cemetery or landfill over 90 % full; students above capacity at any school level |
| watch | any availability gauge between -0.2 and 0.1; cemetery or landfill over 70 % full |
| fine | otherwise |

Skip a service whose numbers are all 0 in a warm-up export (seen for post in Mega City) instead of calling it a problem. Never rate telecom from services.json: the game leaves that value at 0 (see data-reference section 5). Use `map_hotspots("telecom")`: its `summary.shareBelowBaselinePercent` is the share of households whose internet makes them unhappy. Guessed bands (not from the game): problem above 25 %, watch 5-25 %, fine below 5 %. Second check: the `Telecom` happiness factor in wellbeing.json.

**Per building** (services.json `buildingDetails`, added 2026-11-01; bands guessed): **problem** if any school, hospital or jail/prison has `load.percent` above 110, a cemetery or landfill above 95, or a service's `averageEfficiencyPercent` below 60; **watch** if any of those is above 90 (cemetery / landfill above 80), a service's average efficiency is below 80, or most buildings of a service have all their vehicles `out` (calls wait for a free vehicle). Name the building by its `where`, and the main reason from `losses` / `biggestLosses` (e.g. NotEnoughEmployees -> education or wages; ServiceBudget -> budget slider; ElectricitySupply -> power). City totals can look fine while one building is overloaded: always check the list.

## 6. Utilities
Reserve = (production or capacity - consumption) / consumption.

| Status | Rule |
|---|---|
| problem | fulfilled < consumption for electricity, fresh water or sewage (someone is without service) |
| watch | reserve under 15 % for any of them; or importing electricity/water costs more than 5 % of income |
| fine | all reserves >= 15 % and everything fulfilled |

Also (utilities.json `details`, added 2026-11-01; guessed): **problem** if `dirtyTapWater.dirtyPercent` above 5; **watch** above 1, or any pump `pumpingDirtyWater` (move the pump away from sewage outlets and polluted ground, or fix the pollution). Power plants far below capacity while power is short point at their reason: wind, sun, water depth or missing fuel.

## 7. Road traffic
traffic.json covers the city-wide flow chart, parking, bicycles, accidents now, through traffic and road condition; jams at places come from `map_hotspots("traffic")` and the Traffic jam icons (problems). Rate **watch** if `parkingAvailability` or `bikeParkingAvailability` is below 0, if `roadCondition.segmentsBelow50Percent` is above 10 % of segments (no or too few road maintenance depots), or if `throughTraffic.throughTrafficPercent` is above 30 % (outside traffic crowding city roads; a bypass may help). Rate **problem** if more than 25 % of segments are below 50 % condition. Bands are guesses. `accidents` is a moment, not a rate: mention a spot only when the Traffic accident icons cluster there.

## 8. Public transport
transport.json: report per transport type the lines and passengers. **Watch** if a type has lines but almost no passengers (money spent for little use); otherwise informational until the passenger numbers are verified.

Per line (transport.json `lines`, added 2026-11-01; guessed): **watch** if any line has `notEnoughVehicles` (no depot or depot full), a line's `usagePercent` stays above 90 (crowded: more vehicles), or `averageWaitSeconds` is much higher than on the other lines of its type; **problem** if a quarter of the lines are short of vehicles. A line with very low usage and many vehicles costs upkeep for little: suggest fewer vehicles or a new route.

## 9. Environment
environment.json gauges. Until their scale is verified: **watch** if a pollution type appears among the top negative `happinessFactors` (wellbeing.json); **problem** if it is the strongest negative factor. Otherwise fine.

## 10. Citizens and wellbeing
Also (wellbeing.json `dailyLife`, added 2026-11-01): the share of residents per happiness word (Depressed / Sad are the ones who may leave or turn to crime; leaving is almost zero above 30 well-being), `weakHealth`, `overqualifiedPercent` (above 25 %: jobs below the city's education - more offices / high-tech industry would fit), commute times compared over months. Chirper (`chirper.latest`) only as colour.

Happiness (wellbeing.json `averageHappiness`) and health (services.json `averageHealth`) use a 0-100 scale where 50 is neutral. Name the top two negative `happinessFactors` as the main causes.

| Status | Rule |
|---|---|
| problem | average happiness < 40 or average health < 40 |
| watch | either below 50, or happiness fell 5+ points since last month |
| fine | both >= 50 |

## 11. Progression
Informational: report milestone, progress to the next one, and whether progress is moving. "Watch" only if progress has not moved for three months while the city is growing slowly (growth may be stalling just before an unlock).

## 12. Disasters and emergencies
Rate only when disasters.json `naturalDisastersOn` is true or something is happening. **problem** while buildings are on fire in several places, any `buildingsInDanger` or `floodedBuildings`, a weather event over the city, or destroyed buildings not yet rebuilt in numbers (more than 1 % of buildings); **watch** if disasters are on and the city has few shelter places for its population (services `disasterShelters`), no early warning building (`earlyWarningBuildings` 0), or the event journal shows a disaster in the last game months. Use the event journal's `casualties`, `damages` and `cityEffectChange` to say what the last disaster cost. Bands guessed.
