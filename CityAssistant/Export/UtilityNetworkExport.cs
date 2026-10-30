using System.Collections.Generic;
using Game.Buildings;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;
using WaterPumpingStation = Game.Buildings.WaterPumpingStation;
using SewageOutlet = Game.Buildings.SewageOutlet;

namespace CityAssistant.Export
{
    /// <summary>
    /// map-utilities (Phase 4.3): the power and water networks as the game's Electricity and Water views
    /// draw them. Every power line, cable and pipe on the map has a node in the game's flow graph, and so
    /// do its two ends; the flow edges between them carry the line's capacity, flow and (for power) the
    /// bottleneck flags. This reads them the way NetColorSystem.FillEdgeFlow does (game version 1.6.1f1).
    /// Plus the producers (power plants, pumping stations, sewage outlets) and every building that wants
    /// more power or water than it gets.
    /// </summary>
    public static class UtilityNetworkExport
    {
        // Problem buildings listed one by one; beyond this only the totals are counted.
        private const int kMaxListed = 5000;

        public static JsonObject Build(GameData g, string trigger)
        {
            var o = g.Header("map-utilities", trigger);
            o.Add("electricityNetwork", GameData.Safe("electricityNetwork", () => ElectricityNetwork(g)));
            o.Add("waterNetwork", GameData.Safe("waterNetwork", () => WaterNetwork(g)));
            o.Add("electricityBuildings", GameData.Safe("electricityBuildings", () => ElectricityBuildings(g)));
            o.Add("waterBuildings", GameData.Safe("waterBuildings", () => WaterBuildings(g)));
            o.Add("units", "Flows and capacities are game units (electricity probably 0.1 kW like the panels, unverified); compare flow with capacity on the same line.");
            return o;
        }

        // ---------- networks ----------

        private static JsonObject ElectricityNetwork(GameData g)
        {
            var em = g.EntityManager;
            using (var entities = g.PowerLines.ToEntityArray(Allocator.Temp))
            {
                var points = new List<float>();
                var values = new List<float>();
                var types = new TypeTable(g);
                var typeOfLine = new List<int>();
                int bottlenecks = 0, beyond = 0, disconnected = 0;
                foreach (var entity in entities)
                {
                    if (!TryFlowNodes(em, entity, e => em.HasComponent<ElectricityNodeConnection>(e) ? em.GetComponentData<ElectricityNodeConnection>(e).m_ElectricityNode : Entity.Null,
                            out var start, out var middle, out var end))
                        continue;
                    if (!TryGetFlowEdge(em, start, middle, out ElectricityFlowEdge first) || !TryGetFlowEdge(em, middle, end, out ElectricityFlowEdge second))
                        continue;
                    AddCurve(points, em.GetComponentData<Curve>(entity));
                    var flags = first.m_Flags | second.m_Flags;
                    values.Add(math.min(first.m_Capacity, second.m_Capacity));
                    values.Add(math.max(math.abs(first.m_Flow), math.abs(second.m_Flow)));
                    values.Add((float)flags);
                    typeOfLine.Add(types.Of(em.GetComponentData<PrefabRef>(entity).m_Prefab));
                    if ((flags & ElectricityFlowEdgeFlags.Bottleneck) != 0) bottlenecks++;
                    else if ((flags & ElectricityFlowEdgeFlags.BeyondBottleneck) != 0) beyond++;
                    if ((flags & ElectricityFlowEdgeFlags.Disconnected) != 0) disconnected++;
                }
                return new JsonObject
                {
                    { "count", typeOfLine.Count },
                    { "bottlenecks", bottlenecks },
                    { "beyondBottleneck", beyond },
                    { "disconnected", disconnected },
                    { "curves", MapExport.Base64(points.ToArray()) },
                    { "values", MapExport.Base64(values.ToArray()) },
                    { "layout", "per line: 8 float32 curve (as roads) in 'curves'; 3 float32 in 'values': capacity, flow, flags (1 forward, 2 backward, 4 bottleneck, 8 beyond a bottleneck, 16 disconnected)" },
                    { "types", types.Names },
                    { "typeOfLine", typeOfLine },
                };
            }
        }

        private static JsonObject WaterNetwork(GameData g)
        {
            var em = g.EntityManager;
            using (var entities = g.Pipes.ToEntityArray(Allocator.Temp))
            {
                var points = new List<float>();
                var values = new List<float>();
                var types = new TypeTable(g);
                var typeOfPipe = new List<int>();
                foreach (var entity in entities)
                {
                    if (!TryFlowNodes(em, entity, e => em.HasComponent<WaterPipeNodeConnection>(e) ? em.GetComponentData<WaterPipeNodeConnection>(e).m_WaterPipeNode : Entity.Null,
                            out var start, out var middle, out var end))
                        continue;
                    if (!TryGetFlowEdge(em, start, middle, out WaterPipeEdge first) || !TryGetFlowEdge(em, middle, end, out WaterPipeEdge second))
                        continue;
                    AddCurve(points, em.GetComponentData<Curve>(entity));
                    values.Add(math.min(first.m_FreshCapacity, second.m_FreshCapacity));
                    values.Add(math.max(math.abs(first.m_FreshFlow), math.abs(second.m_FreshFlow)));
                    values.Add(math.min(first.m_SewageCapacity, second.m_SewageCapacity));
                    values.Add(math.max(math.abs(first.m_SewageFlow), math.abs(second.m_SewageFlow)));
                    values.Add(math.max(first.m_FreshPollution, second.m_FreshPollution));
                    typeOfPipe.Add(types.Of(em.GetComponentData<PrefabRef>(entity).m_Prefab));
                }
                return new JsonObject
                {
                    { "count", typeOfPipe.Count },
                    { "curves", MapExport.Base64(points.ToArray()) },
                    { "values", MapExport.Base64(values.ToArray()) },
                    { "layout", "per pipe: 8 float32 curve in 'curves'; 5 float32 in 'values': fresh capacity, fresh flow, sewage capacity, sewage flow, fresh water pollution" },
                    { "types", types.Names },
                    { "typeOfPipe", typeOfPipe },
                };
            }
        }

        /// <summary>The flow-graph nodes of a line's start, the line itself and its end (all must exist).</summary>
        private static bool TryFlowNodes(EntityManager em, Entity edge, System.Func<Entity, Entity> flowNode, out Entity start, out Entity middle, out Entity end)
        {
            start = middle = end = Entity.Null;
            if (!em.HasComponent<Edge>(edge))
                return false;
            var ends = em.GetComponentData<Edge>(edge);
            middle = flowNode(edge);
            start = flowNode(ends.m_Start);
            end = flowNode(ends.m_End);
            return middle != Entity.Null && start != Entity.Null && end != Entity.Null;
        }

        /// <summary>Same search as ElectricityGraphUtils / WaterPipeGraphUtils.TryGetFlowEdge.</summary>
        private static bool TryGetFlowEdge<T>(EntityManager em, Entity from, Entity to, out T edge) where T : unmanaged, IComponentData
        {
            edge = default(T);
            if (!em.HasBuffer<ConnectedFlowEdge>(from))
                return false;
            var connections = em.GetBuffer<ConnectedFlowEdge>(from, true);
            for (int i = 0; i < connections.Length; i++)
            {
                var candidate = connections[i].m_Edge;
                if (!em.HasComponent<T>(candidate))
                    continue;
                var data = em.GetComponentData<T>(candidate);
                Entity s, e;
                if (data is ElectricityFlowEdge power) { s = power.m_Start; e = power.m_End; }
                else if (data is WaterPipeEdge water) { s = water.m_Start; e = water.m_End; }
                else return false;
                if (s == from && e == to)
                {
                    edge = data;
                    return true;
                }
            }
            return false;
        }

        // ---------- buildings ----------

        private static JsonObject ElectricityBuildings(GameData g)
        {
            var em = g.EntityManager;
            var producers = new List<object>();
            var problems = new List<float>();
            int consumers = 0, shortOfPower = 0, notConnected = 0, bottleneckWarning = 0;
            long wanted = 0, fulfilled = 0;
            using (var entities = g.Buildings.ToEntityArray(Allocator.Temp))
            using (var transforms = g.Buildings.ToComponentDataArray<Transform>(Allocator.Temp))
            using (var prefabs = g.Buildings.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var p = transforms[i].m_Position;
                    if (em.HasComponent<ElectricityProducer>(entities[i]))
                    {
                        var producer = em.GetComponentData<ElectricityProducer>(entities[i]);
                        producers.Add(new JsonObject
                        {
                            { "type", g.Prefabs.GetPrefabName(prefabs[i].m_Prefab) }, { "x", math.round(p.x) }, { "z", math.round(p.z) },
                            { "capacity", producer.m_Capacity }, { "lastProduction", producer.m_LastProduction },
                        });
                    }
                    if (!em.HasComponent<ElectricityConsumer>(entities[i]))
                        continue;
                    var c = em.GetComponentData<ElectricityConsumer>(entities[i]);
                    if (c.m_WantedConsumption <= 0)
                        continue;
                    consumers++;
                    wanted += c.m_WantedConsumption;
                    fulfilled += c.m_FulfilledConsumption;
                    var missing = c.m_FulfilledConsumption < c.m_WantedConsumption;
                    var unconnected = !c.electricityConnected;
                    var warned = (c.m_Flags & ElectricityConsumerFlags.BottleneckWarning) != 0;
                    if (missing) shortOfPower++;
                    if (unconnected) notConnected++;
                    if (warned) bottleneckWarning++;
                    if ((missing || unconnected || warned) && problems.Count < kMaxListed * 5)
                    {
                        problems.Add(p.x); problems.Add(p.z); problems.Add(c.m_WantedConsumption); problems.Add(c.m_FulfilledConsumption); problems.Add((float)c.m_Flags);
                    }
                }
            }
            return new JsonObject
            {
                { "consumers", consumers }, { "wanted", wanted }, { "fulfilled", fulfilled },
                { "shortOfPower", shortOfPower }, { "notConnected", notConnected }, { "bottleneckWarning", bottleneckWarning },
                { "problemBuildings", MapExport.Base64(problems.ToArray()) },
                { "problemLayout", "per building 5 float32: x, z, wanted, fulfilled, flags (1 connected, 2 no-electricity warning, 4 bottleneck warning); listed if short, not connected or warned (max 5000)" },
                { "producers", producers },
            };
        }

        private static JsonObject WaterBuildings(GameData g)
        {
            var em = g.EntityManager;
            var pumps = new List<object>();
            var outlets = new List<object>();
            var problems = new List<float>();
            int consumers = 0, shortOfWater = 0, shortOfSewage = 0, noWater = 0, noSewage = 0;
            using (var entities = g.Buildings.ToEntityArray(Allocator.Temp))
            using (var transforms = g.Buildings.ToComponentDataArray<Transform>(Allocator.Temp))
            using (var prefabs = g.Buildings.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var p = transforms[i].m_Position;
                    var type = g.Prefabs.GetPrefabName(prefabs[i].m_Prefab);
                    if (em.HasComponent<WaterPumpingStation>(entities[i]))
                    {
                        var pump = em.GetComponentData<WaterPumpingStation>(entities[i]);
                        pumps.Add(new JsonObject
                        {
                            { "type", type }, { "x", math.round(p.x) }, { "z", math.round(p.z) },
                            { "capacity", pump.m_Capacity }, { "lastProduction", pump.m_LastProduction }, { "pollution", pump.m_Pollution },
                        });
                    }
                    if (em.HasComponent<SewageOutlet>(entities[i]))
                    {
                        var outlet = em.GetComponentData<SewageOutlet>(entities[i]);
                        outlets.Add(new JsonObject
                        {
                            { "type", type }, { "x", math.round(p.x) }, { "z", math.round(p.z) },
                            { "capacity", outlet.m_Capacity }, { "lastProcessed", outlet.m_LastProcessed }, { "lastPurified", outlet.m_LastPurified },
                        });
                    }
                    if (!em.HasComponent<WaterConsumer>(entities[i]))
                        continue;
                    var c = em.GetComponentData<WaterConsumer>(entities[i]);
                    if (c.m_WantedConsumption <= 0)
                        continue;
                    consumers++;
                    var water = c.m_FulfilledFresh < c.m_WantedConsumption;
                    var sewage = c.m_FulfilledSewage < c.m_WantedConsumption;
                    var waterLink = (c.m_Flags & WaterConsumerFlags.WaterConnected) != 0;
                    var sewageLink = (c.m_Flags & WaterConsumerFlags.SewageConnected) != 0;
                    if (water) shortOfWater++;
                    if (sewage) shortOfSewage++;
                    if (!waterLink) noWater++;
                    if (!sewageLink) noSewage++;
                    if ((water || sewage || !waterLink || !sewageLink) && problems.Count < kMaxListed * 6)
                    {
                        problems.Add(p.x); problems.Add(p.z); problems.Add(c.m_WantedConsumption); problems.Add(c.m_FulfilledFresh); problems.Add(c.m_FulfilledSewage); problems.Add((float)c.m_Flags);
                    }
                }
            }
            return new JsonObject
            {
                { "consumers", consumers }, { "shortOfWater", shortOfWater }, { "shortOfSewage", shortOfSewage },
                { "notConnectedToWater", noWater }, { "notConnectedToSewage", noSewage },
                { "problemBuildings", MapExport.Base64(problems.ToArray()) },
                { "problemLayout", "per building 6 float32: x, z, wanted, fulfilled fresh, fulfilled sewage, flags (1 water connected, 2 sewage connected); max 5000" },
                { "pumps", pumps },
                { "sewageOutlets", outlets },
            };
        }

        // ---------- helpers ----------

        private static void AddCurve(List<float> points, Curve curve)
        {
            var b = curve.m_Bezier;
            points.Add(b.a.x); points.Add(b.a.z); points.Add(b.b.x); points.Add(b.b.z);
            points.Add(b.c.x); points.Add(b.c.z); points.Add(b.d.x); points.Add(b.d.z);
        }

        private class TypeTable
        {
            private readonly GameData m_Game;
            private readonly Dictionary<Entity, int> m_Index = new Dictionary<Entity, int>();
            public readonly List<string> Names = new List<string>();

            public TypeTable(GameData g) { m_Game = g; }

            public int Of(Entity prefab)
            {
                if (!m_Index.TryGetValue(prefab, out var i))
                {
                    i = Names.Count;
                    m_Index[prefab] = i;
                    Names.Add(m_Game.Prefabs.GetPrefabName(prefab));
                }
                return i;
            }
        }
    }
}
