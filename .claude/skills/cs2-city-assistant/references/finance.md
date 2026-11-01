# Finance domain deep dive

Follows the design plan's domain template: sub-areas, metrics, scaling, diagnostic tree, levers, links, data. Use it when Finance is watch or problem, when the player asks about money, and on the first session for any city.

## 1. Sub-areas
- **Income:** taxes (residential, commercial, industrial, office), service fees (electricity, water, garbage, healthcare, education, parking, public transport), exports (electricity, water), government subsidy.
- **Expenses:** service upkeep (by far the largest line in most cities), imports (electricity, water, sewage, emergency services), subsidies to zones, loan interest, map tile upkeep.
- **Debt:** loan amount, interest, creditworthiness (borrowing headroom).
- **Reserves:** cash and runway.

## 2. Leading and lagging metrics
- **Lagging (confirm):** balance, total income, total expenses, cash.
- **Leading (warn early):** expense lines growing faster than income; tax income per citizen falling; import costs appearing (a utility is running short); unemployment rising (tax base will shrink); demand dropping (growth, and with it tax growth, will slow); a GovernmentSubsidy line that is large relative to taxes (it supports small cities and should not be relied on as the city grows).

## 3. Scaling
Always compute and quote:
- balance as % of income (margin);
- income and expenses per citizen;
- each income source and each large expense line as % of the total;
- runway in months when balance < 0.
These make months and cities comparable and keep thresholds valid from 500 to 200,000 citizens.

## 4. Diagnostic tree: "the city is losing money"
Work down the list; stop when the cause is clear, but mention any second cause.

1. **Is this real?** Unlimited money, a `load` export right after a big build, or a one-off (a just-bought map tile, a new expensive building) can make one month look bad. Check the previous month.
2. **Expense side first** (it is usually the cause in young cities):
   - Which service lines in `serviceBudgets` carry the most upkeep? Is any service far larger than the city needs at this population (e.g. a power plant much bigger than consumption, with the surplus exported at a low price)?
   - Are imports costing money (`ImportElectricity`, `Import*Service`)? That points to a missing or too-small facility, which is usually cheaper to build than to keep importing. `ImportWater` is real imported water, not a fixed charge, but a city can import even with ample capacity (see data-reference, Known quirks): report the cost and amount (`utilities.json` `water.waterImport`) without claiming a cause, and suspect a shortage only if water use is not fully supplied.
   - Are any budgets above 100 %?
   - Map tile upkeep and loan interest: are they growing?
3. **Income side:**
   - Tax income per citizen compared with earlier months: falling means unemployment, low education, or businesses struggling.
   - Is one zone type missing? (e.g. no office tax at all: office zones not built or not unlocked.)
   - Fees at or below the default (all fees are adjustable).
   - Electricity/water export income: real money, but it depends on a surplus that shrinks as the city grows.
4. **Growth stage:** young cities often run a deficit until population catches up with the services built for it. If growth is strong and runway is long, "wait and keep growing" can be the right answer; say so explicitly instead of cutting services.

## 5. Levers
| Lever | Where in game | Typical step | Effect and delay | Trade-off |
|---|---|---|---|---|
| Tax rate per zone | Economy panel -> Taxation | 1-2 points | Income changes from the next month; demand reacts over 1-3 months | Higher taxes lower demand for that zone and can lower happiness |
| Residential tax by education level | Taxation, expand Residential | 1-2 points | As above, targeted | Taxing the highly educated harder may push them away |
| Service budget | Economy panel -> Services | 10 % steps | Upkeep changes next month; service efficiency drops immediately | Lower budget = worse coverage, slower response, unhappiness |
| Service fees | Economy panel -> Services / fees | Small steps from default | Income next month | Higher fees lower happiness and can reduce consumption |
| Loan | Economy panel -> Loans | As needed | Cash now, interest every day | Charter decides when debt is acceptable |
| Build a missing utility/service facility | Player builds | - | Removes import costs once running | Upfront cost and its own upkeep |
| Remove or downsize an oversized facility | Player | - | Saves upkeep at once | Check reserves and coverage first |
| Grow the tax base: zone what demand asks for | Player zones | - | Several months | Needs services and jobs to follow |

### Taxes and companies leaving

Source: game code (`CompanyMoveAwaySystem`, `CompanyUtils.GetCompanyMoveAwayChance`, 1.6.1f1), **confirmed in game** on Mega City (1 Nov 2026): with the Financial office tax raised from 10 % to 13 %, the finance offices went from 218 to 181 in 3 game hours (the code predicts about 30 leaving; 37 did), while the 325 software offices left at 10 % lost none. No new finance offices replaced them in that time. Shops, industry and offices (not extractors or warehouses) roll a chance to leave the city 16 times per game day (one day = one month on the calendar). The chance per roll is (product tax - 10) x 2.5 %, rounded down, plus 5 % while the building shows "Lack of Labor". At 10 % tax or less it is 0, so only bankruptcy makes companies leave. Above 10 % it adds up fast:

| Product tax | Chance per roll | Chance to leave within a game day (month) |
|---|---|---|
| 10 % or less | 0 | 0 (bankruptcy only) |
| 11 % | 2 % | about 28 % |
| 12 % | 5 % | about 56 % |
| 14 % | 10 % | about 81 % |

New companies replace them only while that zone's demand allows it, and taxes are one of the demand factors. So a commercial, industrial or office tax above 10 % trades steady companies for higher income per company. economy.json `companiesByProduct` shows each product's current tax, its leave chance and how many companies are moving away now. Check it before proposing a tax above 10 %, and say this trade-off plainly. Residential tax does not use this rule.

Co-pilot note: tax rates (including per product), service budgets, fees, the loan and city policies can be changed by the co-pilot after the player's yes.

### Estimating what a new building will cost
The export has no building prices or per-building upkeep. Don't fill the gap with prices from memory: game balance changes between versions, and a remembered figure presented in a table looks like data. (In testing, an answer built on remembered prices of 300,000-450,000 per station concluded two stations would bankrupt Pedoburg, while the city's own data showed two buildings had just cost about 74,000.) Instead:
- **Construction cost:** compare `cash` between two snapshots around the time something was built; the drop, minus the monthly balance for that period, is roughly what was spent. Label it as an inference.
- **Upkeep:** when a service category's `estimatedUpkeep` jumps from one snapshot to the next, that jump is the new building's upkeep at the current budget. Use similar categories in the same city as the reference.
- **Best source:** ask the player to read the price and upkeep in the build menu tooltip; then replace the estimate.
- Turn it into a rule the player can apply: e.g. "affordable if the new monthly balance stays above X and cash covers at least 12 months of any deficit".

## 6. Links
- Finance is fed by: Growth (more citizens and companies = more tax), Economy (employment and education raise tax per citizen), Utilities (exports earn, imports cost).
- Finance affects: Public services (budgets set efficiency), Growth (taxes steer demand), Citizens (taxes and fees affect happiness), Progression indirectly (money funds the buildings that earn XP).

## 7. Data used
`finance.json`: summary, income, expenses, loan, taxes, serviceFees, serviceBudgets. `overview.json`: population (for per-capita), unemploymentRate, demand, electricity/water (for import/export context). `history\`: same files for previous months.

Wished for in a later mod version: per-building upkeep, the game's fee defaults, loan term and maximum.
