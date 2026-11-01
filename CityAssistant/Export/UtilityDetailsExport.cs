using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Phase 6 hole 11: utility details the info views do not show. Dirty tap water reaching buildings, power
    /// plants per type (capacity vs what they produce now), pumps, sewage outlets and batteries. Per-building
    /// positions of the same buildings are in map-utilities.
    /// </summary>
    public static class UtilityDetailsExport
    {
        private static EntityQuery Query<T>(EntityManager em) where T : unmanaged, IComponentData
        {
            return em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<T>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        public static JsonObject Build(GameData g)
        {
            var em = g.EntityManager;
            float tolerated = 0.1f;
            var pipeParams = em.CreateEntityQuery(ComponentType.ReadOnly<WaterPipeParameterData>());
            if (pipeParams.CalculateEntityCount() == 1)
                tolerated = pipeParams.GetSingleton<WaterPipeParameterData>().m_MaxToleratedPollution;

            // Dirty water at consumers: DispatchWaterSystem gives the "dirty water" icon and the DirtyWater
            // efficiency factor above m_MaxToleratedPollution.
            int served = 0, dirty = 0;
            double pollutionSum = 0;
            using (var consumers = Query<WaterConsumer>(em).ToComponentDataArray<WaterConsumer>(Allocator.Temp))
            {
                foreach (var c in consumers)
                {
                    if (c.m_FulfilledFresh <= 0)
                        continue;
                    served++;
                    pollutionSum += c.m_Pollution;
                    if (c.m_Pollution > tolerated)
                        dirty++;
                }
            }

            var plants = new SortedDictionary<string, long[]>();   // type -> count, capacity, production
            var q = Query<ElectricityProducer>(em);
            using (var entities = q.ToEntityArray(Allocator.Temp))
            using (var producers = q.ToComponentDataArray<ElectricityProducer>(Allocator.Temp))
            using (var prefabs = q.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (em.HasComponent<Game.Buildings.Battery>(entities[i]))
                        continue;
                    string type = g.Prefabs.GetPrefabName(prefabs[i].m_Prefab);
                    if (!plants.TryGetValue(type, out var a))
                        plants.Add(type, a = new long[3]);
                    a[0]++; a[1] += producers[i].m_Capacity; a[2] += producers[i].m_LastProduction;
                }
            }
            var plantList = new List<object>();
            foreach (var kv in plants)
                plantList.Add(new JsonObject
                {
                    { "type", kv.Key }, { "count", kv.Value[0] },
                    { "capacityMW", Math.Round(kv.Value[1] / 10000.0, 1) }, { "producingMW", Math.Round(kv.Value[2] / 10000.0, 1) },
                    { "outputPercent", kv.Value[1] > 0 ? (object)Math.Round(100.0 * kv.Value[2] / kv.Value[1], 1) : null },
                });

            int pumps = 0, dirtyPumps = 0;
            long pumpCapacity = 0, pumpProduction = 0;
            using (var data = Query<Game.Buildings.WaterPumpingStation>(em).ToComponentDataArray<Game.Buildings.WaterPumpingStation>(Allocator.Temp))
            {
                foreach (var p in data)
                {
                    pumps++;
                    pumpCapacity += p.m_Capacity;
                    pumpProduction += p.m_LastProduction;
                    if (p.m_Pollution > tolerated) dirtyPumps++;
                }
            }
            int outlets = 0;
            long outletCapacity = 0, processed = 0, purified = 0;
            using (var data = Query<Game.Buildings.SewageOutlet>(em).ToComponentDataArray<Game.Buildings.SewageOutlet>(Allocator.Temp))
            {
                foreach (var s in data)
                {
                    outlets++;
                    outletCapacity += s.m_Capacity;
                    processed += s.m_LastProcessed;
                    purified += s.m_LastPurified;
                }
            }
            int batteries = 0;
            long stored = 0, batteryCapacity = 0, flow = 0;
            using (var data = Query<Game.Buildings.Battery>(em).ToComponentDataArray<Game.Buildings.Battery>(Allocator.Temp))
            {
                foreach (var b in data)
                {
                    batteries++;
                    stored += b.m_StoredEnergy;
                    batteryCapacity += b.m_Capacity;
                    flow += b.m_LastFlow;
                }
            }
            return new JsonObject
            {
                { "dirtyTapWater", new JsonObject
                    {
                        { "buildingsServed", served },
                        { "buildingsWithDirtyWater", dirty },
                        { "dirtyPercent", served > 0 ? (object)Math.Round(100.0 * dirty / served, 2) : null },
                        { "averagePollution", served > 0 ? (object)Math.Round(pollutionSum / served, 4) : null },
                        { "toleratedPollution", tolerated },
                        { "note", "Buildings whose delivered water is more polluted than the game tolerates get the dirty-water icon, lose efficiency and their residents' health suffers (game code DispatchWaterSystem)." },
                    }
                },
                { "powerPlants", plantList },
                { "pumps", new JsonObject
                    {
                        { "count", pumps }, { "capacity", pumpCapacity }, { "pumping", pumpProduction },
                        { "pumpingDirtyWater", dirtyPumps },
                        { "note", "Amounts in the game's water units (m³ per month like utilities.water). pumpingDirtyWater = pumps whose output is above the tolerated pollution." },
                    }
                },
                { "sewageOutlets", new JsonObject
                    {
                        { "count", outlets }, { "capacity", outletCapacity }, { "processed", processed }, { "purified", purified },
                        { "purifiedPercent", processed > 0 ? (object)Math.Round(100.0 * purified / processed, 1) : null },
                    }
                },
                { "batteries", new JsonObject { { "count", batteries }, { "storedRaw", stored }, { "capacityRaw", batteryCapacity }, { "flowRaw", flow } } },
                { "note", "Power in MW (the game's 0.1 kW units / 10,000). Output below capacity is normal for plants that follow demand; for wind, solar and hydro it also follows the weather and the water." },
            };
        }
    }
}
