using System;
using System.Collections.Generic;
using Game.Areas;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;
using AreaNode = Game.Areas.Node;

namespace CityAssistant.Export
{
    /// <summary>
    /// progression.json (Phase 6 hole 13): milestones with their rewards and what the next ones unlock, the
    /// development tree (points, every node, what can be bought now), signature buildings still locked with
    /// the game's own progress counters, and map tiles (permits left, price, upkeep and resources per tile).
    /// Everything follows the game code of version 1.6.1f1; formulas are quoted where we recompute them.
    /// </summary>
    public static class ProgressionExport
    {
        public static JsonObject Build(GameData g, World world, string trigger)
        {
            var o = g.Header("progression", trigger);
            o.Add("gameOptions", GameData.Safe("gameOptions", () => new JsonObject
            {
                { "unlockAll", g.Config.unlockAll },
                { "unlockAllMapTiles", g.Config.unlockMapTiles },
                { "unlimitedMoney", g.Config.unlimitedMoney },
            }));
            o.Add("milestones", GameData.Safe("milestones", () => Milestones(g, world)));
            o.Add("devTree", GameData.Safe("devTree", () => DevTree(g)));
            o.Add("mapTiles", GameData.Safe("mapTiles", () => MapTiles(g, world)));
            o.Add("signatureBuildings", GameData.Safe("signatureBuildings", () => SignatureBuildings(g, world)));
            o.Add("xpRules", GameData.Safe("xpRules", () => XpRules(g)));
            return o;
        }

        private static bool IsLocked(EntityManager em, Entity e) => em.HasComponent<Locked>(e) && em.IsComponentEnabled<Locked>(e);

        private static string Localize(string key)
        {
            var dictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
            string text = null;
            dictionary?.TryGetValue(key, out text);
            return text;
        }

        // ---------- milestones ----------

        private static JsonObject Milestones(GameData g, World world)
        {
            var em = g.EntityManager;
            var list = new List<(Entity e, MilestoneData d, bool locked)>();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<MilestoneData>(), ComponentType.ReadOnly<PrefabData>()))
            using (var entities = q.ToEntityArray(Allocator.Temp))
            using (var data = q.ToComponentDataArray<MilestoneData>(Allocator.Temp))
                for (int i = 0; i < entities.Length; i++)
                    list.Add((entities[i], data[i], IsLocked(em, entities[i])));
            list.Sort((a, b) => a.d.m_Index.CompareTo(b.d.m_Index));

            g.TryGetCityComponent<MilestoneLevel>(out var level);
            var rows = new List<object>();
            int detailed = 0;
            foreach (var m in list)
            {
                var row = new JsonObject
                {
                    { "index", m.d.m_Index },
                    { "reached", !m.locked },
                    { "xpRequired", m.d.m_XpRequried },
                    { "reward", m.d.m_Reward },
                    { "devTreePoints", m.d.m_DevTreePoints },
                    { "mapTiles", m.d.m_MapTiles },
                    { "loanLimit", m.d.m_LoanLimit },
                    { "major", m.d.m_Major },
                };
                if (m.d.m_IsVictory)
                    row.Add("isVictory", true);
                // What the next three milestones bring: the Progression panel's own list (MilestoneUISystem).
                if (m.locked && detailed < 3)
                {
                    detailed++;
                    row.Add("unlocks", GameData.Safe("unlocks", () => SimplifyUnlocks(InfoviewReader.CallWriter<MilestoneUISystem>(world, "BindMilestoneUnlocks", m.e))));
                }
                rows.Add(row);
            }
            return new JsonObject
            {
                { "achieved", level.m_AchievedMilestone },
                { "totalXP", g.City.XP },
                { "allReached", list.TrueForAll(m => !m.locked) },
                { "list", rows },
                { "note", "reward = money paid on reaching it; loanLimit = the new loan ceiling; mapTiles = extra map tile permits; devTreePoints = development points. 'unlocks' (next 3 milestones only) = the Progression panel's list: features, zones, services with their buildings, policies." },
            };
        }

        /// <summary>Panel entries -> {kind, name, title, assets}, without the UI-only entity ids and icons.</summary>
        private static object SimplifyUnlocks(object captured)
        {
            var result = new List<object>();
            if (!(captured is List<object> items))
                return captured;
            foreach (var item in items)
            {
                if (!(item is JsonObject o))
                    continue;
                var name = o.Get("name") as string;
                var assets = o.Get("assets") as List<object>;
                string kind = assets != null ? "service" : o.Get("themes") != null ? "zone" : null;
                string title = kind == "service" ? Localize($"Services.NAME[{name}]")
                    : Localize($"Assets.NAME[{name}]") ?? Localize($"Policy.TITLE[{name}]") ?? Localize($"Services.NAME[{name}]");
                if (kind == null)
                    kind = Localize($"Policy.TITLE[{name}]") != null ? "policy" : "feature";
                var entry = new JsonObject { { "kind", kind }, { "name", name }, { "title", title } };
                if (kind == "service")
                {
                    entry.Add("devTreeUnlocked", o.Get("devTreeUnlocked"));
                    var buildings = new List<object>();
                    foreach (var a in assets)
                        if (a is JsonObject ao && ao.Get("name") is string an)
                            buildings.Add(Localize($"Assets.NAME[{an}]") ?? an);
                    entry.Add("buildings", buildings);
                }
                result.Add(entry);
            }
            return result;
        }

        // ---------- development tree ----------

        private static JsonObject DevTree(GameData g)
        {
            var em = g.EntityManager;
            int stored = 0;
            using (var pq = em.CreateEntityQuery(ComponentType.ReadOnly<DevTreePoints>()))
                if (!pq.IsEmptyIgnoreFilter)
                    stored = pq.GetSingleton<DevTreePoints>().m_Points;

            var services = new SortedDictionary<string, List<object>>();
            var serviceLocked = new Dictionary<string, bool>();
            int lockedCostSum = 0, bought = 0, total = 0;
            var nodes = new List<(Entity e, DevTreeNodeData d, bool locked)>();
            using (var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<DevTreeNodeData>(), ComponentType.ReadOnly<PrefabData>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            }))
            using (var entities = q.ToEntityArray(Allocator.Temp))
            using (var data = q.ToComponentDataArray<DevTreeNodeData>(Allocator.Temp))
                for (int i = 0; i < entities.Length; i++)
                {
                    var locked = IsLocked(em, entities[i]);
                    nodes.Add((entities[i], data[i], locked));
                    if (locked)
                        lockedCostSum += data[i].m_Cost;
                }
            // The panel shows min(stored points, cost of everything still locked) (DevTreeUISystem.GetDevTreePoints).
            int points = Math.Min(stored, lockedCostSum);

            foreach (var n in nodes)
            {
                total++;
                if (!n.locked)
                    bought++;
                string name = g.Prefabs.GetPrefabName(n.e);
                string service = n.d.m_Service != Entity.Null ? g.Prefabs.GetPrefabName(n.d.m_Service) : "(none)";
                bool svcLocked = n.d.m_Service != Entity.Null && IsLocked(em, n.d.m_Service);
                serviceLocked[service] = svcLocked;
                // Purchase rule (DevTreeSystem.Purchase): node still locked, its service unlocked, enough points,
                // and no requirement or AT LEAST ONE required node bought (CheckRequirements returns on the first).
                var requires = new List<object>();
                bool anyReqBought = false, hasReq = false;
                if (em.HasBuffer<DevTreeNodeRequirement>(n.e))
                {
                    var reqs = em.GetBuffer<DevTreeNodeRequirement>(n.e, true);
                    for (int i = 0; i < reqs.Length; i++)
                    {
                        if (reqs[i].m_Node == Entity.Null)
                            continue;
                        hasReq = true;
                        if (!IsLocked(em, reqs[i].m_Node))
                            anyReqBought = true;
                        var rn = g.Prefabs.GetPrefabName(reqs[i].m_Node);
                        requires.Add(Localize($"Progression.NODE_NAME[{rn}]") ?? rn);
                    }
                }
                bool open = n.locked && !svcLocked && (!hasReq || anyReqBought);
                if (!services.TryGetValue(service, out var list))
                    services[service] = list = new List<object>();
                var row = new JsonObject
                {
                    { "name", name },
                    { "title", Localize($"Progression.NODE_NAME[{name}]") },
                    { "cost", n.d.m_Cost },
                    { "bought", !n.locked },
                    { "available", open },
                    { "affordable", open && n.d.m_Cost <= points },
                };
                if (requires.Count > 0)
                    row.Add("requires", requires);
                var description = Localize($"Progression.NODE_DESCRIPTION[{name}]");
                if (description != null)
                    row.Add("description", description);
                list.Add(row);
            }

            var serviceRows = new List<object>();
            foreach (var kv in services)
                serviceRows.Add(new JsonObject
                {
                    { "service", kv.Key },
                    { "title", Localize($"Services.NAME[{kv.Key}]") },
                    { "serviceLocked", serviceLocked.TryGetValue(kv.Key, out var l) && l },
                    { "nodes", kv.Value },
                });
            return new JsonObject
            {
                { "points", points },
                { "storedPoints", stored },
                { "nodesBought", bought },
                { "nodesTotal", total },
                { "services", serviceRows },
                { "note", "points = unspent development points as the panel shows them. available = can be bought once you have the points (service unlocked, and no prerequisite or at least one prerequisite bought - game code DevTreeSystem.Purchase); affordable = available and cheap enough now. Points come only from milestones." },
            };
        }

        // ---------- map tiles ----------

        // MapTilePurchaseSystem constants (private in the game): feature value = amount x modifier x 10 x
        // feature cost (tile prefab MapFeatureData) x tile cost factor (TilePurchaseCostFactor).
        private static readonly double kSizeModifier = 1.0 / Math.Pow(623.304347826087, 2.0);
        private const double kResourceModifier = 8.0718994140625E-07;
        private static readonly double[] kBaseline = { kSizeModifier, kSizeModifier, kResourceModifier, kResourceModifier, kResourceModifier, kResourceModifier, 1.0, kResourceModifier };
        private static readonly string[] kFeatures = { "area", "buildableLand", "fertileLand", "forest", "oil", "ore", "surfaceWater", "groundWater", "fish" };

        private static JsonObject MapTiles(GameData g, World world)
        {
            var em = g.EntityManager;
            var purchase = world.GetOrCreateSystemManaged<MapTilePurchaseSystem>();
            var tileSystem = world.GetOrCreateSystemManaged<MapTileSystem>();
            var resources = world.GetOrCreateSystemManaged<NaturalResourceSystem>();
            var startTiles = new HashSet<Entity>();
            var start = tileSystem.GetStartTiles();
            for (int i = 0; i < start.Length; i++)
                startTiles.Add(start[i]);

            var tiles = new List<(Entity e, bool owned, double value, float[] amounts, float x, float z)>();
            double ownedValue = 0;
            int ownedCount = 0;
            using (var entities = g.MapTiles.ToEntityArray(Allocator.Temp))
            {
                foreach (var tile in entities)
                {
                    bool owned = !em.HasComponent<Native>(tile);
                    var amounts = new float[kFeatures.Length];
                    double value = 0;
                    if (em.HasBuffer<MapFeatureElement>(tile) && em.HasComponent<PrefabRef>(tile))
                    {
                        var features = em.GetBuffer<MapFeatureElement>(tile, true);
                        var prefab = em.GetComponentData<PrefabRef>(tile).m_Prefab;
                        float factor = em.HasComponent<TilePurchaseCostFactor>(prefab) ? em.GetComponentData<TilePurchaseCostFactor>(prefab).m_Amount : 0f;
                        var costs = em.HasBuffer<MapFeatureData>(prefab) ? em.GetBuffer<MapFeatureData>(prefab, true) : default;
                        for (int j = 0; j < features.Length && j < amounts.Length; j++)
                        {
                            amounts[j] = features[j].m_Amount;
                            if (costs.IsCreated && j < costs.Length)
                                value += features[j].m_Amount * (j < kBaseline.Length ? kBaseline[j] : 1.0) * 10.0 * costs[j].m_Cost * factor;
                        }
                    }
                    float x = 0, z = 0;
                    if (em.HasBuffer<AreaNode>(tile))
                    {
                        var nodes = em.GetBuffer<AreaNode>(tile, true);
                        for (int i = 0; i < nodes.Length; i++)
                        {
                            x += nodes[i].m_Position.x;
                            z += nodes[i].m_Position.z;
                        }
                        if (nodes.Length > 0)
                        {
                            x /= nodes.Length;
                            z /= nodes.Length;
                        }
                    }
                    if (owned)
                    {
                        ownedCount++;
                        // Start tiles never count toward tile value or upkeep (CalculateOwnedTilesCost skips them).
                        if (!startTiles.Contains(tile))
                            ownedValue += value;
                    }
                    tiles.Add((tile, owned, value, amounts, x, z));
                }
            }

            float multNow = purchase.GetMapTileUpkeepCostMultiplier(ownedCount);
            float multNext = purchase.GetMapTileUpkeepCostMultiplier(ownedCount + 1);
            var rows = new List<object>();
            foreach (var t in tiles)
            {
                var row = new JsonObject
                {
                    { "x", Math.Round(t.x, 1) },
                    { "z", Math.Round(t.z, 1) },
                    { "owned", t.owned },
                };
                if (startTiles.Contains(t.e))
                    row.Add("startTile", true);
                var f = new JsonObject();
                for (int j = 0; j < kFeatures.Length; j++)
                {
                    // The panel turns fertile land into an area (NaturalResourceSystem.ResourceAmountToArea).
                    double v = j == 2 ? resources.ResourceAmountToArea(t.amounts[j]) : t.amounts[j];
                    if (v != 0)
                        f.Add(kFeatures[j], Math.Round(v, 1));
                }
                row.Add("features", f);
                if (!t.owned)
                {
                    // Buying this tile alone: price = value x tiles owned (UpdateStatus: m_Cost += value x (owned + k)).
                    row.Add("price", (long)Math.Round(t.value * ownedCount));
                    // True upkeep change = (owned value + value) x curve(owned + 1) - owned value x curve(owned).
                    row.Add("upkeepIncrease", (long)Math.Round((ownedValue + t.value) * multNext - ownedValue * multNow));
                    // The purchase panel's own upkeep figure (UpdateStatus, selection of 1): it adds the selection twice
                    // and leaves the owned tiles out. Kept to compare with the panel in game.
                    row.Add("panelUpkeep", (long)Math.Round(2 * t.value * multNext - t.value * multNow));
                }
                rows.Add(row);
            }
            return new JsonObject
            {
                { "owned", ownedCount },
                { "count", tiles.Count },
                { "permitsAvailable", purchase.GetAvailableTiles() },
                { "moreFromMilestones", purchase.IsMilestonesLeft() },
                { "upkeepNow", purchase.CalculateOwnedTilesUpkeep() },
                { "upkeepEnabled", purchase.GetMapTileUpkeepEnabled() },
                { "upkeepMultiplierNow", Math.Round(multNow, 4) },
                { "upkeepMultiplierNext", Math.Round(multNext, 4) },
                { "tiles", rows },
                { "note", "permitsAvailable = max(9, start tiles) + map tiles from reached milestones - tiles owned (MapTilePurchaseSystem.GetAvailableTiles). price = one tile bought alone; buying several, the most valuable is charged x owned, the next x (owned + 1) and so on. upkeepIncrease = monthly tile upkeep added (our calculation); panelUpkeep = what the purchase panel shows (game formula, may differ). The first 9 tiles cost no upkeep. features: area, buildableLand, fertileLand in m2; forest, oil, ore, fish in kg; groundWater in m3; surfaceWater raw. GAME BUG (checked in game, Felburg 2026-11-01): the purchase panel shows the selected tiles' resources and upkeep doubled; these values are the real ones. x, z = tile centre (world metres)." },
            };
        }

        // ---------- signature buildings ----------

        private static JsonObject SignatureBuildings(GameData g, World world)
        {
            var em = g.EntityManager;
            var prefabUI = world.GetOrCreateSystemManaged<PrefabUISystem>();
            var locked = new List<object>();
            int unlockedCount = 0;
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<SignatureBuildingData>(), ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<UIObjectData>()))
            using (var entities = q.ToEntityArray(Allocator.Temp))
            {
                foreach (var e in entities)
                {
                    if (!IsLocked(em, e))
                    {
                        unlockedCount++;
                        continue;
                    }
                    var name = g.Prefabs.GetPrefabName(e);
                    var row = new JsonObject
                    {
                        { "name", name },
                        { "title", Localize($"Assets.NAME[{name}]") },
                        { "requirements", GameData.Safe("requirements", () => Clean(JsonCapture.Capture(w => prefabUI.BindPrefabRequirements(w, e)))) },
                    };
                    locked.Add(row);
                }
            }
            return new JsonObject
            {
                { "unlocked", unlockedCount },
                { "lockedCount", locked.Count },
                { "locked", locked },
                { "note", "requirements = the game's unlock conditions (PrefabUISystem.BindPrefabRequirements): requireAll must all be met, requireAny one of them. Each has locked (not met yet) and progress (the game's counter toward the minimum, e.g. citizens, zone squares or buildings, produced amount, passengers)." },
            };
        }

        /// <summary>Drops UI-only fields (entity ids, icons) from captured panel output.</summary>
        private static object Clean(object value)
        {
            switch (value)
            {
                case JsonObject o:
                    var copy = new JsonObject();
                    foreach (var kv in o)
                        if (kv.Key != "entity" && kv.Key != "icon")
                            copy.Add(kv.Key, Clean(kv.Value));
                    return copy;
                case List<object> list:
                    var items = new List<object>();
                    foreach (var item in list)
                        items.Add(Clean(item));
                    return items;
                default:
                    return value;
            }
        }

        // ---------- XP rules ----------

        private static JsonObject XpRules(GameData g)
        {
            var em = g.EntityManager;
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<XPParameterData>()))
            {
                if (q.IsEmptyIgnoreFilter)
                    return null;
                var xp = q.GetSingleton<XPParameterData>();
                return new JsonObject
                {
                    { "xpPerPopulation", xp.m_XPPerPopulation },
                    { "xpPerHappiness", xp.m_XPPerHappiness },
                    { "note", "XP sources (game code XPSystem, XPAccumulationSystem, XPBuiltSystem): new population records only (growth above the highest population ever, x xpPerPopulation per citizen), average happiness (x xpPerHappiness, 32 times a game day), each service building and upgrade built (XP set per building), roads and tracks built, the first electricity grid." },
                };
            }
        }
    }
}
