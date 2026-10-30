using System;
using System.Collections.Generic;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;
using AreaNode = Game.Areas.Node;

namespace CityAssistant.Export
{
    /// <summary>
    /// Layer 4, the spatial layer (Phase 4): the numbers behind the game's map overlays, for Claude to draw
    /// its own annotated maps. Three exports:
    ///   map-grids     the game's simulation grids (pollution, wind, land value, population, groundwater, water)
    ///   map-network   roads with their street names, district outlines and landmarks (the base map)
    ///   map-buildings polluting buildings with the pollution the game gives them, and homes
    ///   map-utilities power and water networks with flow, capacity and bottlenecks (UtilityNetworkExport)
    ///   map-zones     zoning cells, owned map tiles and building lots, for layout plans (ZoneExport)
    /// Roads also carry their traffic (the Traffic Flow view) and service coverage (the coverage views).
    ///
    /// Coordinates are the game's world metres: x west to east, z south to north, (0, 0) the map centre,
    /// the whole map from -7168 to +7168 (CellMapSystem.kMapSize = 14336). Grid cell (column c, row r) is
    /// value index c + r * size; its centre is -7168 + (c + 0.5) * cellMetres, likewise for z with r
    /// (source: game code CellMapSystem.GetCellCenter). Big arrays are base64 little-endian float32, which
    /// keeps a 256 x 256 grid at about 350 KB instead of several MB of JSON numbers.
    /// </summary>
    public static class MapExport
    {
        public static readonly string[] Names = { "map-grids", "map-network", "map-buildings", "map-utilities", "map-zones" };

        public static JsonObject Build(string name, GameData g, World world, string trigger)
        {
            switch (name)
            {
                case "map-grids": return Grids(g, world, trigger);
                case "map-network": return Network(g, world, trigger);
                case "map-buildings": return Buildings(g, world, trigger);
                case "map-utilities": return UtilityNetworkExport.Build(g, trigger);
                case "map-zones": return ZoneExport.Build(g, world, trigger);
                default: return null;
            }
        }

        // ---------- map-grids ----------

        private static JsonObject Grids(GameData g, World world, string trigger)
        {
            var o = g.Header("map-grids", trigger);
            o.Add("mapMetres", CellMapSystem<AirPollution>.kMapSize);
            o.Add("encoding", "base64 float32 little-endian, row-major: index = column + row * size; row 0 = south (z = -7168), column 0 = west (x = -7168)");
            var grids = new JsonObject();
            grids.Add("airPollution", GameData.Safe("airPollution", () =>
                ReadGrid(world.GetExistingSystemManaged<AirPollutionSystem>(), AirPollutionSystem.kTextureSize, c => new float[] { c.m_Pollution },
                    "Air pollution per cell (the Air Pollution view; game units, 0 = none)")));
            grids.Add("groundPollution", GameData.Safe("groundPollution", () =>
                ReadGrid(world.GetExistingSystemManaged<GroundPollutionSystem>(), GroundPollutionSystem.kTextureSize, c => new float[] { c.m_Pollution },
                    "Ground pollution per cell (the Ground Pollution view; game units, 0 = none)")));
            grids.Add("noisePollution", GameData.Safe("noisePollution", () =>
                ReadGrid(world.GetExistingSystemManaged<NoisePollutionSystem>(), NoisePollutionSystem.kTextureSize, c => new float[] { c.m_Pollution },
                    "Noise per cell (the Noise Pollution view; game units, 0 = none)")));
            grids.Add("groundWater", GameData.Safe("groundWater", () =>
                ReadGrid(world.GetExistingSystemManaged<GroundWaterSystem>(), GroundWaterSystem.kTextureSize, c => new float[] { c.m_Amount, c.m_Polluted, c.m_Max },
                    "Groundwater per cell: channels amount, polluted, max")));
            grids.Add("landValue", GameData.Safe("landValue", () =>
                ReadGrid(world.GetExistingSystemManaged<LandValueSystem>(), LandValueSystem.kTextureSize, c => new float[] { c.m_LandValue },
                    "Land value per cell (raw game value; the Land Value panel's average is x 240)")));
            grids.Add("population", GameData.Safe("population", () =>
                ReadGrid(world.GetExistingSystemManaged<PopulationToGridSystem>(), PopulationToGridSystem.kTextureSize, c => new float[] { c.m_Population },
                    "Residents per cell")));
            grids.Add("wind", GameData.Safe("wind", () =>
                ReadGrid(world.GetExistingSystemManaged<WindSystem>(), WindSystem.kTextureSize, c => new float[] { c.m_Wind.x, c.m_Wind.y },
                    "Wind per cell: channels x (towards east), z (towards north); the direction the wind blows to")));
            grids.Add("surfaceWater", GameData.Safe("surfaceWater", () => SurfaceWater(world)));
            grids.Add("terrainHeight", GameData.Safe("terrainHeight", () => TerrainHeight(world)));
            o.Add("grids", grids);
            return o;
        }

        private static JsonObject ReadGrid<T>(CellMapSystem<T> system, int size, Func<T, float[]> channels, string meaning)
            where T : struct, Colossal.Serialization.Entities.ISerializable
        {
            if (system == null)
                throw new InvalidOperationException("grid system not found");
            var map = system.GetMap(true, out var dependencies);
            dependencies.Complete();
            int channelCount = channels(map[0]).Length;
            var values = new float[map.Length * channelCount];
            for (int i = 0; i < map.Length; i++)
            {
                var cell = channels(map[i]);
                for (int c = 0; c < channelCount; c++)
                    values[i * channelCount + c] = cell[c];
            }
            return GridJson(size, channelCount, values, meaning);
        }

        /// <summary>Sea, rivers and lakes, sampled from the game's 2048 x 2048 water surface at 256 x 256 cell centres.</summary>
        private static JsonObject SurfaceWater(World world)
        {
            var water = world.GetExistingSystemManaged<WaterSystem>();
            if (water == null)
                throw new InvalidOperationException("WaterSystem not found");
            var depths = water.GetDepths(out var dependencies);
            dependencies.Complete();
            if (!depths.IsCreated || depths.Length == 0)
                throw new InvalidOperationException("water surface not available yet");
            var side = (int)Math.Round(Math.Sqrt(depths.Length));
            const int size = 256;
            var values = new float[size * size * 2];
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++)
                {
                    var source = (int)((r + 0.5) * side / size) * side + (int)((c + 0.5) * side / size);
                    var cell = depths[source];
                    values[(r * size + c) * 2] = cell.m_Depth;
                    values[(r * size + c) * 2 + 1] = cell.m_Polluted;
                }
            }
            return GridJson(size, 2, values, "Surface water per cell: channels depth (metres), polluted (the Water Pollution view)");
        }

        /// <summary>Ground height (metres) at 256 x 256 cell centres, for slopes; sampled like the camera does (TerrainUtils.SampleHeight).</summary>
        private static JsonObject TerrainHeight(World world)
        {
            var terrain = world.GetExistingSystemManaged<TerrainSystem>();
            if (terrain == null)
                throw new InvalidOperationException("TerrainSystem not found");
            var data = terrain.GetHeightData();
            if (!data.isCreated || data.resolution.x <= 2)
                throw new InvalidOperationException("terrain heights not available yet");
            const int size = 256;
            var cell = (float)CellMapSystem<AirPollution>.kMapSize / size;
            var values = new float[size * size];
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    values[r * size + c] = TerrainUtils.SampleHeight(ref data, new float3(-7168f + (c + 0.5f) * cell, 0f, -7168f + (r + 0.5f) * cell));
            return GridJson(size, 1, values, "Ground height per cell (metres above the game's zero; water surface = height + surfaceWater depth)");
        }

        private static JsonObject GridJson(int size, int channels, float[] values, string meaning)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var v in values)
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
            return new JsonObject
            {
                { "meaning", meaning },
                { "size", size },
                { "cellMetres", (double)CellMapSystem<AirPollution>.kMapSize / size },
                { "channels", channels },
                { "min", min },
                { "max", max },
                { "values", Base64(values) },
            };
        }

        // ---------- map-network ----------

        private static JsonObject Network(GameData g, World world, string trigger)
        {
            var o = g.Header("map-network", trigger);
            var names = world.GetExistingSystemManaged<NameSystem>();
            o.Add("roads", GameData.Safe("roads", () => Roads(g, world, names)));
            o.Add("districts", GameData.Safe("districts", () => DistrictOutlines(g, world)));
            o.Add("landmarks", GameData.Safe("landmarks", () => Landmarks(g, names)));
            o.Add("colourRanges", GameData.Safe("colourRanges", () => ColourRanges(g)));
            return o;
        }

        /// <summary>
        /// Every road segment as its curve (4 Bezier points), with the road type and the street it belongs to.
        /// A street is the game's "aggregate" of segments; its name is the label the game draws on the map
        /// (NameSystem.GetRenderedLabelName, as AggregateMeshSystem uses for street labels).
        /// </summary>
        private static JsonObject Roads(GameData g, World world, NameSystem names)
        {
            var em = g.EntityManager;
            using (var entities = g.Roads.ToEntityArray(Allocator.Temp))
            using (var curves = g.Roads.ToComponentDataArray<Curve>(Allocator.Temp))
            using (var prefabs = g.Roads.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                var types = new List<string>();
                var typeIndex = new Dictionary<Entity, int>();
                var typeOfRoad = new int[curves.Length];
                var points = new float[curves.Length * 8];
                var streets = new List<string>();
                var streetIndex = new Dictionary<Entity, int>();
                var streetOfRoad = new int[curves.Length];
                var traffic = new float[curves.Length * 7];
                var coverage = new float[curves.Length * kCoverageServices * 2];
                var density = new float[curves.Length];
                for (int i = 0; i < curves.Length; i++)
                {
                    var b = curves[i].m_Bezier;
                    points[i * 8 + 0] = b.a.x; points[i * 8 + 1] = b.a.z;
                    points[i * 8 + 2] = b.b.x; points[i * 8 + 3] = b.b.z;
                    points[i * 8 + 4] = b.c.x; points[i * 8 + 5] = b.c.z;
                    points[i * 8 + 6] = b.d.x; points[i * 8 + 7] = b.d.z;
                    var prefab = prefabs[i].m_Prefab;
                    if (!typeIndex.TryGetValue(prefab, out var t))
                    {
                        t = types.Count;
                        typeIndex[prefab] = t;
                        types.Add(g.Prefabs.GetPrefabName(prefab));
                    }
                    typeOfRoad[i] = t;

                    AddTraffic(em, entities[i], traffic, i);
                    AddCoverage(em, entities[i], coverage, i);
                    density[i] = em.HasComponent<Density>(entities[i]) ? em.GetComponentData<Density>(entities[i]).m_Density : -1f;

                    streetOfRoad[i] = -1;
                    if (names != null && em.HasComponent<Aggregated>(entities[i]))
                    {
                        var street = em.GetComponentData<Aggregated>(entities[i]).m_Aggregate;
                        if (street != Entity.Null)
                        {
                            if (!streetIndex.TryGetValue(street, out var s))
                            {
                                s = streets.Count;
                                streetIndex[street] = s;
                                streets.Add(names.GetRenderedLabelName(street));
                            }
                            streetOfRoad[i] = s;
                        }
                    }
                }
                var suitability = GameData.Safe("suitability", () => Suitability(g, world, entities));
                return new JsonObject
                {
                    { "count", curves.Length },
                    { "curves", Base64(points) },
                    { "curvesLayout", "per road 8 float32: a.x a.z b.x b.z c.x c.z d.x d.z (cubic Bezier, world metres)" },
                    { "types", types },
                    { "typeOfRoad", typeOfRoad },
                    { "streets", streets },
                    { "streetOfRoad", streetOfRoad },
                    { "streetsLayout", "streetOfRoad[i] = index into streets (the name the game shows on the map), -1 = unnamed" },
                    { "traffic", Base64(traffic) },
                    { "trafficLayout", "per road 7 float32: flow forward, flow backward (the Traffic Flow view's value, 0 = jammed .. 1 = free; game code NetColorSystem), speed share at the 4 times of day 00/06/12/18 h (0..1, both directions), volume (driven distance, both directions)" },
                    { "coverage", Base64(coverage) },
                    { "coverageLayout", "per road 8 services x 2 float32 (start, end of the road): healthcare, fire & rescue, police, park, post, education, emergency shelter, welfare (game code Game.Net.CoverageService); -1 = no coverage data" },
                    { "density", Base64(density) },
                    { "densityLayout", "per road 1 float32: residents + jobs of the buildings on it per metre of road (Game.Net.Density, NetEdgeDensitySystem); -1 = none" },
                    { "suitability", suitability },
                };
            }
        }

        private const int kCoverageServices = 8;

        /// <summary>The game's zone suitability per road for every zone type that has cells in the city (RoadSuitability).</summary>
        private static JsonObject Suitability(GameData g, World world, NativeArray<Entity> roads)
        {
            var em = g.EntityManager;
            var zoneSystem = world.GetExistingSystemManaged<Game.Prefabs.ZoneSystem>();
            var used = new HashSet<ushort>();
            using (var blocks = g.ZoneBlocks.ToEntityArray(Allocator.Temp))
            {
                foreach (var block in blocks)
                {
                    var cells = em.GetBuffer<Game.Zones.Cell>(block, true);
                    for (int c = 0; c < cells.Length; c++)
                        if (cells[c].m_Zone.m_Index != 0)
                            used.Add(cells[c].m_Zone.m_Index);
                }
            }
            var prefabs = new List<Entity>();
            var zoneNames = new List<string>();
            foreach (var index in used)
            {
                var prefab = zoneSystem.GetPrefab(new Game.Zones.ZoneType { m_Index = index });
                if (prefab == Entity.Null || !em.HasComponent<ZoneData>(prefab))
                    continue;
                prefabs.Add(prefab);
                zoneNames.Add(g.Prefabs.GetPrefabName(prefab));
            }
            var values = world.GetOrCreateSystemManaged<RoadSuitability>().Compute(roads, prefabs);
            return new JsonObject
            {
                { "zones", zoneNames },
                { "values", Base64(values) },
                { "layout", "per road, per zone in 'zones', 2 float32 (start, end): the game's suitability colour 0 = bad .. 1 = good (InfoviewUtils.GetColor, as NetColorSystem draws it while that zone is picked in the zoning tool); -1 = no data" },
            };
        }

        /// <summary>
        /// The value ranges the game's views colour from red (min) to green (max): per coverage service and
        /// for traffic flow. They live in the game's data files (infomode prefabs), not in its code; colour =
        /// clamp((value - min) / (max - min), 0, 1), source: game code InfoviewUtils.GetColor.
        /// </summary>
        private static JsonObject ColourRanges(GameData g)
        {
            var em = g.EntityManager;
            var coverage = new JsonObject();
            using (var query = em.CreateEntityQuery(ComponentType.ReadOnly<InfoviewCoverageData>()))
            using (var data = query.ToComponentDataArray<InfoviewCoverageData>(Allocator.Temp))
            {
                foreach (var d in data)
                {
                    var key = d.m_Service.ToString();
                    if (coverage.Get(key) == null)
                        coverage.Add(key, new[] { d.m_Range.min, d.m_Range.max });
                }
            }
            var traffic = new JsonObject();
            using (var query = em.CreateEntityQuery(ComponentType.ReadOnly<InfoviewNetStatusData>()))
            using (var data = query.ToComponentDataArray<InfoviewNetStatusData>(Allocator.Temp))
            {
                foreach (var d in data)
                {
                    var key = d.m_Type.ToString();
                    if (traffic.Get(key) == null)
                        traffic.Add(key, new[] { d.m_Range.min, d.m_Range.max });
                }
            }
            return new JsonObject { { "coverage", coverage }, { "netStatus", traffic } };
        }

        /// <summary>Traffic of one road as the Traffic Flow view computes it (NetColorSystem, NetUtils.GetTrafficFlowSpeed).</summary>
        private static void AddTraffic(EntityManager em, Entity road, float[] traffic, int i)
        {
            var r = em.GetComponentData<Road>(road);
            var forward = NetUtils.GetTrafficFlowSpeed(r.m_TrafficFlowDuration0, r.m_TrafficFlowDistance0);
            var backward = NetUtils.GetTrafficFlowSpeed(r.m_TrafficFlowDuration1, r.m_TrafficFlowDistance1);
            var both = NetUtils.GetTrafficFlowSpeed(r);
            traffic[i * 7 + 0] = math.csum(forward) * 0.125f + math.cmin(forward) * 0.5f;
            traffic[i * 7 + 1] = math.csum(backward) * 0.125f + math.cmin(backward) * 0.5f;
            for (int t = 0; t < 4; t++)
                traffic[i * 7 + 2 + t] = both[t];
            traffic[i * 7 + 6] = math.csum(r.m_TrafficFlowDistance0 + r.m_TrafficFlowDistance1);
        }

        /// <summary>Service coverage of one road, per Game.Net.CoverageService (what the coverage views colour).</summary>
        private static void AddCoverage(EntityManager em, Entity road, float[] coverage, int i)
        {
            var row = i * kCoverageServices * 2;
            if (!em.HasBuffer<Game.Net.ServiceCoverage>(road))
            {
                for (int k = 0; k < kCoverageServices * 2; k++)
                    coverage[row + k] = -1f;
                return;
            }
            var buffer = em.GetBuffer<Game.Net.ServiceCoverage>(road, true);
            for (int s = 0; s < kCoverageServices; s++)
            {
                var c = s < buffer.Length ? buffer[s].m_Coverage : new float2(-1f, -1f);
                coverage[row + s * 2] = c.x;
                coverage[row + s * 2 + 1] = c.y;
            }
        }

        private static List<object> DistrictOutlines(GameData g, World world)
        {
            var names = world.GetExistingSystemManaged<NameSystem>();
            var result = new List<object>();
            using (var districts = g.Districts.ToEntityArray(Allocator.Temp))
            {
                foreach (var district in districts)
                {
                    var outline = new List<object>();
                    if (g.EntityManager.HasBuffer<AreaNode>(district))
                    {
                        var nodes = g.EntityManager.GetBuffer<AreaNode>(district, true);
                        for (int i = 0; i < nodes.Length; i++)
                            outline.Add(new[] { Math.Round(nodes[i].m_Position.x, 1), Math.Round(nodes[i].m_Position.z, 1) });
                    }
                    result.Add(new JsonObject { { "name", names.GetRenderedLabelName(district) }, { "outline", outline } });
                }
            }
            return result;
        }

        /// <summary>
        /// Buildings the player can find by name: city service buildings (schools, hospitals, the Central
        /// Bank, ...) and signature buildings, with the name the game shows for them.
        /// </summary>
        private static JsonObject Landmarks(GameData g, NameSystem names)
        {
            var em = g.EntityManager;
            var result = new List<object>();
            using (var entities = g.Buildings.ToEntityArray(Allocator.Temp))
            using (var prefabs = g.Buildings.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            using (var transforms = g.Buildings.ToComponentDataArray<Transform>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var prefab = prefabs[i].m_Prefab;
                    var service = em.HasComponent<ServiceObjectData>(prefab);
                    if (!service && !em.HasComponent<SignatureBuildingData>(prefab))
                        continue;
                    result.Add(new JsonObject
                    {
                        { "name", names != null ? names.GetRenderedLabelName(entities[i]) : g.Prefabs.GetPrefabName(prefab) },
                        { "type", g.Prefabs.GetPrefabName(prefab) },
                        { "kind", service ? "service" : "signature" },
                        { "x", Math.Round(transforms[i].m_Position.x, 1) },
                        { "z", Math.Round(transforms[i].m_Position.z, 1) },
                    });
                }
            }
            return new JsonObject { { "count", result.Count }, { "list", result } };
        }

        // ---------- map-buildings ----------

        private static JsonObject Buildings(GameData g, World world, string trigger)
        {
            var o = g.Header("map-buildings", trigger);
            o.Add("pollutionSources", GameData.Safe("pollutionSources", () => world.GetOrCreateSystemManaged<PollutionSources>().Compute(g)));
            o.Add("homes", GameData.Safe("homes", () => Homes(g)));
            return o;
        }

        /// <summary>Every residential building: position and number of renters (households), for "which homes are hit".</summary>
        private static JsonObject Homes(GameData g)
        {
            var em = g.EntityManager;
            using (var entities = g.Homes.ToEntityArray(Allocator.Temp))
            using (var transforms = g.Homes.ToComponentDataArray<Transform>(Allocator.Temp))
            {
                var values = new float[entities.Length * 3];
                for (int i = 0; i < entities.Length; i++)
                {
                    values[i * 3] = transforms[i].m_Position.x;
                    values[i * 3 + 1] = transforms[i].m_Position.z;
                    values[i * 3 + 2] = em.HasBuffer<Renter>(entities[i]) ? em.GetBuffer<Renter>(entities[i], true).Length : 0;
                }
                return new JsonObject
                {
                    { "count", entities.Length },
                    { "values", Base64(values) },
                    { "layout", "per home 3 float32: x, z, renters (households living there)" },
                };
            }
        }

        public static string Base64(float[] values)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian)
                for (int i = 0; i < bytes.Length; i += 4)
                    Array.Reverse(bytes, i, 4);
            return Convert.ToBase64String(bytes);
        }
    }
}
