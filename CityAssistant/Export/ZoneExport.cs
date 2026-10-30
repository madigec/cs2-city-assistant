using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Transform = Game.Objects.Transform;
using AreaNode = Game.Areas.Node;

namespace CityAssistant.Export
{
    /// <summary>
    /// map-zones (Phase 4.6, layout plans): what is zoned where, which zoned land is still empty, which
    /// map tiles the city owns, and where buildings stand. Everything a layout proposal has to respect.
    ///
    /// Zoning lives in "blocks": the strips of 8 m cells the game puts along both sides of a road
    /// (Game.Zones.Block + Cell buffer). Cell (x, y) of a block is buffer index y * size.x + x, x along the
    /// road, y away from it (y = 0 next to the road); its centre is
    ///   position + direction * ((size.y - 2y - 1) * 4) + right * ((size.x - 2x - 1) * 4)
    /// (source: game code ZoneUtils.GetCellPosition, CellOccupyJobs).
    /// </summary>
    public static class ZoneExport
    {
        public static JsonObject Build(GameData g, Unity.Entities.World world, string trigger)
        {
            var o = g.Header("map-zones", trigger);
            var zoneTypes = new List<object>();
            o.Add("blocks", GameData.Safe("blocks", () => Blocks(g, world, zoneTypes)));
            o.Add("zoneTypes", zoneTypes);
            o.Add("mapTiles", GameData.Safe("mapTiles", () => MapTiles(g)));
            o.Add("buildings", GameData.Safe("buildings", () => Buildings(g)));
            o.Add("pollutionHappiness", GameData.Safe("pollutionHappiness", () => PollutionHappiness(g)));
            o.Add("coverageBuildingTypes", GameData.Safe("coverageBuildingTypes", () => CoverageBuildingTypes(g)));
            return o;
        }

        /// <summary>
        /// Every building type (and upgrade) that gives service coverage, with the game's numbers: a road at
        /// road distance d from it gets magnitude * (1 - t^2), t = clamp(max(cost / 2R, (d - 0.6R) / 0.6R)),
        /// so coverage ends at about 1.2 x range (game code ServiceCoverageSystem, CoverageJobs); capacity is
        /// how many people it serves before coverage thins. locked = not unlocked yet in this city.
        /// </summary>
        private static List<object> CoverageBuildingTypes(GameData g)
        {
            var em = g.EntityManager;
            var result = new List<object>();
            using (var query = em.CreateEntityQuery(ComponentType.ReadOnly<CoverageData>(), ComponentType.ReadOnly<PrefabData>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            using (var data = query.ToComponentDataArray<CoverageData>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var prefab = entities[i];
                    var locked = em.HasComponent<Locked>(prefab) && em.IsComponentEnabled<Locked>(prefab);
                    result.Add(new JsonObject
                    {
                        { "name", g.Prefabs.GetPrefabName(prefab) },
                        { "service", data[i].m_Service.ToString() },
                        { "range", Math.Round(data[i].m_Range, 1) },
                        { "capacity", Math.Round(data[i].m_Capacity, 1) },
                        { "magnitude", Math.Round(data[i].m_Magnitude, 3) },
                        { "lotCells", em.HasComponent<BuildingData>(prefab) ? new[] { em.GetComponentData<BuildingData>(prefab).m_LotSize.x, em.GetComponentData<BuildingData>(prefab).m_LotSize.y } : null },
                        { "upgrade", em.HasComponent<ServiceUpgradeData>(prefab) },
                        { "locked", locked },
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// How pollution at a home costs happiness: penalty = min(max, pollution / divisor) points, separately
        /// for air, ground and noise (game code CitizenHappinessSystem.GetAirPollutionBonuses and friends; the
        /// numbers come from the game's data, code defaults 600 / 50 / 15).
        /// </summary>
        private static JsonObject PollutionHappiness(GameData g)
        {
            using (var query = g.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CitizenHappinessParameterData>()))
            {
                var p = query.GetSingleton<CitizenHappinessParameterData>();
                return new JsonObject
                {
                    { "pollutionDivisor", p.m_PollutionBonusDivisor },
                    { "maxAirOrGroundPenalty", p.m_MaxAirAndGroundPollutionBonus },
                    { "maxNoisePenalty", p.m_MaxNoisePollutionBonus },
                };
            }
        }

        // Cell state bits kept in the export (game code Game.Zones.CellFlags, low byte).
        private const int kStateMask = 0xFF;

        private static JsonObject Blocks(GameData g, Unity.Entities.World world, List<object> zoneTypes)
        {
            var em = g.EntityManager;
            var zoneSystem = world.GetExistingSystemManaged<ZoneSystem>();
            if (zoneSystem == null)
                throw new InvalidOperationException("ZoneSystem not found");
            var zoneIndex = new Dictionary<ushort, int>();   // game zone type index -> our zoneTypes index
            using (var entities = g.ZoneBlocks.ToEntityArray(Allocator.Temp))
            using (var blocks = g.ZoneBlocks.ToComponentDataArray<Block>(Allocator.Temp))
            {
                int cellCount = 0;
                for (int i = 0; i < blocks.Length; i++)
                    cellCount += blocks[i].m_Size.x * blocks[i].m_Size.y;
                var geometry = new float[blocks.Length * 8];
                var states = new byte[cellCount];
                var zones = new byte[cellCount];
                int next = 0;
                for (int i = 0; i < blocks.Length; i++)
                {
                    var b = blocks[i];
                    var right = MathUtils.Right(b.m_Direction);
                    geometry[i * 8 + 0] = b.m_Position.x; geometry[i * 8 + 1] = b.m_Position.z;
                    geometry[i * 8 + 2] = b.m_Direction.x; geometry[i * 8 + 3] = b.m_Direction.y;
                    geometry[i * 8 + 4] = right.x; geometry[i * 8 + 5] = right.y;
                    geometry[i * 8 + 6] = b.m_Size.x; geometry[i * 8 + 7] = b.m_Size.y;
                    var cells = em.GetBuffer<Cell>(entities[i], true);
                    var count = b.m_Size.x * b.m_Size.y;
                    for (int c = 0; c < count; c++)
                    {
                        var cell = c < cells.Length ? cells[c] : default(Cell);
                        states[next] = (byte)((int)cell.m_State & kStateMask);
                        zones[next] = (byte)ZoneTypeIndex(g, zoneSystem, cell.m_Zone, zoneIndex, zoneTypes);
                        next++;
                    }
                }
                return new JsonObject
                {
                    { "count", blocks.Length },
                    { "cellCount", cellCount },
                    { "geometry", MapExport.Base64(geometry) },
                    { "geometryLayout", "per block 8 float32: position x, z (block centre), direction x, z, right x, z, size x (cells along the road), size y (cells deep); cell centre = position + direction * ((size.y - 2y - 1) * 4) + right * ((size.x - 2x - 1) * 4)" },
                    { "cellState", Convert.ToBase64String(states) },
                    { "cellZone", Convert.ToBase64String(zones) },
                    { "cellsLayout", "blocks' cells one after another, block by block, cell index y * size.x + x; cellState = 1 byte of game CellFlags (1 blocked, 2 shared, 4 roadside, 8 visible, 16 overridden, 32 occupied, 64 selected, 128 redundant); cellZone = 1 byte index into zoneTypes (look up its kind; 'Unzoned' is one of the entries)" },
                };
            }
        }

        /// <summary>Our index for a game zone type, adding the zone's name, kind and the game's colour on first use.</summary>
        private static int ZoneTypeIndex(GameData g, ZoneSystem zoneSystem, ZoneType zone, Dictionary<ushort, int> index, List<object> zoneTypes)
        {
            if (index.TryGetValue(zone.m_Index, out var known))
                return known;
            var prefab = zoneSystem.GetPrefab(zone);
            var entry = new JsonObject { { "gameIndex", (int)zone.m_Index } };
            if (zone.m_Index == 0 || prefab == Entity.Null)
            {
                entry.Add("name", "Unzoned");
                entry.Add("kind", "none");
            }
            else
            {
                entry.Add("name", g.Prefabs.GetPrefabName(prefab));
                var zonePrefab = g.Prefabs.GetPrefab<ZonePrefab>(prefab);
                var kind = zonePrefab.m_Office ? "office" : zonePrefab.m_AreaType.ToString().ToLowerInvariant();
                entry.Add("kind", kind);
                var c = zonePrefab.m_Color;
                entry.Add("colour", $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}");
            }
            var ours = zoneTypes.Count;
            if (ours > 255)
                throw new InvalidOperationException("more than 255 zone types in use");
            index[zone.m_Index] = ours;
            zoneTypes.Add(entry);
            return ours;
        }

        /// <summary>The game's map tiles (about 623 m each): outline and whether the city owns it.</summary>
        private static JsonObject MapTiles(GameData g)
        {
            var em = g.EntityManager;
            var result = new List<object>();
            int owned = 0;
            using (var entities = g.MapTiles.ToEntityArray(Allocator.Temp))
            {
                foreach (var tile in entities)
                {
                    // Unowned tiles carry the Native tag (game code MapTilePurchaseSystem.m_OwnedTileQuery).
                    var isOwned = !em.HasComponent<Native>(tile);
                    if (isOwned)
                        owned++;
                    float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                    if (em.HasBuffer<AreaNode>(tile))
                    {
                        var nodes = em.GetBuffer<AreaNode>(tile, true);
                        for (int i = 0; i < nodes.Length; i++)
                        {
                            minX = Math.Min(minX, nodes[i].m_Position.x); maxX = Math.Max(maxX, nodes[i].m_Position.x);
                            minZ = Math.Min(minZ, nodes[i].m_Position.z); maxZ = Math.Max(maxZ, nodes[i].m_Position.z);
                        }
                    }
                    if (minX > maxX)
                        continue;
                    result.Add(new object[] { Math.Round(minX, 1), Math.Round(minZ, 1), Math.Round(maxX, 1), Math.Round(maxZ, 1), isOwned ? 1 : 0 });
                }
            }
            return new JsonObject
            {
                { "count", result.Count },
                { "owned", owned },
                { "allUnlocked", g.Config.unlockMapTiles },
                { "tiles", result },
                { "layout", "per tile [min x, min z, max x, max z, owned 1/0] (world metres); allUnlocked = the 'unlock all map tiles' option" },
            };
        }

        /// <summary>Every building's position, lot radius and what it is, so plans keep clear of them.</summary>
        private static JsonObject Buildings(GameData g)
        {
            var em = g.EntityManager;
            using (var entities = g.Buildings.ToEntityArray(Allocator.Temp))
            using (var prefabs = g.Buildings.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            using (var transforms = g.Buildings.ToComponentDataArray<Transform>(Allocator.Temp))
            {
                var values = new float[entities.Length * 4];
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    var prefab = prefabs[i].m_Prefab;
                    float radius = 8f;
                    if (em.HasComponent<BuildingData>(prefab))
                    {
                        var lot = em.GetComponentData<BuildingData>(prefab).m_LotSize;
                        radius = 4f * (float)Math.Sqrt(lot.x * lot.x + lot.y * lot.y);
                    }
                    int kind = 0;
                    if (em.HasComponent<ResidentialProperty>(e)) kind = 1;
                    else if (em.HasComponent<CommercialProperty>(e)) kind = 2;
                    else if (em.HasComponent<OfficeProperty>(e)) kind = 4;
                    else if (em.HasComponent<IndustrialProperty>(e)) kind = 3;
                    else if (em.HasComponent<ServiceObjectData>(prefab)) kind = 5;
                    values[i * 4] = transforms[i].m_Position.x;
                    values[i * 4 + 1] = transforms[i].m_Position.z;
                    values[i * 4 + 2] = radius;
                    values[i * 4 + 3] = kind;
                }
                return new JsonObject
                {
                    { "count", entities.Length },
                    { "values", MapExport.Base64(values) },
                    { "layout", "per building 4 float32: x, z, lot radius (metres, half the lot diagonal), kind (0 other, 1 residential, 2 commercial, 3 industrial, 4 office, 5 city service)" },
                };
            }
        }
    }
}
