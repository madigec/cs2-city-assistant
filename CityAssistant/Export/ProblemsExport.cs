using System.Collections.Generic;
using Game.Common;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// problems.json: every warning icon the game shows over buildings, roads, lines and vehicles, counted per
    /// icon type, with the position of each. An icon is an entity with Icon + PrefabRef (the notification
    /// prefab); the type name is the prefab name, and the in-game title is "Notifications.TITLE[name]" (the
    /// same key the game's UI uses). Left out: tool previews (Temp), icons being removed (Deleted), and the
    /// Marker / Transaction layers (selection markers and money pop-ups, not problems).
    /// </summary>
    public static class ProblemsExport
    {
        private class Group
        {
            public Entity Prefab;
            public int Count;
            public int Hidden;
            public IconPriority Priority;
            public readonly List<float> Positions = new List<float>();
        }

        public static JsonObject Build(GameData g, string trigger)
        {
            var o = g.Header("problems", trigger);
            o.Add("problems", GameData.Safe("problems", () => Collect(g)));
            return o;
        }

        private static JsonObject Collect(GameData g)
        {
            var em = g.EntityManager;
            var groups = new Dictionary<Entity, Group>();
            using (var entities = g.Icons.ToEntityArray(Allocator.Temp))
            using (var icons = g.Icons.ToComponentDataArray<Icon>(Allocator.Temp))
            using (var prefabs = g.Icons.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var icon = icons[i];
                    if (icon.m_ClusterLayer != IconClusterLayer.Default)
                        continue;
                    if (!groups.TryGetValue(prefabs[i].m_Prefab, out var group))
                        groups.Add(prefabs[i].m_Prefab, group = new Group { Prefab = prefabs[i].m_Prefab });
                    if (em.HasComponent<Hidden>(entities[i]))
                    {
                        group.Hidden++;
                        continue;
                    }
                    group.Count++;
                    if (icon.m_Priority > group.Priority)
                        group.Priority = icon.m_Priority;
                    group.Positions.Add(icon.m_Location.x);
                    group.Positions.Add(icon.m_Location.z);
                }
            }

            var sorted = new List<Group>(groups.Values);
            sorted.Sort((a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : a.Prefab.Index.CompareTo(b.Prefab.Index));
            var dictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
            var list = new List<object>();
            var byPriority = new SortedDictionary<IconPriority, int>();
            int total = 0;
            foreach (var group in sorted)
            {
                string name = g.Prefabs.GetPrefabName(group.Prefab);
                string title = null, description = null;
                dictionary?.TryGetValue($"Notifications.TITLE[{name}]", out title);
                dictionary?.TryGetValue($"Notifications.DESCRIPTION[{name}]", out description);
                list.Add(new JsonObject
                {
                    { "type", name },
                    { "title", title ?? name },
                    { "description", description },
                    { "priority", group.Count > 0 ? (object)group.Priority : null },
                    { "count", group.Count },
                    { "hiddenCount", group.Hidden },
                    { "positions", MapExport.Base64(group.Positions.ToArray()) },
                });
                if (group.Count > 0)
                {
                    total += group.Count;
                    byPriority.TryGetValue(group.Priority, out var n);
                    byPriority[group.Priority] = n + group.Count;
                }
            }
            var priorities = new JsonObject();
            foreach (var kv in byPriority)
                priorities.Add(kv.Key.ToString(), kv.Value);
            return new JsonObject
            {
                { "total", total },
                { "buildingCount", g.Buildings.CalculateEntityCount() },
                { "byPriority", priorities },
                { "priorityScale", "Info < Problem < Warning < MajorProblem < Error < FatalProblem (the game's IconPriority; a type's priority is the highest among its icons)" },
                { "positionsLayout", "base64 float32 little-endian, per icon 2 floats: x, z (metres, map centre = 0)" },
                { "types", list },
            };
        }
    }
}
