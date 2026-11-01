using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Prefabs;
using Game.Vehicles;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Transform = Game.Objects.Transform;

namespace CityAssistant.Export
{
    /// <summary>
    /// Phase 6 holes 3 and 10: every city service building with its efficiency and what holds it down, its
    /// load (students, patients, bodies, garbage, prisoners, sheltered people, park maintenance), its vehicles
    /// and its installed extensions; plus city-wide efficiency summaries for services and for zoned buildings.
    /// Capacities include active extensions, combined the way the game does it (UpgradeUtils.CombineStats).
    /// </summary>
    public static class ServiceBuildingsExport
    {
        private static bool Combined<T>(EntityManager em, Entity prefab, Entity building, out T data)
            where T : unmanaged, IComponentData, ICombineData<T>
        {
            bool found = em.HasComponent<T>(prefab);
            data = found ? em.GetComponentData<T>(prefab) : default;
            if (!em.HasBuffer<InstalledUpgrade>(building))
                return found;
            var upgrades = em.GetBuffer<InstalledUpgrade>(building, true);
            for (int i = 0; i < upgrades.Length; i++)
            {
                var u = upgrades[i];
                if (BuildingUtils.CheckOption(u, BuildingOption.Inactive) || !em.HasComponent<PrefabRef>(u.m_Upgrade))
                    continue;
                var up = em.GetComponentData<PrefabRef>(u.m_Upgrade).m_Prefab;
                if (!em.HasComponent<T>(up))
                    continue;
                if (found) data.Combine(em.GetComponentData<T>(up));
                else { data = em.GetComponentData<T>(up); found = true; }
            }
            return found;
        }

        /// <summary>BuildingUtils.GetEfficiency(DynamicBuffer): the product of the factors, rounded to whole percent, at least 1 % unless 0.</summary>
        private static float Product(DynamicBuffer<Efficiency> factors)
        {
            float p = 1f;
            for (int i = 0; i < factors.Length; i++)
                p *= Math.Max(0f, factors[i].m_Efficiency);
            return p > 0f ? Math.Max(0.01f, (float)Math.Round(100f * p) / 100f) : 0f;
        }

        private static JsonObject Load(string what, int used, int capacity)
        {
            return new JsonObject
            {
                { "what", what }, { "used", used }, { "capacity", capacity },
                { "percent", capacity > 0 ? (object)Math.Round(100.0 * used / capacity, 1) : null },
            };
        }

        private class Summary
        {
            public int Buildings, Below50, Below80;
            public double EfficiencySum;
            public readonly Dictionary<EfficiencyFactor, (int count, double loss)> Losses = new Dictionary<EfficiencyFactor, (int, double)>();

            public void Add(float efficiency, DynamicBuffer<Efficiency> factors)
            {
                Buildings++;
                EfficiencySum += efficiency;
                if (efficiency < 0.5f) Below50++;
                if (efficiency < 0.8f) Below80++;
                for (int i = 0; i < factors.Length; i++)
                {
                    var f = factors[i];
                    if (f.m_Efficiency >= 0.999f)
                        continue;
                    Losses.TryGetValue(f.m_Factor, out var v);
                    Losses[f.m_Factor] = (v.count + 1, v.loss + (1 - f.m_Efficiency));
                }
            }

            public JsonObject Json()
            {
                var losses = new List<KeyValuePair<EfficiencyFactor, (int count, double loss)>>(Losses);
                losses.Sort((a, b) => b.Value.loss.CompareTo(a.Value.loss));
                var top = new List<object>();
                for (int i = 0; i < losses.Count && i < 6; i++)
                    top.Add(new JsonObject
                    {
                        { "factor", losses[i].Key.ToString() },
                        { "buildings", losses[i].Value.count },
                        { "averageLossPercentWhereItApplies", Math.Round(100 * losses[i].Value.loss / losses[i].Value.count, 1) },
                    });
                return new JsonObject
                {
                    { "buildings", Buildings },
                    { "averageEfficiencyPercent", Buildings > 0 ? (object)Math.Round(100 * EfficiencySum / Buildings, 1) : null },
                    { "below80Percent", Below80 },
                    { "below50Percent", Below50 },
                    { "biggestLosses", top },
                };
            }
        }

        private static string FactorList(DynamicBuffer<Efficiency> factors, out List<object> list)
        {
            list = new List<object>();
            var sorted = new List<Efficiency>();
            for (int i = 0; i < factors.Length; i++)
                if (factors[i].m_Efficiency < 0.999f)
                    sorted.Add(factors[i]);
            sorted.Sort((a, b) => a.m_Efficiency.CompareTo(b.m_Efficiency));
            foreach (var f in sorted)
                list.Add(new JsonObject { { "factor", f.m_Factor.ToString() }, { "percent", Math.Round(100.0 * f.m_Efficiency, 1) } });
            return sorted.Count > 0 ? sorted[0].m_Factor.ToString() : null;
        }

        /// <summary>services.json "buildingDetails" and "efficiencyByService".</summary>
        public static JsonObject Services(GameData g)
        {
            var em = g.EntityManager;
            var list = new List<object>();
            var summaries = new SortedDictionary<string, Summary>();
            var dictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
            using (var entities = g.Buildings.ToEntityArray(Allocator.Temp))
            using (var prefabs = g.Buildings.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            using (var transforms = g.Buildings.ToComponentDataArray<Transform>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    var prefab = prefabs[i].m_Prefab;
                    if (!em.HasComponent<ServiceObjectData>(prefab) || em.HasComponent<SpawnableBuildingData>(prefab))
                        continue;
                    string service = g.Prefabs.GetPrefabName(em.GetComponentData<ServiceObjectData>(prefab).m_Service);
                    string name = g.Prefabs.GetPrefabName(prefab);
                    string title = null;
                    dictionary?.TryGetValue($"Assets.NAME[{name}]", out title);
                    var o = new JsonObject
                    {
                        { "service", service },
                        { "building", title ?? name },
                        { "type", name },
                        { "x", Math.Round(transforms[i].m_Position.x) },
                        { "z", Math.Round(transforms[i].m_Position.z) },
                    };
                    if (em.HasBuffer<Efficiency>(e))
                    {
                        var factors = em.GetBuffer<Efficiency>(e, true);
                        float eff = Product(factors);
                        o.Add("efficiencyPercent", Math.Round(100.0 * eff, 1));
                        FactorList(factors, out var losses);
                        o.Add("losses", losses);
                        if (!summaries.TryGetValue(service, out var s))
                            summaries.Add(service, s = new Summary());
                        s.Add(eff, factors);
                    }
                    if (em.HasComponent<Game.Companies.WorkProvider>(e))
                    {
                        o.Add("workers", em.HasBuffer<Game.Companies.Employee>(e) ? em.GetBuffer<Game.Companies.Employee>(e, true).Length : 0);
                        o.Add("maxWorkers", em.GetComponentData<Game.Companies.WorkProvider>(e).m_MaxWorkers);
                    }
                    var loads = new List<object>();
                    int vehicleCapacity = 0;
                    if (Combined<SchoolData>(em, prefab, e, out var school))
                    {
                        var l = Load("students", em.HasBuffer<Student>(e) ? em.GetBuffer<Student>(e, true).Length : 0, school.m_StudentCapacity);
                        if (em.HasComponent<Game.Buildings.School>(e))
                        {
                            var sc = em.GetComponentData<Game.Buildings.School>(e);
                            // The building panel shows this x 12 as months (0.5 = "6 months"; EducationSection shows 0.5 when 0).
                            l.Add("averageGraduationMonths", Math.Round((sc.m_AverageGraduationTime > 0f ? sc.m_AverageGraduationTime : 0.5f) * 12, 1));
                            l.Add("dropoutPercent", Math.Round(100.0 * sc.m_AverageFailProbability, 1));
                        }
                        loads.Add(l);
                    }
                    if (Combined<HospitalData>(em, prefab, e, out var hospital))
                    {
                        loads.Add(Load("patients", em.HasBuffer<Patient>(e) ? em.GetBuffer<Patient>(e, true).Length : 0, hospital.m_PatientCapacity));
                        vehicleCapacity += hospital.m_AmbulanceCapacity + hospital.m_MedicalHelicopterCapacity;
                    }
                    if (Combined<DeathcareFacilityData>(em, prefab, e, out var deathcare))
                    {
                        int bodies = em.HasComponent<Game.Buildings.DeathcareFacility>(e) ? em.GetComponentData<Game.Buildings.DeathcareFacility>(e).m_LongTermStoredCount : 0;
                        if (em.HasBuffer<Patient>(e)) bodies += em.GetBuffer<Patient>(e, true).Length;
                        loads.Add(Load("bodies", bodies, deathcare.m_StorageCapacity));
                        vehicleCapacity += deathcare.m_HearseCapacity;
                    }
                    if (Combined<GarbageFacilityData>(em, prefab, e, out var garbage))
                    {
                        int stored = em.HasBuffer<Game.Economy.Resources>(e) ? Game.Economy.EconomyUtils.GetResources(Game.Economy.Resource.Garbage, em.GetBuffer<Game.Economy.Resources>(e, true)) : 0;
                        loads.Add(Load("garbage (kg)", stored, garbage.m_GarbageCapacity));
                        vehicleCapacity += garbage.m_VehicleCapacity;
                    }
                    if (Combined<PrisonData>(em, prefab, e, out var prison))
                    {
                        loads.Add(Load("prisoners", em.HasBuffer<Occupant>(e) ? em.GetBuffer<Occupant>(e, true).Length : 0, prison.m_PrisonerCapacity));
                        vehicleCapacity += prison.m_PrisonVanCapacity;
                    }
                    if (Combined<PoliceStationData>(em, prefab, e, out var police))
                    {
                        if (police.m_JailCapacity > 0)
                            loads.Add(Load("jailed", em.HasBuffer<Occupant>(e) ? em.GetBuffer<Occupant>(e, true).Length : 0, police.m_JailCapacity));
                        vehicleCapacity += police.m_PatrolCarCapacity + police.m_PoliceHelicopterCapacity;
                    }
                    if (Combined<EmergencyShelterData>(em, prefab, e, out var shelter))
                    {
                        loads.Add(Load("sheltered", em.HasBuffer<Occupant>(e) ? em.GetBuffer<Occupant>(e, true).Length : 0, shelter.m_ShelterCapacity));
                        vehicleCapacity += shelter.m_VehicleCapacity;
                    }
                    if (Combined<ParkData>(em, prefab, e, out var park) && park.m_MaintenancePool > 0 && em.HasComponent<Game.Buildings.Park>(e))
                        loads.Add(Load("maintenance left", em.GetComponentData<Game.Buildings.Park>(e).m_Maintenance, park.m_MaintenancePool));
                    if (Combined<FireStationData>(em, prefab, e, out var fire))
                        vehicleCapacity += fire.m_FireEngineCapacity + fire.m_FireHelicopterCapacity;
                    if (Combined<PostFacilityData>(em, prefab, e, out var post))
                        vehicleCapacity += post.m_PostVanCapacity + post.m_PostTruckCapacity;
                    if (Combined<TransportDepotData>(em, prefab, e, out var depot))
                        vehicleCapacity += depot.m_VehicleCapacity;
                    if (Combined<MaintenanceDepotData>(em, prefab, e, out var maintenance))
                        vehicleCapacity += maintenance.m_VehicleCapacity;
                    if (loads.Count > 0)
                        o.Add("load", loads);
                    if (em.HasBuffer<OwnedVehicle>(e) || vehicleCapacity > 0)
                    {
                        int owned = 0, parked = 0;
                        var byType = new SortedDictionary<string, int>();
                        if (em.HasBuffer<OwnedVehicle>(e))
                        {
                            var vehicles = em.GetBuffer<OwnedVehicle>(e, true);
                            owned = vehicles.Length;
                            for (int v = 0; v < vehicles.Length; v++)
                            {
                                var ve = vehicles[v].m_Vehicle;
                                if (em.HasComponent<ParkedCar>(ve) || em.HasComponent<ParkedTrain>(ve))
                                {
                                    parked++;
                                    continue;
                                }
                                string vt = em.HasComponent<PrefabRef>(ve) ? g.Prefabs.GetPrefabName(em.GetComponentData<PrefabRef>(ve).m_Prefab) : "?";
                                byType.TryGetValue(vt, out var nv);
                                byType[vt] = nv + 1;
                            }
                        }
                        var outByType = new JsonObject();
                        foreach (var kv in byType) outByType.Add(kv.Key, kv.Value);
                        o.Add("vehicles", new JsonObject { { "capacity", vehicleCapacity }, { "owned", owned }, { "out", owned - parked }, { "outByType", outByType } });
                    }
                    if (em.HasBuffer<Game.Areas.ServiceDistrict>(e))
                    {
                        var limited = em.GetBuffer<Game.Areas.ServiceDistrict>(e, true);
                        if (limited.Length > 0)
                            o.Add("servesOnlyDistricts", limited.Length);
                    }
                    if (em.HasBuffer<InstalledUpgrade>(e))
                    {
                        var upgrades = em.GetBuffer<InstalledUpgrade>(e, true);
                        var names = new List<object>();
                        for (int u = 0; u < upgrades.Length; u++)
                            if (em.HasComponent<PrefabRef>(upgrades[u].m_Upgrade))
                                names.Add(g.Prefabs.GetPrefabName(em.GetComponentData<PrefabRef>(upgrades[u].m_Upgrade).m_Prefab)
                                          + (BuildingUtils.CheckOption(upgrades[u], BuildingOption.Inactive) ? " (off)" : ""));
                        if (names.Count > 0)
                            o.Add("extensions", names);
                    }
                    list.Add(o);
                }
            }
            var byService = new JsonObject();
            foreach (var kv in summaries)
                byService.Add(kv.Key, kv.Value.Json());
            return new JsonObject
            {
                { "efficiencyByService", byService },
                { "buildings", list },
                { "note", "Efficiency is the product of named factors (game code BuildingUtils.GetEfficiency); losses lists the factors below 100 %, worst first. load.percent above 100 can happen (the game lets some facilities overfill). vehicles.out = owned vehicles not parked at the building." },
            };
        }

        /// <summary>economy.json "efficiencyByZone": zoned (company) buildings' efficiency and what holds it down.</summary>
        public static JsonObject Zoned(GameData g)
        {
            var em = g.EntityManager;
            var summaries = new SortedDictionary<string, Summary>();
            using (var entities = g.Buildings.ToEntityArray(Allocator.Temp))
            using (var prefabs = g.Buildings.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    var prefab = prefabs[i].m_Prefab;
                    if (!em.HasComponent<SpawnableBuildingData>(prefab) || !em.HasBuffer<Efficiency>(e))
                        continue;
                    var zonePrefab = em.GetComponentData<SpawnableBuildingData>(prefab).m_ZonePrefab;
                    if (zonePrefab == Entity.Null || !em.HasComponent<ZoneData>(zonePrefab))
                        continue;
                    var zone = em.GetComponentData<ZoneData>(zonePrefab);
                    if (zone.m_AreaType == AreaType.Residential)
                        continue;
                    string key = zone.IsOffice() ? "office" : zone.m_AreaType.ToString().ToLowerInvariant();
                    if (!summaries.TryGetValue(key, out var s))
                        summaries.Add(key, s = new Summary());
                    var factors = em.GetBuffer<Efficiency>(e, true);
                    s.Add(Product(factors), factors);
                }
            }
            var o = new JsonObject();
            foreach (var kv in summaries)
                o.Add(kv.Key, kv.Value.Json());
            return o;
        }
    }
}
