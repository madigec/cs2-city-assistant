using System;
using System.Reflection;
using Game.City;
using Game.UI.InGame;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Layer 2, the remaining domains of the design plan, one file each. Like utilities.json these are the
    /// numbers the game's own panels show (read with InfoviewReader); units are checked against the panels
    /// before the skill relies on them.
    /// </summary>
    public static class DomainExports
    {
        /// <summary>Domain 2, growth and land use: demand and its reasons, population, land value, building levels, wealth.</summary>
        public static JsonObject Growth(GameData g, World world, string trigger)
        {
            var o = g.Header("growth", trigger);
            o.Add("demandBars", GameData.Safe("demandBars", () => DemandBars(world)));
            o.Add("demandFactors", GameData.Safe("demandFactors", () => DemandFactors(world)));
            o.Add("population", GameData.Safe("population", () => InfoviewReader.Read<PopulationInfoviewUISystem>(world)));
            o.Add("landValue", GameData.Safe("landValue", () => InfoviewReader.Read<LandValueInfoviewUISystem>(world)));
            o.Add("buildingLevels", GameData.Safe("buildingLevels", () => InfoviewReader.Read<LevelInfoviewUISystem>(world)));
            o.Add("wealth", GameData.Safe("wealth", () => InfoviewReader.Read<WealthInfoviewUISystem>(world)));
            o.Add("moveAway", GameData.Safe("moveAway", () => HousingExport.MoveAway(g)));
            o.Add("homes", GameData.Safe("homes", () => HousingExport.Homes(world)));
            o.Add("zonedBuildings", GameData.Safe("zonedBuildings", () => HousingExport.Buildings(g)));
            return o;
        }

        /// <summary>Domain 3, economy and industry: jobs by education, company profits, trade, natural resources, tourism.</summary>
        public static JsonObject Economy(GameData g, World world, string trigger)
        {
            var o = g.Header("economy", trigger);
            o.Add("workplaces", GameData.Safe("workplaces", () => InfoviewReader.Read<WorkplacesInfoviewUISystem>(world)));
            o.Add("companies", GameData.Safe("companies", () => InfoviewReader.Read<CompanyInfoviewUISystem>(world)));
            o.Add("trade", GameData.Safe("trade", () => InfoviewReader.Read<OutsideConnectionsInfoviewUISystem>(world)));
            o.Add("naturalResources", GameData.Safe("naturalResources", () => InfoviewReader.Read<NaturalResourcesInfoviewUISystem>(world)));
            o.Add("tourism", GameData.Safe("tourism", () => InfoviewReader.Read<TourismInfoviewUISystem>(world)));
            o.Add("companiesByProduct", GameData.Safe("companiesByProduct", () => CompaniesExport.Build(g)));
            o.Add("efficiencyByZone", GameData.Safe("efficiencyByZone", () => ServiceBuildingsExport.Zoned(g)));
            o.Add("production", GameData.Safe("production", () => EconomyDetailsExport.Production(world)));
            o.Add("commuters", GameData.Safe("commuters", () => EconomyDetailsExport.Commuters(g)));
            o.Add("tourismDetails", GameData.Safe("tourismDetails", () => Wave3Exports.Tourism(g)));
            return o;
        }

        /// <summary>Domain 6, road traffic: traffic flow over the day, car and bike parking.</summary>
        public static JsonObject Traffic(GameData g, World world, string trigger)
        {
            var o = g.Header("traffic", trigger);
            o.Add("traffic", GameData.Safe("traffic", () => InfoviewReader.Read<TrafficInfoviewUISystem>(world)));
            o.Add("roadsAndParking", GameData.Safe("roadsAndParking", () => InfoviewReader.Read<RoadsInfoviewUISystem>(world)));
            o.Add("bikes", GameData.Safe("bikes", () => InfoviewReader.Read<BikesInfoviewUISystem>(world)));
            o.Add("bicycles", GameData.Safe("bicycles", () => TrafficExtrasExport.Bicycles(world)));
            o.Add("accidents", GameData.Safe("accidents", () => TrafficExtrasExport.Accidents(g)));
            o.Add("throughTraffic", GameData.Safe("throughTraffic", () => TrafficExtrasExport.ThroughTraffic(g)));
            o.Add("roadCondition", GameData.Safe("roadCondition", () => TrafficExtrasExport.RoadCondition(g)));
            return o;
        }

        /// <summary>Domain 7, public transport: lines and passengers per transport type, cargo.</summary>
        public static JsonObject Transport(GameData g, World world, string trigger)
        {
            var o = g.Header("transport", trigger);
            o.Add("transport", GameData.Safe("transport", () => InfoviewReader.Read<TransportInfoviewUISystem>(world)));
            o.Add("lines", GameData.Safe("lines", () => TransitExport.Build(g, world)));
            return o;
        }

        /// <summary>Domain 8, environment: average ground, water, air and noise pollution.</summary>
        public static JsonObject Environment(GameData g, World world, string trigger)
        {
            var o = g.Header("environment", trigger);
            o.Add("pollution", GameData.Safe("pollution", () => InfoviewReader.Read<PollutionInfoviewUISystem>(world)));
            o.Add("climate", GameData.Safe("climate", () => Wave3Exports.Climate(world)));
            return o;
        }

        /// <summary>Domain 9, citizens and wellbeing: average happiness and the factors behind it.</summary>
        public static JsonObject Wellbeing(GameData g, World world, string trigger)
        {
            var o = g.Header("wellbeing", trigger);
            o.Add("averageHappiness", GameData.Safe("averageHappiness", () =>
            {
                // The number in the happiness bar at the bottom of the screen (CityInfoUISystem reads the same field).
                return g.TryGetCityComponent<Population>(out var p) ? p.m_AverageHappiness : (object)null;
            }));
            o.Add("happinessFactors", GameData.Safe("happinessFactors", () =>
            {
                foreach (var entry in InfoviewReader.Read<CityInfoUISystem>(world))
                    if (entry.Key == "happinessFactors")
                        return entry.Value;
                return null;
            }));
            o.Add("dailyLife", GameData.Safe("dailyLife", () => Wave3Exports.DailyLife(g)));
            o.Add("chirper", GameData.Safe("chirper", () => Wave3Exports.Chirper(g, world)));
            return o;
        }

        /// <summary>
        /// The six demand bars as drawn, 0-100. The game eases each bar towards its demand value (up slowly,
        /// down five times faster), so a bar can lag behind overview.json's demand for a few game hours.
        /// The bars are plain private fields, not bindings, so InfoviewReader does not see them.
        /// </summary>
        private static JsonObject DemandBars(World world)
        {
            var system = world.GetExistingSystemManaged<CityInfoUISystem>();
            if (system == null)
                throw new InvalidOperationException("CityInfoUISystem not found");

            var result = new JsonObject();
            foreach (var name in new[] { "ResidentialLow", "ResidentialMedium", "ResidentialHigh", "Commercial", "Industrial", "Office" })
            {
                var field = typeof(CityInfoUISystem).GetField("m_" + name + "Demand", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null)
                    throw new MissingFieldException(nameof(CityInfoUISystem), "m_" + name + "Demand");
                result.Add(char.ToLowerInvariant(name[0]) + name.Substring(1), Math.Round((float)field.GetValue(system) * 100.0, 1));
            }
            return result;
        }

        /// <summary>The city info panel's top demand factors per zone type (what the demand bars' tooltips show).</summary>
        private static JsonObject DemandFactors(World world)
        {
            var result = new JsonObject();
            foreach (var entry in InfoviewReader.Read<CityInfoUISystem>(world))
                if (entry.Key != "happinessFactors")
                    result.Add(entry.Key, entry.Value);
            return result;
        }
    }
}
