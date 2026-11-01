using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Creatures;
using Game.Economy;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Game.Vehicles;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CityAssistant.Export
{
    /// <summary>
    /// The Phase 6 leftovers (2026-11-01): demand per product, money city buildings earn selling goods, how
    /// people travel right now, border connections, parking, and who uses a road (on request).
    /// </summary>
    public static class LeftoverExports
    {
        private static EntityQuery Query(EntityManager em, params ComponentType[] all)
        {
            return em.CreateEntityQuery(new EntityQueryDesc
            {
                All = all,
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        // ---------- demand per product (economy.json "demandByProduct") ----------

        public static JsonObject DemandByProduct(GameData g)
        {
            var ind = g.IndustrialDemand;
            var com = g.CommercialDemand;
            var indCompany = ind.GetResourceDemands(out var d1); d1.Complete();
            var indBuilding = ind.GetBuildingDemands(out var d2); d2.Complete();
            var indConsumption = ind.GetConsumption(out var d3); d3.Complete();
            var storageCompany = ind.GetStorageCompanyDemands(out var d4); d4.Complete();
            var storageBuilding = ind.GetStorageBuildingDemands(out var d5); d5.Complete();
            var comCompany = com.GetResourceDemands(out var d6); d6.Complete();
            var comBuilding = com.GetBuildingDemands(out var d7); d7.Complete();
            var comConsumption = com.GetConsumption(out var d8); d8.Complete();

            var industry = new List<object>();
            var office = new List<object>();
            var commercial = new List<object>();
            for (int i = 0; i < EconomyUtils.ResourceCount; i++)
            {
                var r = EconomyUtils.GetResource(i);
                if (i < indCompany.Length && (indCompany[i] != 0 || indBuilding[i] != 0 || indConsumption[i] != 0 || storageCompany[i] != 0 || storageBuilding[i] != 0))
                {
                    var row = new JsonObject
                    {
                        { "product", r.ToString() },
                        { "companyDemand", indCompany[i] },
                        { "buildingDemand", indBuilding[i] },
                        { "consumption", indConsumption[i] },
                    };
                    if (storageCompany[i] != 0 || storageBuilding[i] != 0)
                    {
                        row.Add("storageCompanyDemand", storageCompany[i]);
                        row.Add("storageBuildingDemand", storageBuilding[i]);
                    }
                    (EconomyUtils.IsOfficeResource(r) ? office : industry).Add(row);
                }
                if (i < comCompany.Length && (comCompany[i] != 0 || comBuilding[i] != 0 || comConsumption[i] != 0))
                    commercial.Add(new JsonObject
                    {
                        { "product", r.ToString() },
                        { "companyDemand", comCompany[i] },
                        { "buildingDemand", comBuilding[i] },
                        { "consumption", comConsumption[i] },
                    });
            }
            Comparison<object> byDemand = (a, b) => Convert.ToInt32(((JsonObject)b).Get("companyDemand")).CompareTo(Convert.ToInt32(((JsonObject)a).Get("companyDemand")));
            industry.Sort(byDemand);
            office.Sort(byDemand);
            commercial.Sort(byDemand);
            return new JsonObject
            {
                { "industry", industry },
                { "office", office },
                { "commercial", commercial },
                { "note", "Per product, from the demand systems the zone demand bars add up (IndustrialDemandSystem, CommercialDemandSystem). companyDemand 0-100 = how much the city wants new companies making/selling it (the sum over products is the company demand bar); buildingDemand > 0 = they need new buildings because no free property fits; consumption = what the city uses (game units). A product with high companyDemand and buildingDemand is what new zoning of that type will be built for." },
            };
        }

        // ---------- money city buildings earn selling goods (finance.json "serviceSales") ----------

        public static JsonObject ServiceSales(GameData g, World world)
        {
            var em = g.EntityManager;
            var resourcePrefabs = world.GetOrCreateSystemManaged<ResourceSystem>().GetPrefabs();
            var dictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
            var names = world.GetExistingSystemManaged<NameSystem>();
            var rows = new List<object>();
            long pendingTotal = 0;
            double maxIncomeTotal = 0, estimatedTotal = 0;
            var q = Query(em, ComponentType.ReadOnly<Game.Buildings.ResourceProducer>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>());
            using (var entities = q.ToEntityArray(Allocator.Temp))
            {
                foreach (var e in entities)
                {
                    var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                    var production = new List<ResourceProductionData>();
                    Collect(em, prefab, production);
                    if (em.HasBuffer<InstalledUpgrade>(e))
                    {
                        var ups = em.GetBuffer<InstalledUpgrade>(e, true);
                        for (int i = 0; i < ups.Length; i++)
                            if (!BuildingUtils.CheckOption(ups[i], BuildingOption.Inactive) && em.HasComponent<PrefabRef>(ups[i].m_Upgrade))
                                Collect(em, em.GetComponentData<PrefabRef>(ups[i].m_Upgrade).m_Prefab, production);
                    }
                    if (production.Count == 0)
                        continue;
                    var resources = em.HasBuffer<Game.Economy.Resources>(e) ? em.GetBuffer<Game.Economy.Resources>(e, true) : default;
                    int money = resources.IsCreated ? EconomyUtils.GetResources(Resource.Money, resources) : 0;
                    pendingTotal += money;
                    var goods = new List<object>();
                    foreach (var p in production)
                    {
                        float price = 0f;
                        var rp = resourcePrefabs[p.m_Type];
                        if (rp != Entity.Null && em.HasComponent<ResourceData>(rp))
                            price = em.GetComponentData<ResourceData>(rp).m_Price.x;
                        double maxIncome = p.m_ProductionRate * (double)price;
                        maxIncomeTotal += maxIncome;
                        goods.Add(new JsonObject
                        {
                            { "resource", p.m_Type.ToString() },
                            { "maxPerMonth", p.m_ProductionRate },
                            { "stock", resources.IsCreated ? EconomyUtils.GetResources(p.m_Type, resources) : 0 },
                            { "storageCapacity", p.m_StorageCapacity },
                            { "salePrice", Math.Round(price, 3) },
                            { "maxIncomePerMonth", Math.Round(maxIncome) },
                        });
                    }
                    // How busy it is: the garbage panel's "processing speed" (GarbageSection: m_ProcessingRate of
                    // GarbageFacilityData.m_ProcessingSpeed incl. active upgrades). Output scales with it.
                    double? share = null;
                    if (em.HasComponent<Game.Buildings.GarbageFacility>(e))
                    {
                        int speed = em.HasComponent<GarbageFacilityData>(prefab) ? em.GetComponentData<GarbageFacilityData>(prefab).m_ProcessingSpeed : 0;
                        if (em.HasBuffer<InstalledUpgrade>(e))
                        {
                            var ups = em.GetBuffer<InstalledUpgrade>(e, true);
                            for (int i = 0; i < ups.Length; i++)
                                if (!BuildingUtils.CheckOption(ups[i], BuildingOption.Inactive) && em.HasComponent<PrefabRef>(ups[i].m_Upgrade))
                                {
                                    var up = em.GetComponentData<PrefabRef>(ups[i].m_Upgrade).m_Prefab;
                                    if (em.HasComponent<GarbageFacilityData>(up))
                                        speed += em.GetComponentData<GarbageFacilityData>(up).m_ProcessingSpeed;
                                }
                        }
                        if (speed > 0)
                            share = Math.Min(1.0, em.GetComponentData<Game.Buildings.GarbageFacility>(e).m_ProcessingRate / (double)speed);
                    }
                    double maxBuilding = 0;
                    foreach (var gd in goods)
                        maxBuilding += Convert.ToDouble(((JsonObject)gd).Get("maxIncomePerMonth"));
                    if (share.HasValue)
                        estimatedTotal += maxBuilding * share.Value;
                    var prefabName = g.Prefabs.GetPrefabName(prefab);
                    string title = null;
                    dictionary?.TryGetValue($"Assets.NAME[{prefabName}]", out title);
                    var pos = em.GetComponentData<Transform>(e).m_Position;
                    rows.Add(new JsonObject
                    {
                        { "building", Label(g, names, e) },
                        { "type", title ?? prefabName },
                        { "goods", goods },
                        { "processingPercent", share.HasValue ? (object)Math.Round(100 * share.Value, 1) : null },
                        { "estimatedIncomePerMonth", share.HasValue ? (object)Math.Round(maxBuilding * share.Value) : null },
                        { "moneyNotYetPaidIn", money },
                        { "x", Math.Round(pos.x, 1) },
                        { "z", Math.Round(pos.z, 1) },
                    });
                }
            }
            return new JsonObject
            {
                { "buildings", rows },
                { "maxIncomePerMonthTotal", Math.Round(maxIncomeTotal) },
                { "estimatedIncomePerMonthTotal", Math.Round(estimatedTotal) },
                { "moneyNotYetPaidInTotal", pendingTotal },
                { "note", "City buildings that make goods (recycling centres, prisons...). The goods are sold once the stock reaches min(20,000, storage) at the product's industrial price (ResourceExporterSystem); the money collects in the building and goes straight into the city's cash in steps of more than 10,000 (ResourceProducerSystem) - it shows up in NO income line of the budget. maxPerMonth = production at full processing (the game adds rate/1024 per update, 1,024 updates a day); actual output is lower when the building has less to process. maxIncomePerMonth = maxPerMonth x sale price (upper bound); for garbage buildings processingPercent = the panel's processing speed share and estimatedIncomePerMonth = max x that share (checked: Recycling Center panel 908 / 2,000 t/month)." },
            };
        }

        private static void Collect(EntityManager em, Entity prefab, List<ResourceProductionData> into)
        {
            if (!em.HasBuffer<ResourceProductionData>(prefab))
                return;
            var buffer = em.GetBuffer<ResourceProductionData>(prefab, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                int found = into.FindIndex(p => p.m_Type == buffer[i].m_Type);
                if (found < 0)
                    into.Add(buffer[i]);
                else
                {
                    var p = into[found];
                    p.m_ProductionRate += buffer[i].m_ProductionRate;
                    p.m_StorageCapacity += buffer[i].m_StorageCapacity;
                    into[found] = p;
                }
            }
        }

        // ---------- vehicle and destination kinds ----------

        private static string VehicleKind(EntityManager em, Entity v)
        {
            if (em.HasComponent<Bicycle>(v)) return "bicycle";
            if (em.HasComponent<Game.Vehicles.PersonalCar>(v)) return "car";
            if (em.HasComponent<Game.Vehicles.Taxi>(v)) return "taxi";
            if (em.HasComponent<Game.Vehicles.PublicTransport>(v) || em.HasComponent<Game.Vehicles.CargoTransport>(v))
            {
                var e = v;
                if (em.HasComponent<Controller>(v))
                {
                    var c = em.GetComponentData<Controller>(v).m_Controller;
                    if (c != Entity.Null)
                        e = c;
                }
                string type = null;
                if (em.HasComponent<PrefabRef>(e))
                {
                    var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (em.HasComponent<PublicTransportVehicleData>(prefab))
                        type = em.GetComponentData<PublicTransportVehicleData>(prefab).m_TransportType.ToString().ToLowerInvariant();
                    else if (em.HasComponent<Game.Vehicles.Train>(e))
                        type = "cargo train";
                    else if (em.HasComponent<Game.Vehicles.Watercraft>(e))
                        type = "cargo ship";
                    else if (em.HasComponent<Game.Vehicles.Aircraft>(e))
                        type = "cargo plane";
                }
                return type ?? (em.HasComponent<Game.Vehicles.CargoTransport>(v) ? "cargo" : "public transport");
            }
            if (em.HasComponent<Game.Vehicles.DeliveryTruck>(v)) return "delivery truck";
            return "service vehicle";
        }

        /// <summary>Climbs from a lane, spawn point or sub-object to the building or border it belongs to.</summary>
        private static Entity Top(EntityManager em, Entity e)
        {
            for (int i = 0; i < 4 && e != Entity.Null; i++)
            {
                if (em.HasComponent<Game.Objects.OutsideConnection>(e) || em.HasComponent<Game.Net.OutsideConnection>(e) || em.HasComponent<Building>(e))
                    return e;
                if (em.HasComponent<Owner>(e))
                    e = em.GetComponentData<Owner>(e).m_Owner;
                else if (em.HasComponent<Game.Objects.Attached>(e))
                    e = em.GetComponentData<Game.Objects.Attached>(e).m_Parent;
                else
                    break;
            }
            return e;
        }

        private static string PlaceKind(EntityManager em, Entity place)
        {
            if (place == Entity.Null) return "unknown";
            if (em.HasComponent<Game.Objects.OutsideConnection>(place) || em.HasComponent<Game.Net.OutsideConnection>(place)) return "outside the city";
            if (em.HasComponent<Game.Net.Edge>(place) || em.HasComponent<Game.Net.Node>(place) || em.HasComponent<Game.Net.Lane>(place)) return "a road (work on it, or a stop)";
            if (!em.HasComponent<Building>(place) || !em.HasComponent<PrefabRef>(place)) return "other";
            var prefab = em.GetComponentData<PrefabRef>(place).m_Prefab;
            if (em.HasComponent<ResidentialProperty>(place)) return "homes";
            if (em.HasComponent<SpawnableBuildingData>(prefab))
            {
                var zone = em.GetComponentData<SpawnableBuildingData>(prefab).m_ZonePrefab;
                if (zone != Entity.Null && em.HasComponent<ZoneData>(zone))
                {
                    var z = em.GetComponentData<ZoneData>(zone);
                    if ((z.m_ZoneFlags & ZoneFlags.Office) != 0) return "offices";
                    switch (z.m_AreaType)
                    {
                        case AreaType.Commercial: return "shops";
                        case AreaType.Industrial: return "industry";
                        case AreaType.Residential: return "homes";
                    }
                }
            }
            if (em.HasBuffer<Renter>(place))
            {
                var renters = em.GetBuffer<Renter>(place, true);
                for (int i = 0; i < renters.Length; i++)
                {
                    var r = renters[i].m_Renter;
                    if (em.HasComponent<Game.Companies.CommercialCompany>(r)) return "shops";
                    if (em.HasComponent<Game.Companies.OfficeCompany>(r)) return "offices";
                    if (em.HasComponent<Game.Companies.IndustrialCompany>(r) || em.HasComponent<Game.Companies.ExtractorCompany>(r)) return "industry";
                }
            }
            if (em.HasComponent<Game.Buildings.ParkingFacility>(place)) return "parking";
            if (em.HasComponent<ServiceObjectData>(prefab)) return "city services";
            return "other";
        }

        /// <summary>The name the game shows, or the building type when the game has no proper label for it.</summary>
        public static string Label(GameData g, NameSystem names, Entity e)
        {
            var em = g.EntityManager;
            string prefabName = em.HasComponent<PrefabRef>(e) ? g.Prefabs.GetPrefabName(em.GetComponentData<PrefabRef>(e).m_Prefab) : null;
            string label = names != null ? names.GetRenderedLabelName(e) : null;
            if (!string.IsNullOrEmpty(label) && !label.StartsWith("Assets.NAME["))
                return label;
            var dictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
            string title = null;
            if (prefabName != null)
                dictionary?.TryGetValue($"Assets.NAME[{prefabName}]", out title);
            return title ?? prefabName ?? e.ToString();
        }

        /// <summary>Entity position: object, net node, or the middle of a curve; else its owner's.</summary>
        private static float3? Position(EntityManager em, Entity e)
        {
            for (int i = 0; i < 4 && e != Entity.Null; i++)
            {
                if (em.HasComponent<Transform>(e)) return em.GetComponentData<Transform>(e).m_Position;
                if (em.HasComponent<Game.Net.Node>(e)) return em.GetComponentData<Game.Net.Node>(e).m_Position;
                if (em.HasComponent<Curve>(e)) return MathUtils.Position(em.GetComponentData<Curve>(e).m_Bezier, 0.5f);
                if (!em.HasComponent<Owner>(e)) break;
                e = em.GetComponentData<Owner>(e).m_Owner;
            }
            return null;
        }

        /// <summary>Whether a vehicle comes from outside the city: owned by a border connection, or a tourist's or commuter's car.</summary>
        private static string Origin(EntityManager em, Entity vehicle)
        {
            if (!em.HasComponent<Owner>(vehicle))
                return null;
            var owner = em.GetComponentData<Owner>(vehicle).m_Owner;
            if (owner == Entity.Null) return null;
            var top = Top(em, owner);
            if (em.HasComponent<Game.Objects.OutsideConnection>(top) || em.HasComponent<Game.Net.OutsideConnection>(top)) return "outside connection";
            if (em.HasComponent<TouristHousehold>(owner)) return "tourist";
            if (em.HasComponent<CommuterHousehold>(owner)) return "commuter";
            return null;
        }

        // ---------- how people travel right now (traffic.json "modeShare") ----------

        public static JsonObject ModeShare(GameData g)
        {
            var em = g.EntityManager;
            var residents = new SortedDictionary<string, int>();
            var tourists = new SortedDictionary<string, int>();
            int total = 0;
            var q = Query(em, ComponentType.ReadOnly<Human>(), ComponentType.ReadOnly<Game.Creatures.Resident>());
            using (var entities = q.ToEntityArray(Allocator.Temp))
            using (var res = q.ToComponentDataArray<Game.Creatures.Resident>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if ((res[i].m_Flags & ResidentFlags.DummyTraffic) != 0)
                        continue;
                    string mode;
                    if (em.HasComponent<CurrentVehicle>(entities[i]))
                        mode = VehicleKind(em, em.GetComponentData<CurrentVehicle>(entities[i]).m_Vehicle);
                    else if ((res[i].m_Flags & ResidentFlags.WaitingTransport) != 0)
                        mode = "waiting for transit";
                    else
                        mode = "walking";
                    bool tourist = false;
                    var citizen = res[i].m_Citizen;
                    if (citizen != Entity.Null && em.HasComponent<HouseholdMember>(citizen))
                        tourist = em.HasComponent<TouristHousehold>(em.GetComponentData<HouseholdMember>(citizen).m_Household);
                    var into = tourist ? tourists : residents;
                    into.TryGetValue(mode, out var n);
                    into[mode] = n + 1;
                    total++;
                }
            }
            JsonObject Shares(SortedDictionary<string, int> d)
            {
                int sum = 0;
                foreach (var v in d.Values) sum += v;
                var o = new JsonObject();
                var sorted = new List<KeyValuePair<string, int>>(d);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (var kv in sorted)
                    o.Add(kv.Key, new JsonObject { { "people", kv.Value }, { "percent", sum > 0 ? Math.Round(100.0 * kv.Value / sum, 1) : 0.0 } });
                return o;
            }
            return new JsonObject
            {
                { "peopleOnTheMove", total },
                { "residents", Shares(residents) },
                { "tourists", Shares(tourists) },
                { "note", "A snapshot: every citizen outside a building right now and how they travel (riding in a vehicle by its type, waiting at a stop, or walking; walking includes the walk to and from a parked car or stop). The game keeps no history of car trips, so compare snapshots at the same time of day. Transit statistics (passengers per month) are in statistics.json." },
            };
        }

        // ---------- border connections (traffic.json "borders") ----------

        public static JsonObject Borders(GameData g, World world)
        {
            var em = g.EntityManager;
            var names = world.GetExistingSystemManaged<NameSystem>();
            var connections = new Dictionary<Entity, JsonObject>();
            var outCounts = new Dictionary<Entity, SortedDictionary<string, int>>();
            var inCounts = new Dictionary<Entity, SortedDictionary<string, int>>();
            // Road, rail and harbour borders are owned by their road/track piece, so owned ones count too
            // (only the outermost connection object of a group is listed).
            var positions = new List<(Entity e, float3 p)>();
            var cq = Query(em, ComponentType.ReadOnly<Game.Objects.OutsideConnection>(), ComponentType.ReadOnly<PrefabRef>());
            using (var entities = cq.ToEntityArray(Allocator.Temp))
            {
                foreach (var e in entities)
                {
                    if (Root(em, e) != e)
                        continue;
                    var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (!em.HasComponent<OutsideConnectionData>(prefab))
                        continue; // power lines and pipes carry no vehicles
                    string type = em.GetComponentData<OutsideConnectionData>(prefab).m_Type.ToString();
                    var p = Position(em, e);
                    var row = new JsonObject
                    {
                        { "name", Label(g, names, e) },
                        { "type", type },
                    };
                    if (p.HasValue)
                    {
                        row.Add("x", Math.Round(p.Value.x, 1));
                        row.Add("z", Math.Round(p.Value.z, 1));
                        positions.Add((e, p.Value));
                    }
                    connections[e] = row;
                    outCounts[e] = new SortedDictionary<string, int>();
                    inCounts[e] = new SortedDictionary<string, int>();
                }
            }
            Entity Match(Entity place)
            {
                if (place == Entity.Null) return Entity.Null;
                if (em.HasComponent<Game.Objects.OutsideConnection>(place))
                {
                    var r = Root(em, place);
                    if (connections.ContainsKey(r)) return r;
                }
                if (em.HasComponent<Game.Objects.OutsideConnection>(place) || em.HasComponent<Game.Net.OutsideConnection>(place))
                {
                    var p = Position(em, place);
                    if (!p.HasValue) return Entity.Null;
                    Entity best = Entity.Null;
                    float bestD = 600f * 600f;
                    foreach (var c in positions)
                    {
                        float d = math.distancesq(c.p.xz, p.Value.xz);
                        if (d < bestD) { bestD = d; best = c.e; }
                    }
                    return best;
                }
                return Entity.Null;
            }
            var vq = Query(em, ComponentType.ReadOnly<Vehicle>(), ComponentType.ReadOnly<Target>());
            using (var vehicles = vq.ToEntityArray(Allocator.Temp))
            using (var targets = vq.ToComponentDataArray<Target>(Allocator.Temp))
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    if (em.HasComponent<ParkedCar>(vehicles[i]) || em.HasComponent<ParkedTrain>(vehicles[i]))
                        continue;
                    string kind = null;
                    var to = Match(Top(em, targets[i].m_Target));
                    if (to != Entity.Null && outCounts.TryGetValue(to, out var o))
                    {
                        kind = VehicleKind(em, vehicles[i]);
                        o.TryGetValue(kind, out var n);
                        o[kind] = n + 1;
                    }
                    if (em.HasComponent<Owner>(vehicles[i]))
                    {
                        var from = Match(Top(em, em.GetComponentData<Owner>(vehicles[i]).m_Owner));
                        if (from != Entity.Null && inCounts.TryGetValue(from, out var c))
                        {
                            kind = kind ?? VehicleKind(em, vehicles[i]);
                            c.TryGetValue(kind, out var n);
                            c[kind] = n + 1;
                        }
                    }
                }
            }
            var list = new List<object>();
            foreach (var kv in connections)
            {
                int outTotal = 0, inTotal = 0;
                var outO = new JsonObject();
                foreach (var c in outCounts[kv.Key]) { outO.Add(c.Key, c.Value); outTotal += c.Value; }
                var inO = new JsonObject();
                foreach (var c in inCounts[kv.Key]) { inO.Add(c.Key, c.Value); inTotal += c.Value; }
                kv.Value.Add("vehiclesHeadingOut", outTotal);
                kv.Value.Add("headingOutByKind", outO);
                kv.Value.Add("vehiclesFromHere", inTotal);
                kv.Value.Add("fromHereByKind", inO);
                list.Add(kv.Value);
            }
            list.Sort((a, b) => Convert.ToInt32(((JsonObject)b).Get("vehiclesHeadingOut")).CompareTo(Convert.ToInt32(((JsonObject)a).Get("vehiclesHeadingOut"))));
            return new JsonObject
            {
                { "connections", list },
                { "note", "Every border connection (Road, Train, Air, Ship; electricity and water too). vehiclesHeadingOut = vehicles in the city whose destination is this connection now (exports, people leaving); vehiclesFromHere = vehicles that belong to it (imports and service traffic it sent in); tourists' and commuters' own cars are not counted here. Through the tools each road connection also gets the traffic flow on the road next to it." },
            };
        }

        private static Entity Root(EntityManager em, Entity e)
        {
            for (int i = 0; i < 4 && em.HasComponent<Owner>(e); i++)
            {
                var o = em.GetComponentData<Owner>(e).m_Owner;
                if (o == Entity.Null || !em.HasComponent<Game.Objects.OutsideConnection>(o))
                    break;
                e = o;
            }
            return e;
        }

        // ---------- parking (traffic.json "parking") and per road (map-network) ----------

        public struct ParkingLookups
        {
            public ComponentLookup<Game.Net.ParkingLane> ParkingLanes;
            public ComponentLookup<PrefabRef> Prefabs;
            public ComponentLookup<Curve> Curves;
            public ComponentLookup<ParkingLaneData> LaneData;
            public ComponentLookup<ParkedCar> ParkedCars;
            public ComponentLookup<GarageLane> Garages;
            public BufferLookup<LaneObject> LaneObjects;
            public BufferLookup<Game.Net.SubLane> SubLanes;
            public BufferLookup<Game.Net.SubNet> SubNets;
            public BufferLookup<Game.Objects.SubObject> SubObjects;

            public static ParkingLookups Create(SystemBase em)
            {
                return new ParkingLookups
                {
                    ParkingLanes = em.GetComponentLookup<Game.Net.ParkingLane>(true),
                    Prefabs = em.GetComponentLookup<PrefabRef>(true),
                    Curves = em.GetComponentLookup<Curve>(true),
                    LaneData = em.GetComponentLookup<ParkingLaneData>(true),
                    ParkedCars = em.GetComponentLookup<ParkedCar>(true),
                    Garages = em.GetComponentLookup<GarageLane>(true),
                    LaneObjects = em.GetBufferLookup<LaneObject>(true),
                    SubLanes = em.GetBufferLookup<Game.Net.SubLane>(true),
                    SubNets = em.GetBufferLookup<Game.Net.SubNet>(true),
                    SubObjects = em.GetBufferLookup<Game.Objects.SubObject>(true),
                };
            }

            /// <summary>
            /// Street parking of one road. Roadside parking has no marked spaces in this game version (ParkingLaneData
            /// slot interval 0; the Roads info view and VehicleUtils.GetParkingData then give up on a capacity), so we
            /// report what the game itself uses: parked cars, and the longest free stretch of kerb on the road
            /// (ParkingLane.m_FreeSpace, metres, ParkingLaneDataSystem.CalculateFreeSpace - drivers looking for a
            /// space test against it). freeGap = -1 when the road has no car parking.
            /// </summary>
            public void Street(Entity road, out int parked, out float freeGap, out int fee)
            {
                parked = 0; freeGap = -1f; fee = 0;
                int lanes = 0, feeSum = 0;
                if (!SubLanes.TryGetBuffer(road, out var sub))
                    return;
                for (int i = 0; i < sub.Length; i++)
                {
                    var lane = sub[i].m_SubLane;
                    if (!ParkingLanes.TryGetComponent(lane, out var pl) || (pl.m_Flags & (ParkingLaneFlags.VirtualLane | ParkingLaneFlags.ParkingDisabled)) != 0)
                        continue;
                    if (!Prefabs.TryGetComponent(lane, out var pr) || !LaneData.TryGetComponent(pr.m_Prefab, out var data) || (data.m_RoadTypes & Game.Net.RoadTypes.Car) == 0)
                        continue;
                    if (LaneObjects.TryGetBuffer(lane, out var objects))
                        for (int j = 0; j < objects.Length; j++)
                            if (ParkedCars.HasComponent(objects[j].m_LaneObject))
                                parked++;
                    freeGap = Math.Max(freeGap, pl.m_FreeSpace);
                    feeSum += pl.m_ParkingFee;
                    lanes++;
                }
                if (lanes > 0)
                    fee = feeSum / lanes;
            }

            /// <summary>Parking spaces, parked cars and average fee of one road or building (the parking panel's numbers, VehicleUtils.GetParkingData).</summary>
            public void Get(Entity e, out int capacity, out int parked, out int fee)
            {
                int lanes = 0;
                capacity = 0; parked = 0; fee = 0;
                VehicleUtils.GetParkingData(e, ref lanes, ref capacity, ref parked, ref fee, ref ParkingLanes, ref Prefabs, ref Curves, ref LaneData, ref ParkedCars, ref Garages, ref LaneObjects, ref SubLanes, ref SubNets, ref SubObjects);
                if (lanes > 0)
                    fee /= lanes;
                if (capacity < 0)
                    capacity = 0;
            }
        }

        /// <summary>A road counts as full when no free kerb stretch is this long (metres, about one car; our threshold, not the game's).</summary>
        private const float kCarGap = 5f;

        public static JsonObject Parking(GameData g, World world)
        {
            var em = g.EntityManager;
            var look = ParkingLookups.Create(world.GetExistingSystemManaged<CityAssistant.Systems.CityExportSystem>());
            var names = world.GetExistingSystemManaged<NameSystem>();
            long streetParked = 0, feeWeighted = 0;
            int roadsWithParking = 0, roadsFull = 0, roadsPaid = 0;
            using (var roads = g.Roads.ToEntityArray(Allocator.Temp))
            {
                foreach (var r in roads)
                {
                    look.Street(r, out var parked, out var gap, out var fee);
                    if (gap < 0)
                        continue;
                    roadsWithParking++;
                    streetParked += parked;
                    if (gap < kCarGap)
                        roadsFull++;
                    if (fee > 0)
                    {
                        roadsPaid++;
                        feeWeighted += fee;
                    }
                }
            }
            var lots = new List<object>();
            long lotSpaces = 0, lotParked = 0;
            var q = Query(em, ComponentType.ReadOnly<Game.Buildings.ParkingFacility>(), ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>());
            using (var entities = q.ToEntityArray(Allocator.Temp))
            {
                foreach (var e in entities)
                {
                    look.Get(e, out var cap, out var parked, out var fee);
                    lotSpaces += cap;
                    lotParked += parked;
                    var p = em.GetComponentData<Transform>(e).m_Position;
                    lots.Add(new JsonObject
                    {
                        { "building", Label(g, names, e) },
                        { "spaces", cap },
                        { "parked", parked },
                        { "percentUsed", cap > 0 ? Math.Round(100.0 * parked / cap, 1) : 0.0 },
                        { "fee", fee },
                        { "x", Math.Round(p.x, 1) },
                        { "z", Math.Round(p.z, 1) },
                    });
                }
            }
            lots.Sort((a, b) => Convert.ToDouble(((JsonObject)b).Get("percentUsed")).CompareTo(Convert.ToDouble(((JsonObject)a).Get("percentUsed"))));
            return new JsonObject
            {
                { "streetParked", streetParked },
                { "roadsWithStreetParking", roadsWithParking },
                { "roadsWithStreetParkingFull", roadsFull },
                { "percentOfRoadsFull", roadsWithParking > 0 ? Math.Round(100.0 * roadsFull / roadsWithParking, 1) : 0.0 },
                { "roadsWithPaidParking", roadsPaid },
                { "streetAverageFeeWherePaid", roadsPaid > 0 ? Math.Round((double)feeWeighted / roadsPaid, 1) : 0.0 },
                { "lotSpaces", lotSpaces },
                { "lotParked", lotParked },
                { "parkingBuildings", lots },
                { "note", "Lots and garages as their panels and the Roads info view count them (VehicleUtils.GetParkingData; the game lets them fill past their spaces). Street parking has no marked spaces in this game version, so it is given as parked cars and roads that are full: no free kerb stretch of 5 m or more (our threshold) on either side. fee = the parking fee per space (set by the district/building parking policy); the money is the budget's FeeParking income line. Full parking makes drivers circle and search; per street see the map layer 'parking'." },
            };
        }

        // ---------- who uses a road (/check/road-users) ----------

        public static JsonObject RoadUsers(GameData g, World world, float x, float z)
        {
            var em = g.EntityManager;
            var names = world.GetExistingSystemManaged<NameSystem>();
            // The road segment nearest to the point.
            Entity road = Entity.Null;
            float best = float.MaxValue;
            float3 roadMid = default;
            using (var roads = g.Roads.ToEntityArray(Allocator.Temp))
            using (var curves = g.Roads.ToComponentDataArray<Curve>(Allocator.Temp))
            {
                for (int i = 0; i < roads.Length; i++)
                {
                    for (int s = 0; s <= 8; s++)
                    {
                        var p = MathUtils.Position(curves[i].m_Bezier, s / 8f);
                        float d = (p.x - x) * (p.x - x) + (p.z - z) * (p.z - z);
                        if (d < best)
                        {
                            best = d;
                            road = roads[i];
                            roadMid = MathUtils.Position(curves[i].m_Bezier, 0.5f);
                        }
                    }
                }
            }
            if (road == Entity.Null)
                return new JsonObject { { "error", "no road found" } };

            var lanes = new HashSet<Entity>();
            if (em.HasBuffer<Game.Net.SubLane>(road))
            {
                var sub = em.GetBuffer<Game.Net.SubLane>(road, true);
                for (int i = 0; i < sub.Length; i++)
                    lanes.Add(sub[i].m_SubLane);
            }
            string street = null;
            if (names != null && em.HasComponent<Aggregated>(road))
            {
                var agg = em.GetComponentData<Aggregated>(road).m_Aggregate;
                if (agg != Entity.Null)
                    street = names.GetRenderedLabelName(agg);
            }

            var byKind = new SortedDictionary<string, int>();
            var byDestination = new SortedDictionary<string, int>();
            var byOrigin = new SortedDictionary<string, int>();
            var places = new Dictionary<Entity, int>();
            int total = 0, onItNow = 0, through = 0;

            void Count(Entity agent, string kind, bool now, Entity target, Entity vehicle)
            {
                total++;
                if (now) onItNow++;
                byKind.TryGetValue(kind, out var n); byKind[kind] = n + 1;
                var place = Top(em, target);
                var dest = PlaceKind(em, place);
                byDestination.TryGetValue(dest, out n); byDestination[dest] = n + 1;
                if (place != Entity.Null && dest != "outside the city")
                {
                    places.TryGetValue(place, out n); places[place] = n + 1;
                }
                var origin = vehicle != Entity.Null ? Origin(em, vehicle) : null;
                if (origin != null)
                {
                    byOrigin.TryGetValue(origin, out n); byOrigin[origin] = n + 1;
                    if (dest == "outside the city") through++;
                }
            }

            bool PathUses(Entity e, out bool now)
            {
                now = false;
                if (em.HasComponent<CarCurrentLane>(e) && lanes.Contains(em.GetComponentData<CarCurrentLane>(e).m_Lane)) { now = true; return true; }
                if (em.HasComponent<HumanCurrentLane>(e) && lanes.Contains(em.GetComponentData<HumanCurrentLane>(e).m_Lane)) { now = true; return true; }
                if (em.HasBuffer<CarNavigationLane>(e))
                {
                    var nav = em.GetBuffer<CarNavigationLane>(e, true);
                    for (int i = 0; i < nav.Length; i++)
                        if (lanes.Contains(nav[i].m_Lane)) return true;
                }
                if (em.HasBuffer<PathElement>(e) && em.HasComponent<PathOwner>(e))
                {
                    var path = em.GetBuffer<PathElement>(e, true);
                    for (int i = Math.Max(0, em.GetComponentData<PathOwner>(e).m_ElementIndex); i < path.Length; i++)
                        if (lanes.Contains(path[i].m_Target)) return true;
                }
                return false;
            }

            // Vehicles (cars, bikes, trucks, buses, service vehicles).
            var vq = Query(em, ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<Target>());
            using (var vehicles = vq.ToEntityArray(Allocator.Temp))
            {
                foreach (var v in vehicles)
                {
                    if (em.HasComponent<ParkedCar>(v) || !PathUses(v, out var now))
                        continue;
                    Count(v, VehicleKind(em, v), now, em.GetComponentData<Target>(v).m_Target, v);
                }
            }
            // People on foot.
            var hq = Query(em, ComponentType.ReadOnly<Human>(), ComponentType.ReadOnly<Target>());
            using (var humans = hq.ToEntityArray(Allocator.Temp))
            {
                foreach (var h in humans)
                {
                    if (em.HasComponent<CurrentVehicle>(h) || !PathUses(h, out var now))
                        continue;
                    Count(h, "on foot", now, em.GetComponentData<Target>(h).m_Target, Entity.Null);
                }
            }

            var top = new List<KeyValuePair<Entity, int>>(places);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var topPlaces = new List<object>();
            for (int i = 0; i < top.Count && i < 10; i++)
            {
                var e = top[i].Key;
                var row = new JsonObject
                {
                    { "place", Label(g, names, e) },
                    { "kind", PlaceKind(em, e) },
                    { "users", top[i].Value },
                };
                var pos = Position(em, e);
                if (pos.HasValue)
                {
                    row.Add("x", Math.Round(pos.Value.x, 1));
                    row.Add("z", Math.Round(pos.Value.z, 1));
                }
                topPlaces.Add(row);
            }
            JsonObject Dict(SortedDictionary<string, int> d)
            {
                var o = new JsonObject();
                var sorted = new List<KeyValuePair<string, int>>(d);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (var kv in sorted) o.Add(kv.Key, kv.Value);
                return o;
            }
            return new JsonObject
            {
                { "schema", "cityassistant.road-users/1" },
                { "street", street },
                { "roadX", Math.Round(roadMid.x, 1) },
                { "roadZ", Math.Round(roadMid.z, 1) },
                { "users", total },
                { "onItNow", onItNow },
                { "byKind", Dict(byKind) },
                { "goingTo", Dict(byDestination) },
                { "comingFromOutside", Dict(byOrigin) },
                { "throughTraffic", through },
                { "topDestinations", topPlaces },
                { "note", "Like the game's Traffic Routes view (TrafficRoutesSystem) for one road segment: everyone on it now or with it still ahead on their planned route, what they travel in and where they are going. comingFromOutside = vehicles of a border connection (imports, outside services), tourists' and commuters' cars; throughTraffic = those also heading out of the city. A snapshot of this moment." },
            };
        }
    }
}
