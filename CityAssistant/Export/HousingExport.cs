using System;
using System.Collections.Generic;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Growth additions (Phase 6 holes 2 and 4): why people leave, the housing stock, and the state of zoned
    /// buildings (construction, decline, abandonment).
    /// </summary>
    public static class HousingExport
    {
        /// <summary>
        /// Citizens who left, per reason: the game's MovedAwayReason statistic (one count per citizen of a
        /// household that had moved in, recorded together with CitizensMovedAway in HouseholdMoveAwaySystem),
        /// plus the households on their way out right now with the reason the game gave them.
        /// </summary>
        public static JsonObject MoveAway(GameData g)
        {
            var perReason = new JsonObject();
            int sum = 0;
            for (var reason = MoveAwayReason.NoSuitableProperty; reason < MoveAwayReason.Count; reason++)
            {
                int value = g.Statistics.GetStatisticValue(StatisticType.MovedAwayReason, (int)reason);
                perReason.Add(reason.ToString(), value);
                sum += value;
            }
            var leaving = new Dictionary<string, int>();
            int leavingTotal = 0;
            using (var reasons = g.MovingAwayHouseholds.ToComponentDataArray<MovingAway>(Allocator.Temp))
            {
                foreach (var m in reasons)
                {
                    string key = m.m_Reason.ToString();
                    leaving.TryGetValue(key, out var n);
                    leaving[key] = n + 1;
                    leavingTotal++;
                }
            }
            var leavingJson = new JsonObject();
            foreach (var kv in leaving)
                leavingJson.Add(kv.Key, kv.Value);
            return new JsonObject
            {
                { "citizensMovedAwayByReason", perReason },
                { "sumOfReasons", sum },
                { "citizensMovedAway", g.Statistics.GetStatisticValue(StatisticType.CitizensMovedAway) },
                { "householdsLeavingNow", leavingTotal },
                { "householdsLeavingNowByReason", leavingJson },
                { "note", "citizensMovedAwayByReason counts citizens of resident households, like the moved-away figure (tourist reasons stay 0). householdsLeavingNow also includes tourist households (Tourist* reasons) and households whose reason is not set yet (None)." },
            };
        }

        /// <summary>Homes free and total per density, homeless shelter places, cheapest free rent (CountResidentialPropertySystem).</summary>
        public static JsonObject Homes(World world)
        {
            var system = world.GetOrCreateSystemManaged<CountResidentialPropertySystem>();
            var data = system.GetResidentialPropertyData();
            return new JsonObject
            {
                { "low", new JsonObject { { "total", data.m_TotalProperties.x }, { "free", data.m_FreeProperties.x } } },
                { "medium", new JsonObject { { "total", data.m_TotalProperties.y }, { "free", data.m_FreeProperties.y } } },
                { "high", new JsonObject { { "total", data.m_TotalProperties.z }, { "free", data.m_FreeProperties.z } } },
                { "shelterPlaces", new JsonObject { { "total", data.m_TotalShelterCapacity }, { "free", data.m_FreeShelterCapacity } } },
                { "cheapestFreeRent", data.m_MinFreeAskingRent },
                { "note", "Homes are households' places (an apartment tower has many). Free = on the market and not taken." },
            };
        }

        private class ZoneCounts
        {
            public int Total, UnderConstruction, Abandoned, Condemned, Collapsed, Declining, NearAbandonment, Growing;
            public double ProgressSum;
            public int ProgressCount;
        }

        /// <summary>
        /// Zoned (growable) buildings per zone: under construction, abandoned, condemned, collapsed, and the
        /// building condition the game uses for levelling up and abandonment (BuildingUpkeepSystem): condition
        /// rises while the renters can pay the building's upkeep and falls while they cannot; at levelling cost
        /// the building levels up, at minus the abandon cost it is abandoned.
        /// </summary>
        public static JsonObject Buildings(GameData g)
        {
            var em = g.EntityManager;
            var zones = new Dictionary<string, ZoneCounts>();
            DynamicBuffer<CityModifier> cityEffects = em.GetBuffer<CityModifier>(g.CityEntity, true);
            using (var entities = g.Buildings.ToEntityArray(Allocator.Temp))
            using (var prefabs = g.Buildings.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var prefab = prefabs[i].m_Prefab;
                    if (!em.HasComponent<SpawnableBuildingData>(prefab))
                        continue;
                    var spawnable = em.GetComponentData<SpawnableBuildingData>(prefab);
                    if (spawnable.m_ZonePrefab == Entity.Null || !em.HasComponent<ZoneData>(spawnable.m_ZonePrefab))
                        continue;
                    var zone = em.GetComponentData<ZoneData>(spawnable.m_ZonePrefab);
                    string name = zone.IsOffice() ? "office" : zone.m_AreaType.ToString().ToLowerInvariant();
                    if (!zones.TryGetValue(name, out var c))
                        zones.Add(name, c = new ZoneCounts());
                    var e = entities[i];
                    c.Total++;
                    if (em.HasComponent<UnderConstruction>(e)) c.UnderConstruction++;
                    if (em.HasComponent<Abandoned>(e)) { c.Abandoned++; continue; }
                    if (em.HasComponent<Destroyed>(e)) { c.Collapsed++; continue; }
                    if (em.HasComponent<Condemned>(e)) c.Condemned++;
                    if (!em.HasComponent<BuildingCondition>(e) || !em.HasComponent<BuildingPropertyData>(prefab))
                        continue;
                    int condition = em.GetComponentData<BuildingCondition>(e).m_Condition;
                    var property = em.GetComponentData<BuildingPropertyData>(prefab);
                    int levelingCost = BuildingUtils.GetLevelingCost(zone.m_AreaType, property, spawnable.m_Level, cityEffects);
                    int abandonCost = BuildingUtils.GetAbandonCost(zone.m_AreaType, property, spawnable.m_Level, levelingCost, cityEffects);
                    if (condition < 0)
                    {
                        c.Declining++;
                        if (abandonCost > 0 && condition <= -abandonCost / 2)
                            c.NearAbandonment++;
                    }
                    else if (condition > 0)
                    {
                        c.Growing++;
                    }
                    if (levelingCost > 0 && condition >= 0 && spawnable.m_Level < 5)
                    {
                        c.ProgressSum += Math.Min(1.0, condition / (double)levelingCost);
                        c.ProgressCount++;
                    }
                }
            }
            var o = new JsonObject();
            foreach (var kv in zones)
            {
                var c = kv.Value;
                o.Add(kv.Key, new JsonObject
                {
                    { "buildings", c.Total },
                    { "underConstruction", c.UnderConstruction },
                    { "abandoned", c.Abandoned },
                    { "condemned", c.Condemned },
                    { "collapsed", c.Collapsed },
                    { "conditionFalling", c.Declining },
                    { "halfwayToAbandonment", c.NearAbandonment },
                    { "conditionAboveZero", c.Growing },
                    { "averageLevelUpProgressPercent", c.ProgressCount > 0 ? (object)Math.Round(100 * c.ProgressSum / c.ProgressCount, 1) : null },
                });
            }
            o.Add("note", "conditionFalling = building condition below 0: since its last level-up its renters have more often been unable than able to pay the building upkeep; halfwayToAbandonment = condition at or below half the abandon cost. Level-up progress = condition / levelling cost, the game's level panel figure, averaged over buildings below level 5.");
            return o;
        }
    }
}
