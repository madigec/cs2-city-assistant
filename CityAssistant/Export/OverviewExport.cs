using Game.City;

namespace CityAssistant.Export
{
    /// <summary>
    /// Layer 1 of the data model: a few dozen key numbers Claude reads first every session.
    /// </summary>
    public static class OverviewExport
    {
        public static JsonObject Build(GameData g, string trigger)
        {
            var o = g.Header("overview", trigger);

            o.Add("population", GameData.Safe("population", () =>
            {
                g.TryGetCityComponent<Population>(out var p);
                var h = g.Households;
                return new JsonObject
                {
                    // Population.m_Population is what the game displays. Checked on Pedoburg against the
                    // player's readings: June 17:43 shown 86, exported 90; July 00:05 shown 808, exported 806.
                    // The population statistic did NOT match (306 and 801), so it is not used.
                    { "population", p.m_Population },
                    { "households", h.MovedInHouseholdCount },
                    { "movingInCitizens", h.MovingInCitizenCount },
                    { "averageHappiness", p.m_AverageHappiness },
                    { "averageHealth", p.m_AverageHealth },
                    { "ages", new JsonObject
                        {
                            { "children", h.ChildrenCount },
                            { "teens", h.TeenCount },
                            { "adults", h.AdultCount },
                            { "seniors", h.SeniorCount },
                        } },
                    { "education", new JsonObject
                        {
                            { "uneducated", h.UneducatedCount },
                            { "poorlyEducated", h.PoorlyEducatedCount },
                            { "educated", h.EducatedCount },
                            { "wellEducated", h.WellEducatedCount },
                            { "highlyEducated", h.HighlyEducatedCount },
                        } },
                    { "students", h.StudentCount },
                    { "workableCitizens", h.WorkableCitizenCount },
                    { "workers", h.CityWorkerCount },
                    { "unemploymentRate", h.UnemploymentRate },
                    { "homelessCitizens", h.HomelessCitizenCount },
                    { "homelessnessRate", h.HomelessnessRate },
                    { "movingAwayHouseholds", h.MovingAwayHouseholdCount },
                    { "tourists", h.TouristCitizenCount },
                };
            }));

            o.Add("money", GameData.Safe("money", () =>
            {
                g.TryGetCityComponent<PlayerMoney>(out var money);
                return new JsonObject
                {
                    { "cash", money.money },
                    { "unlimitedMoney", money.m_Unlimited },
                    { "totalIncome", g.Budget.GetTotalIncome() },
                    { "totalExpenses", g.TotalExpenses() },
                    { "balance", g.Budget.GetBalance() },
                    { "loan", g.Loans.CurrentLoan.m_Amount },
                };
            }));

            o.Add("demand", GameData.Safe("demand", () => new JsonObject
            {
                { "residentialHouseholds", g.ResidentialDemand.householdDemand },
                { "residentialBuildingsLowMediumHigh", new[]
                    {
                        g.ResidentialDemand.buildingDemand.x,
                        g.ResidentialDemand.buildingDemand.y,
                        g.ResidentialDemand.buildingDemand.z,
                    } },
                { "commercialCompanies", g.CommercialDemand.companyDemand },
                { "commercialBuildings", g.CommercialDemand.buildingDemand },
                { "industrialCompanies", g.IndustrialDemand.industrialCompanyDemand },
                { "industrialBuildings", g.IndustrialDemand.industrialBuildingDemand },
                { "storageCompanies", g.IndustrialDemand.storageCompanyDemand },
                // The industrial demand bar shows the larger of industrialBuildings and this.
                { "storageBuildings", g.IndustrialDemand.storageBuildingDemand },
                { "officeCompanies", g.IndustrialDemand.officeCompanyDemand },
                { "officeBuildings", g.IndustrialDemand.officeBuildingDemand },
            }));

            // The game counts power in units of 0.1 kW: 44000 = 4.4 MW (checked against the Electricity panel).
            o.Add("electricity", GameData.Safe("electricity", () => new JsonObject
            {
                { "productionMW", g.Electricity.production / 10000.0 },
                { "consumptionMW", g.Electricity.consumption / 10000.0 },
                { "fulfilledConsumptionMW", g.Electricity.fulfilledConsumption / 10000.0 },
                { "batteryChargeRaw", g.Electricity.batteryCharge },
                { "batteryCapacityRaw", g.Electricity.batteryCapacity },
            }));

            o.Add("water", GameData.Safe("water", () => new JsonObject
            {
                { "freshCapacity", g.Water.freshCapacity },
                { "freshConsumption", g.Water.freshConsumption },
                { "fulfilledFreshConsumption", g.Water.fulfilledFreshConsumption },
                { "sewageCapacity", g.Water.sewageCapacity },
                { "sewageConsumption", g.Water.sewageConsumption },
                { "fulfilledSewageConsumption", g.Water.fulfilledSewageConsumption },
            }));

            o.Add("progression", GameData.Safe("progression", () =>
            {
                g.TryGetCityComponent<MilestoneLevel>(out var level);
                // With every milestone reached the game reports nonsense XP values (seen: -668941 XP, next = 0).
                var allReached = g.Milestones.nextRequiredXP <= 0;
                // MilestoneSystem.currentXP is XP since the last milestone; the game's progress bar shows total XP.
                // Pedoburg June: currentXP 649 + last milestone 750 = 1,399, the "1399 / 2500" the game showed.
                return new JsonObject
                {
                    { "achievedMilestone", level.m_AchievedMilestone },
                    { "allMilestonesReached", allReached },
                    { "nextMilestone", allReached ? null : (object)g.Milestones.nextMilestone },
                    { "totalXP", g.City.XP },
                    { "xpAtLastMilestone", allReached ? null : (object)g.Milestones.lastRequiredXP },
                    { "xpForNextMilestone", allReached ? null : (object)g.Milestones.nextRequiredXP },
                    { "progressToNext", allReached ? null : (object)g.Milestones.progress },
                };
            }));

            return o;
        }
    }
}
