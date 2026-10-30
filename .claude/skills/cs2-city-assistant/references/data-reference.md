# CityAssistant export reference

What each exported field means, its unit, and how much it has been verified. "Verified" means the value was compared with the game's own UI on a real city (Pedoburg, game version 1.6.1f1, October 2026). Treat unverified fields as probably right but say so if a conclusion rests on them.

## Contents
1. Common header
2. overview.json
3. finance.json
4. utilities.json
5. services.json
6. Domain files: growth, economy, traffic, transport, environment, wellbeing
7. Known quirks
8. Older file formats

## 1. Common header (every file)

| Field | Meaning |
|---|---|
| `schema` | `cityassistant.overview/1` or `cityassistant.finance/1` |
| `city` | City name as set in the game |
| `gameMonth` | In-game calendar month, `YYYY-MM`. **Verified** (June/July matched the in-game calendar). |
| `snapshot` | Which of the month's four snapshot slots the export belongs to: `00h`, `06h`, `12h`, `18h` |
| `gameClock` | In-game clock time of the export, `HH:mm` (one in-game day = one month, so 12:00 is mid-month) |
| `trigger` | `load` = exported shortly after a save was loaded (latest files only, never history); `snapshot` = regular snapshot at 00:00/06:00/12:00/18:00 game time (also written to history). Older files may say `month` = the monthly export used before snapshots. |
| `gameHoursSinceLoad` | Game hours since the save was loaded (one month = 24 game hours) |
| `warmingUp` | `true` for the load export and for snapshots less than 3 game hours after loading: head counts and tax income are not settled yet (see Known quirks). The 3-hour line is a guess: 12 minutes was clearly too early, 6 hours was settled. |
| `exportedAt` | Real-world time of the export |
| `conventions` | Reminder: expenses are positive; balance = totalIncome - totalExpenses |

## 2. overview.json

### population
| Field | Meaning | Status |
|---|---|---|
| `population` | Citizens living in the city, the number the game shows | **Verified** (86 shown vs 90 exported; 808 vs 806) |
| `households` | Households that have moved in | consistent with population |
| `movingInCitizens` | Citizens on their way in, not yet counted | |
| `averageHappiness`, `averageHealth` | 0-100 scale; 50 is neutral | |
| `ages.children/teens/adults/seniors` | Citizen counts by age group | |
| `education.*` | Citizen counts by education level (uneducated ... highlyEducated) | |
| `students` | Citizens in education | |
| `workableCitizens` | Citizens who could work | |
| `workers` | Citizens with a job | |
| `unemploymentRate` | **Percent** (29.6 = 29.6 %); equals (workable - workers) / workable | checked arithmetically |
| `homelessCitizens`, `homelessnessRate` | Count and percent | |
| `movingAwayHouseholds` | Households leaving the city right now | |
| `tourists` | Tourists currently in the city | |

### money
`cash` (money in the bank), `unlimitedMoney` (sandbox option: if true, finance analysis is meaningless - say so and skip it), `totalIncome`, `totalExpenses`, `balance` (all per month), `loan` (outstanding amount). **Cash cannot go above 2,000,000,000** (source: game code, `PlayerMoney.kMaxMoney`, 1.6.1f1): a city sitting at exactly that amount loses its whole monthly surplus. Say so, and treat money as no constraint there (spend it rather than protect it). Seen in Mega City 2030-08.

### demand
Game-internal demand values. `residentialBuildingsLowMediumHigh` and the `*Buildings` fields are on a 0-100 scale (100 = strong demand for new buildings; source: game code, `CityInfoUISystem` divides them by 100 to draw the bars). The game's industrial bar shows the larger of `industrialBuildings` and `storageBuildings` (warehouses; field added 2026-10-29, missing from older files). The `*Companies` and `residentialHouseholds` values use other, unverified scales (office companies has shown 12,800-28,800). Use building demand for judgements; use the others only to compare months with each other. What the bars on screen show is in growth.json `demandBars`.

### electricity
`productionMW`, `consumptionMW`, `fulfilledConsumptionMW`. **Verified** against the Electricity panel (4.4 MW and 4.6 MW). Surplus production is exported automatically and earns income (`ExportElectricity` in finance). If `fulfilledConsumptionMW` < `consumptionMW`, buildings are without power. `batteryChargeRaw`, `batteryCapacityRaw` are in 0.1 kWh (divide by 10,000 for MWh; verified on Mega City, 30,000,000 = 3,000 MWh).

### water
`freshCapacity`, `freshConsumption`, `fulfilledFreshConsumption`, `sewageCapacity`, `sewageConsumption`, `fulfilledSewageConsumption`. In **m³ per month**, the same numbers the Water & Sewage panel shows (verified on Mega City 2030-08 18h, exact). Fulfilled < consumption means some buildings lack water or sewage service.

### progression
| Field | Meaning |
|---|---|
| `achievedMilestone`, `nextMilestone` | Milestone numbers |
| `totalXP` | Total XP, the first number on the game's progress bar ("1399 / 2500") |
| `xpAtLastMilestone` | Total XP the current milestone required (750 for milestone 1) |
| `xpForNextMilestone` | Total XP the next milestone requires, the second number on the progress bar |
| `progressToNext` | 0-1 = (totalXP - xpAtLastMilestone) / (xpForNextMilestone - xpAtLastMilestone) |

**Verified:** Pedoburg June showed "1399 / 2500" in game; XP since the last milestone was 649, plus 750 = 1,399, progress 0.37. "XP still needed" = xpForNextMilestone - totalXP. If `allMilestonesReached` is true, the XP fields are null.

## 3. finance.json

All money values are **per in-game month** and match the Economy panel's budget view. **Verified** (income 38,821 and expenses 68,978 matched exactly).

| Section | Contents |
|---|---|
| `summary` | `cash`, `unlimitedMoney`, `totalIncome`, `totalTaxIncome`, `totalExpenses`, `balance`, `moneyDelta` |
| `income` | One line per income source (14): `TaxResidential`, `TaxCommercial`, `TaxIndustrial`, `TaxOffice`, `FeeHealthcare`, `FeeElectricity`, `FeeEducation`, `FeeWater`, `FeeGarbage`, `FeeParking`, `FeePublicTransport`, `ExportElectricity`, `ExportWater`, `GovernmentSubsidy` |
| `expenses` | One line per expense type (15), **positive numbers**: `ServiceUpkeep`, `LoanInterest`, `ImportElectricity`, `ImportWater`, `ExportSewage`, `MapTileUpkeep`, `Subsidy*` (Residential/Commercial/Industrial/Office), `Import*Service` (Police, Ambulance, Hearse, FireEngine, Garbage) |
| `loan` | `amount`, `dailyInterestRate`, `dailyPayment`, `creditworthiness` (the maximum the city can borrow) |
| `taxes` | `mainRate`, `byZone` (Residential/Commercial/Industrial/Office, in %), `allowedLimitsMinMax` (the slider limits, -10 to 30 % in this game version), `residentialByEducation` (rate per education level), `byResource` (Commercial/Industrial/Office: rate per taxed product, the Taxation menu's sub-rows; limits in `allowedLimitsMinMax.perResource`) |
| `serviceFees` | Current fee per service (`Electricity`, `Water`, `Garbage`, `Healthcare`, `BasicEducation`, `SecondaryEducation`, `HigherEducation`, ...). Units differ per service (per-unit price vs. per-visit fee); compare against the game's defaults rather than across services. |
| `cityPolicies` | City-wide policies as the Policies panel lists them: `name` (use for co-pilot changes), `title` (in-game name), `active`, `locked` (+ `unlocksAtMilestone`), and for slider policies `slider` {`value`, `min`, `max`, `step`, `default`}. District and building policies are not included. |
| `serviceBudgets` | One entry per city service: `service`, display `name`, `budgetAdjustable`, `budgetPercent` (100 = default), `estimatedUpkeep` (per month). The upkeep lines add up to `expenses.ServiceUpkeep`. |

Also verified on Mega City (2030-08 18h, all four Economy tabs, exact): `loan.creditworthiness` = the Loans tab's "Limit"; `taxes.byZone` and the tax `income` lines = the Taxation tab's rates and estimates. The **Services tab shows each service's net** = its fee income - its `estimatedUpkeep` (+ `ExportElectricity` for Electricity; e.g. Education 24,165,450 - 27,368,562 = -3,203,112), and its "monthly balance" is the sum of those nets, not the city's balance. If the player quotes a service figure from that tab, compare it with the net, not the upkeep.

`moneyDelta` is the balance paid out in 24 steps per month (`balance` = 24 x `moneyDelta`); use `balance`.

Not exported yet: tax rates per product (commercial/industrial/office resources), building prices and per-building upkeep, which buildings exist, district figures, loan term. See `finance.md` for how to estimate building costs from the city's own data.

## 4. utilities.json

Read from the game's Electricity and Water info views, so the numbers are the ones those panels show. **Verified** on the downloaded test city "Mega City" (2030-08: export at 13:16 vs the player's panel readings at 14:11 - all values within about 1 %, the gap being the hour of play between them).

### electricity
All amounts are in **0.1 kW: divide by 10,000 for MW**.

| Field | Meaning | Check (export / panel) |
|---|---|---|
| `electricityProduction` | Power produced by the city's plants | 1,105 / 1,103 MW |
| `electricityConsumption` | Power demanded by buildings | 752 / 746 MW |
| `electricityTransmitted` | Demand actually delivered; less than consumption means buildings without power | equal to consumption here |
| `electricityExport`, `electricityImport` | Sold to / bought from outside connections | 353 / 357 MW export, 0 import |
| `electricityAvailability`, `electricityTrade` | Panel gauges, -1 to 1 (0.32 here = production well above use) | not compared |
| `batteryCharge` | Stored energy, `current` of `max`, in **0.1 kWh: divide by 10,000 for MWh** | 30,000,000 = 3,000 MWh shown (2030-08 18h) |

### water
All amounts are in **m³ per month**, as the panel shows them (no conversion).

| Field | Meaning | Check (export / panel) |
|---|---|---|
| `waterCapacity` | Fresh water the pumps can deliver ("output" in the panel) | 2,718,259 / 2,735,512 |
| `waterConsumption` | Fresh water used | 2,265,359 / 2,260,308 |
| `sewageCapacity` | Sewage the treatment plants can process ("processing") | 2,986,000 / 2,990,000 |
| `sewageConsumption` | Sewage produced by buildings ("sewage production") | 2,265,359 / 2,260,308 |
| `waterImport`, `waterExport`, `sewageExport` | Traded through outside connections | 0 / 0 |
| `waterAvailability`, `sewageAvailability`, `waterTrade` | Panel gauges, -1 to 1 | not compared |

In Mega City sewage production was exactly equal to water use (observed in data, not checked in game code). A shortage shows as consumption above capacity, or as availability below 0.

## 5. services.json

What the public-service info view panels show, one section per panel, plus `buildings`: every service building type the city has, with `count`, `workers` and `workplaces`, grouped by service category. **Verified** on Felburg (2025-07 06h snapshot vs the player's panel readings, October 2026) and on Mega City (2030-08 18h snapshot vs the player's screenshots of every panel, taken with the game paused at 18:00, 2026-10-29): values are live, not stale, and match the panels exactly unless noted.

- **Gauges** (`...Availability`, `averageCrimeProbability`, `averageFireHazard`): `current` between `min` and `max`, the bar the panel draws. For -1..1 gauges, below 0 means demand exceeds capacity. A panel percentage = `current` / `max` (crime 603.7 of 25,000 = "2 %"; fire hazard 19.48 of 100 = "20 %", the panel may round up by 1).
- **garbage**: amounts in **kg** (verified twice: Felburg `garbageRate` 64,144 = 64.03 t shown; Mega City `garbageRate` 21,097,872 = 21,098 t/month, `processingRate` 88,905,000 = 88,905 t/month). `garbageRate` = garbage produced per month, `processingRate` = what the facilities can process; `capacity`/`storedGarbage` = landfill space (0 when the city has no landfill).
- **healthcareAndDeathcare**: `averageHealth` (%), `sickCount` ("sick or injured"), `patientCount` of `patientCapacity`, `deathRate` (per month), `processingRate` (crematoria, per month), `cemeteryUse` of `cemeteryCapacity` - all exact against the panel.
- **police**: `arrestedCriminals` of `jailCapacity` (jail), `prisoners` ("sentenced") of `prisonCapacity`, `inJail`, `inPrison`, `criminals` ("current number of criminals"), `crimePerMonth`, `escapedRate` ("crime success rate"), gauge `averageCrimeProbability` (0-25,000; / 250 = the panel's %). All exact.
- **fireAndRescue**: only the `averageFireHazard` gauge (0-100 = %).
- **education**: per level (elementary, highSchool, college, university): `...Students` (the number next to the person icon), `...Eligible`, `...Capacity`, gauge; `educationData.values` = citizens by education level [uneducated, poorly, educated, well, highly] (the "education distribution", as counts). Exact (eligible can differ by 1). Students above capacity or availability below 0 = not enough school places.
- **post**: `mailProductionRate` = the panel's "mail rate" per month; the panel's "processing" = `collectedMail` + `deliveredMail` (verified: 630,156 + 630,156 = 1,260,312 shown). Mail rate above processing = mail piling up. Mega City's very first (warm-up) export showed all 0; settled snapshots are fine.
- **telecom**: **always 0 / 0 of 0 - ignore it.** The game never computes this panel value (source: game code, `TelecomInfoviewUISystem.PerformUpdate` is empty in 1.6.1f1; confirmed in game: the Telecom panel shows only a map legend, no numbers). Judge internet service from the `Telecom` happiness factor in wellbeing.json instead.
- **disasterShelters**: `shelteredCount` ("sheltered citizens"), `shelterCapacity`, gauge `shelterAvailability` (free places of capacity = the panel's %). Exact.
- The upkeep cost of each category is in `finance.json` `serviceBudgets`, not here.

## 6. Domain files: growth, economy, traffic, transport, environment, wellbeing

Added 2026-10-29, one file per remaining domain of the design plan. Each is read from the game panel named in brackets, so its field names are the panel's own. **Verified** on Mega City (2030-08 18h snapshot vs the player's screenshots of every panel, game paused at 18:00, 2026-10-29): every number below matched its panel exactly unless noted. Gauges work as in services.json.

- **growth.json** (domain 2)
  - `demandBars`: the six bars next to the zoning icon as drawn, 0-100 (`residentialLow`, `residentialMedium`, `residentialHigh`, `commercial`, `industrial`, `office`). **Verified** (Mega City, 2026-10-29: 100/100/100/0/97/0 = the bars on screen and in the City Information panel). The game moves each bar slowly towards overview.json's building demand (up over roughly two game hours, down faster; source: game code `CityInfoUISystem.AdvanceSmoothDemand`), so a bar can still show demand that has already gone. The industrial bar is the larger of industrial and warehouse demand (Mega City: industrial 0, warehouses 97 -> bar 97). Use `demandBars` when talking about what the player sees, overview.json `demand` for what the game is doing right now.
  - `demandFactors`: the reasons the game gives for each bar, shown on the **Demand tab of the City Information panel** (click the demand-bar button at the bottom left; hovering only shows a colour legend). For `residentialLowFactors`, `residentialMediumFactors`, `residentialHighFactors`, `commercialFactors`, `industrialFactors`, `officeFactors`: the top factors as `{factor, weight}`, strongest first; positive = pushes demand up (green + in game), negative = holds it down (red -). **Verified** (Mega City, 2026-10-29): same factors, same order, same signs as the panel. The panel shows **no numbers**, only + and -; the weights are game-internal (Mega City: -990 to +810), so use them to rank reasons, never as percentages, and don't quote them as something the player can see. Panel names (verified): `EmptyBuildings` = "Unoccupied Buildings", `Happiness` = "Happiness", `FreeWorkplaces` = "Free Workplaces", `Unemployment` = "Low Unemployment Rate", `Students` = "Students", `Homelessness` = "Cost of Living", `Taxes` = "Taxes", `LocalDemand` = "Local Demand", `UneducatedWorkforce` = "Labor Availability", `EducatedWorkforce` = "High-skill Labor Availability", `Warehouses` = "Warehouse Availability". Use the panel's wording with the player. This is the game's own answer to "why is demand low" - quote it rather than guessing.
  - `population` (Population panel): `population`, `employed`, `jobs`, `unemployment` (%), `homeless`, `homelessness` (%), `birthRate`, `deathRate`, `movedIn`, `movedAway` (all per month), `ageData.values` = [children, teens, adults, seniors]. All exact.
  - `landValue.averageLandValue`: **the panel shows this x 240** (213.6221 x 240 = ₡51,269 per cell, exact to the coin; Mega City 2030-08 18h). The 240 comes from the game's interface scripts, not the C# code, so it is a measurement from one city: quote land value as ₡ per cell using x 240, and compare months on the raw number.
  - `buildingLevels`: per zone type (`residentialLevels`, `commercialLevels`, `industrialLevels`, `officeLevels`) `values` = building counts at levels 1-5 and `total`; the panel shows each as a share of `total`. Exact.
  - `wealth` (Household wealth panel): `wealth` (the word, e.g. "Wealthy"), and per household per month `income`, `rent`, `upkeep` (building upkeep costs, can be negative), `resourceCost`, `fees`, in ₡. Exact.
- **economy.json** (domain 3)
  - `workplaces` (Workplace availability panel): `employeesData.values` / `workplacesData.values` = [uneducated, poorly educated, educated, well educated, highly educated, open positions] (shown as "employees / workplaces" per level; the 6th employees value is the open positions), `workplaces` (total workplaces), `workers` (the panel's "employees"; larger than growth.json `employed` because it includes workers from outside the city - unverified explanation). Open positions at one education level next to unemployment at another = education mismatch. Exact.
  - `companies`: profitability gauges for commercial, industrial and office companies, 0-255; the panel's % = `current` / 255 (46.4 = 18 %, 221.7 = 87 %, 224.5 = 88 %).
  - `trade` (Outside connections panel): `topImportNames` / `topExportNames` with amounts in `topImportData` / `topExportData` (same order); the panel shows each as a share of `total`. Units of the amounts unverified (likely kg per month, like cargo). The `...Colors` lists are chart colours, ignore them.
  - `naturalResources`: amounts in **kg** (`availableOre` 19,584,372 = 19.58 kt; renewal rates in kg per month, `forestRenewalRate` 1,677,908 = 1,678 t/month) except `availableFertility` in **m²** (23,352,322 = 23.4 km²). `...ExtractionRate` = the panel's "utilization rate".
  - `tourism`: `attractiveness` gauge (0-100, shown as is), `tourismRate` (tourists per month), `averageHotelPrice`, `weatherEffect` (%; 12.2 = "+12 %").
- **traffic.json** (domain 6)
  - `traffic.trafficFlow`: the Traffic panel's chart at 00:00, 06:00, 12:00, 18:00 and 00:00 again, in **%** (the 5th value is the next midnight, so it can differ from the 1st). Mega City: 52.8-55 %. Exact.
  - `roadsAndParking` (Roads panel): `parkedCars`, `parkingCapacity` (parked above capacity = parking shortage), `parkingIncome` (₡ per month), gauge `parkingAvailability` (-1..1). Exact.
  - `bikes`: `parking` [parked, capacity], gauge `parkingAvailability` (0-1). Both 0 in Mega City, so the order is unconfirmed.
  - There is no per-junction congestion data yet; hotspots belong to the spatial layer (Phase 4).
- **transport.json** (domain 7): `transport.summaries.passengerSummaries` - per transport type (`Taxi`, `Bus`, `Train`, `Tram`, `Subway`, `Ship`, `Ferry`, `Airplane`): `locked`, `lineCount`, `touristCount` and `citizenCount` = **passengers per month**. `cargoSummaries` (truck - the game names it "Bus" - , `Train`, `Ship`, `Airplane`): `lineCount` (routes) and `cargoCount` in **kg per month** (121,403,962 = 121,404 t/month). Exact. The `requirements` entries are unlock conditions, ignore them.
- **environment.json** (domain 8): `pollution` gauges `averageGroundPollution`, `averageWaterPollution`, `averageAirPollution`, `averageNoisePollution`; the panel's % = `current` / `max` (noise 1 of 20 = "5 %"). Mega City has 0 air, ground and water pollution in game too, so only the noise gauge has been seen above 0.
- **wellbeing.json** (domain 9): `averageHappiness` (0-100, the bar at the bottom of the screen; source: game code, `CityInfoUISystem` reads the same `Population.m_AverageHappiness`) and `happinessFactors` (top 10 `{factor, weight}`, negative = making citizens unhappy). Weights are exactly the Happiness panel's numbers (+7 ... -3). The panel names the factors differently - verified on Mega City: `Consumption` = "Wealth", `Mail` = "Reliable mail service", `Education` = "Walking distance to Elementary School", `Entertainment` = "Abundance of entertainment", `Apartment` = "Small homes" (negative), `WaterFee` = "Low water fees", `Tax` = "Fair taxes", `Leisure` = "Abundance of leisure time", `Healthcare` = "Reliable healthcare coverage", `Telecom` = "Reliable internet service". Use the panel's wording when talking to the player. Other factor names (source: game code `CitizenHappinessSystem.HappinessFactor`): `Crime`, `AirPollution`, `TrafficPenalty`, `Homelessness`, `Unemployment`, ... Health and crime are in services.json.

- **districts.json** (added 2026-10-29): one entry per district the player has drawn, with the district's `name` and what the game's district panel shows when the player clicks that district (the mod lets the panel's own sections calculate). **Verified** on Mega City (2 districts, 2026-10-29): the 2030-09 06h snapshot vs the player's panel screenshots at 06:09 - names, happiness word, wealth word, local services and number of policies exact; households, residents, pets, employees and the per-household averages within 0.5 %, except resource cost (2 % - it moves fastest during the day). Sections are `null` when the panel hides them for that district (e.g. a park district with no homes):
  - `residents` (since 2026-10-29 counted by the mod's own fast copy of the game's calculation, because the game's way froze Mega City for 1.4 s; checked field by field against the game's code with 0 differences - the mod's live check `/check/districts` repeats that comparison, worth running after a game update): `householdCount` of `maxHouseholds` (the panel's "households"), `residentCount`, `petCount`, `ageData` / `educationData` (counts per age group / education level, like growth.json), `wealthKey` (the panel's "average wealth" word) and `wealthData` = **district totals per month**; the panel shows them **divided by `householdCount`** (income 4,200 / 4,208 shown, rent 39.4 / 39, fees 4.1 / 4). Verified at 06:00 (Comfortable = Comfortable; income 4,208 / 4,207, upkeep 142.6 / 143). The wealth word and the averages come from the households' current money (source: game code `CitizenUIUtils.GetAverageHouseholdWealth`), and in the **load export** they were wrong ("Modest", upkeep 15 % low) - another warm-up effect: use them only from settled snapshots.
  - `happiness`: `averageHappiness.key` (the word the panel shows: e.g. "Happy") and `happinessFactors` (`{factor, weight}` like wellbeing.json; the district panel does not show these factors - they come from the same game section but are not visible to the player).
  - `employees`: `employeeCount` of `maxEmployees` (the panel's "employees" = workers / workplaces of companies in the district), split by education in `educationDataEmployees` / `educationDataWorkplaces` (same order as economy.json, 6th value = open positions).
  - `localServices`: list of the service building types inside the district (e.g. `Crematorium01`), the panel's "local services".
  - `policies`: list of `{policy, active}` - the district policies the panel offers (the icon row) and whether each is on.
  - Company profitability is not in the district panel in this game version, so it is not exported. An empty `districts` list means the city has no districts. Per-district money (tax by district) is not exported.
  - Use it to say *where* a city-wide problem sits ("happiness 81 overall, but X district is only 'Content'"); compare a district with the city average.

## 7. Known quirks

- **Exports with `trigger: load` show the city still warming up.** Right after a save is loaded, the game itself re-counts its citizens and companies, and its own UI shows the low numbers too. Pedoburg, loaded in June 2025: the load export showed population 90 (with 301 "moving in"), industrial tax 0 and a deficit of -31,357; the next export six game hours later showed population 806, industrial tax 67,286 and a surplus of +75,590. So: use a `load` export only for things that don't depend on head counts (cash, loan, tax rates, budgets, fees, utility capacity). Never compare a warm-up export (`"warmingUp": true`) with a settled one as if it were a trend; the first settled snapshot after loading is the baseline. The warm-up lasts longer than one might think: the Pedoburg 2025-06 18h snapshot, taken 12 game minutes after loading, still showed population 133 (294 "moving in") and industrial tax 0, while 2025-07 00h six hours later showed 794 and 72,059. If only warm-up exports exist, say the picture is provisional and ask the player to play until the next settled snapshot (a few real minutes at full speed).
- **Fast growth distorts unemployment.** New residents arrive before jobs and job-matching catch up; a high unemployment rate in a month with big population growth is often temporary. Check the trend before acting.
- **History holds only snapshots.** `history\YYYY-MM\HHh_*.json` exists only for slots the game actually played through with the mod installed; after-load exports update just the latest files. Missing slots or months mean the player was not playing then, not that nothing happened. A month's first snapshot after loading a save may be `06h` or later.
- **Time of day matters.** Each snapshot is taken at a fixed game time, and one day = one month, so electricity use, commuting and similar numbers differ between `00h` (night), `06h`, `12h` and `18h` partly because of the clock. Compare the same hour across months.
- **Reloading without saving (`history\abandoned\`).** When the player loads a save, any snapshots later than the save's own time were played but never saved (or come from a newer save); the mod moves them to `history\abandoned\<time>\`. Never use those for trends or decision reviews. They are useful in one way: an abandoned run that started from the same save is a "what if" - same city, same starting point, different choices. Comparing it with the saved run at the same game time shows what the player's changes did; label it clearly as a comparison of two runs, not a trend.
- **One folder per city name, not per save.** Two saves of the same city (e.g. "Pedoburg" and "Pedoburg Test") share one folder, history and journal. If the player switches between such saves, the loaded save's time decides what counts as history; ask which save they are playing when the history looks inconsistent.
- **Months can repeat.** After a reload the same month will be exported again when the game reaches it; the newer file replaces the abandoned one. A decision in the journal logged during an unsaved run may not exist in the save; ask the player before reviewing it.
- **Water import cost is real imported water** (source: game code, `WaterTradeSystem.WaterTradeJob` and `CityServiceBudgetSystem`, version 1.6.1f1). `ImportWater` = fresh water flowing into the city's pipe network through an outside water connection x the game's water import price. It is not a fixed charge (an earlier guess of "always 10 % of use" was wrong). A city can import even when its own capacity is larger than its use - why that happens in a given city is not yet known, so report the cost and the imported amount (`utilities.json` `water.waterImport`, verified m³/month) without claiming a cause. Suspect a shortage only when `fulfilledFreshConsumption` < `freshConsumption`.
- **Service budgets are categories, not buildings.** The export shows upkeep per service category (e.g. "Health & Deathcare" covers clinics, hospitals, cemeteries and crematoria; "Garbage Management" covers landfills, recycling and incinerators). A category's upkeep going from 0 to a number means *something* in that category was built, not which building. Never name a building type as fact ("you built a clinic"); say "something in Health & Deathcare" or ask the player.
- **`"error"` sections.** If a section reads `{"error": "..."}`, the mod could not read it (usually after a game update). Report it as missing data; the rest of the file is still valid.

## 8. Older file formats

Check the header first: the format tells you which rules apply.

**Oldest format (header has `gameDate`, no `gameMonth`; written 17 Oct 2026 before about 18:00):**
- `gameDate` is **not** the calendar date. It is an internal day counter shown as a January date, and in CS2 one day is one month, so the **day number is the calendar month**: `2025-01-06` = June 2025 (verified on Pedoburg, where the game showed June), `2025-01-08` = August 2025. Never write "January" for such a file; convert, and say the month is derived.
- `money.totalExpenses` / `summary.totalExpenses` are **negative** (the expense lines are positive). Use the lines' sum as a positive number.
- `electricity.production`, `consumption`, `fulfilledConsumption` are in units of 0.1 kW: divide by 10,000 for MW.
- `population.population` is the city's own counter (right, but lags after loading).
- `progression.currentXP` is XP since the last milestone; with all milestones reached the values are nonsense (seen: -668,941 XP).
- `taxes.byZone.*.min/max` are not the allowed limits (they showed 10/10 while taxes were adjustable); ignore them.
- These files are always `load` exports with no history; treat everything that depends on head counts as provisional.

Before 17 Oct 2026 the history held one file per month directly in `history\` (`YYYY-MM_overview.json`), exported at the month change (equivalent to today's `00h` snapshot).

The first exports (Pedoburg 2025-06 and 2025-07) were written by an earlier mod build:
- the header has no `conventions` line;
- `population` contains `populationCandidates` instead of `population`: the true population is `populationCandidates.cityPopulation`, and `households` there is the household-counter value. Ignore `statistic` and `cityPopulationWithMoveIn` (they did not match the game);
- electricity values are already in MW (`productionMW`).

Exports from before the XP fix (until 17 Oct 2026) have `currentXP` and `nextRequiredXP` in `progression`: `currentXP` is XP since the last milestone (not total XP), and `nextRequiredXP` is the total needed for the next milestone. Total XP = currentXP + the previous milestone's requirement (750 after milestone 1).
