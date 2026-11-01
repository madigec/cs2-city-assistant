using Game.Buildings;
using Game.Common;
using Game.Events;
using Game.Net;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Traffic additions (Phase 6 hole 8): accidents happening now, traffic that only passes through the city,
    /// bicycles, and the condition of the roads.
    /// </summary>
    public static class TrafficExtrasExport
    {
        /// <summary>Active traffic accident sites (AccidentSite with the TrafficAccident flag; crime scenes use the same component).</summary>
        public static JsonObject Accidents(GameData g)
        {
            int sites = 0, waitingForPolice = 0;
            using (var data = g.AccidentSites.ToComponentDataArray<AccidentSite>(Allocator.Temp))
            {
                foreach (var site in data)
                {
                    if ((site.m_Flags & AccidentSiteFlags.TrafficAccident) == 0)
                        continue;
                    sites++;
                    if ((site.m_Flags & AccidentSiteFlags.Secured) == 0)
                        waitingForPolice++;
                }
            }
            return new JsonObject
            {
                { "activeAccidentSites", sites },
                { "notYetSecuredByPolice", waitingForPolice },
                { "note", "Accidents happening right now, not a monthly count. Where they are: problems.json (Traffic accident icons)." },
            };
        }

        /// <summary>
        /// Moving cars and trucks, and how many of them are through traffic: vehicles the outside connections
        /// send across the city (TrafficSpawnerAISystem gives them the spawner as Owner).
        /// </summary>
        public static JsonObject ThroughTraffic(GameData g)
        {
            var em = g.EntityManager;
            int total = 0, through = 0;
            using (var owners = g.MovingCars.ToComponentDataArray<Owner>(Allocator.Temp))
            {
                foreach (var owner in owners)
                {
                    total++;
                    if (owner.m_Owner != Entity.Null && em.HasComponent<TrafficSpawner>(owner.m_Owner))
                        through++;
                }
            }
            return new JsonObject
            {
                { "roadVehicles", total },
                { "throughTraffic", through },
                { "throughTrafficPercent", total > 0 ? (object)System.Math.Round(100.0 * through / total, 1) : null },
                { "note", "Cars and trucks on the move right now (parked ones left out). Through traffic drives between outside connections and does nothing for the city but load its roads." },
            };
        }

        /// <summary>Bicycles on car roads vs bike-only paths and bike parking (CountVehicleDataSystem).</summary>
        public static JsonObject Bicycles(World world)
        {
            var s = world.GetOrCreateSystemManaged<CountVehicleDataSystem>();
            return new JsonObject
            {
                { "onCarRoads", s.BicycleOnCarRoad },
                { "onBikeOnlyPaths", s.BicycleOnBicycleOnlyRoad },
                { "parkingUsed", s.OccupiedBicycleParkingSpace },
                { "parkingCapacity", s.TotalBicycleParkingSpace },
            };
        }

        /// <summary>Road condition as the road panel shows it, city-wide (wear 0..10 -> 100 - wear x 10 %).</summary>
        public static JsonObject RoadCondition(GameData g)
        {
            var em = g.EntityManager;
            int roads = 0, below75 = 0, below50 = 0, below25 = 0;
            double sum = 0, worst = 100;
            using (var entities = g.Roads.ToEntityArray(Allocator.Temp))
            {
                foreach (var e in entities)
                {
                    if (!em.HasComponent<NetCondition>(e))
                        continue;
                    var wear = em.GetComponentData<NetCondition>(e).m_Wear;
                    double condition = 100.0 - (wear.x + wear.y) * 0.5 * 10.0;
                    roads++;
                    sum += condition;
                    if (condition < worst) worst = condition;
                    if (condition < 75) below75++;
                    if (condition < 50) below50++;
                    if (condition < 25) below25++;
                }
            }
            return new JsonObject
            {
                { "roadSegments", roads },
                { "averageConditionPercent", roads > 0 ? (object)System.Math.Round(sum / roads, 1) : null },
                { "worstConditionPercent", roads > 0 ? (object)System.Math.Round(worst, 1) : null },
                { "segmentsBelow75Percent", below75 },
                { "segmentsBelow50Percent", below50 },
                { "segmentsBelow25Percent", below25 },
                { "note", "Roads wear with time and heavy traffic; road maintenance depots repair them. Worn roads are less safe: wear lowers the road safety score behind accidents (game code RoadSafetySystem). Per road: map-network roads.condition." },
            };
        }
    }
}
