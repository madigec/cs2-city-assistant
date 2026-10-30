using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.Prefabs;
using Game.UI;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Per-district figures: what the game's district panel shows when the player clicks a district
    /// (residents, happiness, employees, local services, policies). Residents are counted by DistrictResidents
    /// (a fast copy of the game's calculation); the others come from the panel's own sections.
    /// The panel's sections calculate for whatever is selected, so for each district we briefly point the
    /// selection at it, let the sections calculate, read them, and put the player's selection back.
    /// All of this happens inside one update, so the screen never sees the borrowed selection.
    ///
    /// This uses private members of SelectedInfoUISystem (found by inspecting game version 1.6.1f1);
    /// the caller wraps it in GameData.Safe so a game update that breaks it becomes {"error": ...}.
    /// </summary>
    public static class DistrictsExport
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        public static JsonObject Build(GameData g, World world, string trigger)
        {
            var o = g.Header("districts", trigger);
            o.Add("districts", GameData.Safe("districts", () => Districts(g, world, false)));
            return o;
        }

        /// <summary>
        /// Self-test for the fast residents copy (live endpoint /check/districts): per district, the game's own
        /// ResidentsSection result next to DistrictResidents' result, every field that differs, and how long
        /// each section took. Slow on purpose (it runs the game's way too); run it after each game update.
        /// </summary>
        public static JsonObject BuildCheck(GameData g, World world)
        {
            var o = g.Header("districts-check", "check");
            o.Add("districts", GameData.Safe("districts", () => Districts(g, world, true)));
            return o;
        }

        private static List<object> Districts(GameData g, World world, bool check)
        {
            var info = world.GetExistingSystemManaged<SelectedInfoUISystem>();
            var names = world.GetExistingSystemManaged<NameSystem>();
            if (info == null || names == null)
                throw new InvalidOperationException("SelectedInfoUISystem or NameSystem not found");

            var entityField = typeof(SelectedInfoUISystem).GetField("m_SelectedEntity", Private);
            var prefabField = typeof(SelectedInfoUISystem).GetField("m_SelectedPrefab", Private);
            if (entityField == null || prefabField == null)
                throw new MissingFieldException(nameof(SelectedInfoUISystem), "m_SelectedEntity / m_SelectedPrefab");

            // The district panel's sections, under the names we export them as. Residents come from the fast
            // copy in DistrictResidents (the game's ResidentsSection froze the game; the check still runs it).
            // ProfitabilitySection also knows districts, but the game never adds it to the panel in 1.6.1f1.
            var keys = new[] { "happiness", "employees", "localServices", "policies" };
            var sections = new InfoSectionBase[]
            {
                world.GetExistingSystemManaged<AverageHappinessSection>(),
                world.GetExistingSystemManaged<EmployeesSection>(),
                world.GetExistingSystemManaged<LocalServicesSection>(),
                world.GetExistingSystemManaged<PoliciesSection>(),
            };
            var gameResidents = world.GetExistingSystemManaged<ResidentsSection>();

            var result = new List<object>();
            var playerEntity = entityField.GetValue(info);
            var playerPrefab = prefabField.GetValue(info);
            using (var districts = g.Districts.ToEntityArray(Allocator.TempJob))
            {
                var clock = Stopwatch.StartNew();
                object[] residents;
                try
                {
                    residents = world.GetOrCreateSystemManaged<DistrictResidents>().Compute(districts, g.CityEntity);
                }
                catch (Exception e)
                {
                    Mod.log.Warn($"Export section 'residents' failed: {e}");
                    var error = new JsonObject { { "error", e.GetType().Name + ": " + e.Message } };
                    residents = new object[districts.Length];
                    for (int i = 0; i < residents.Length; i++)
                        residents[i] = error;
                }
                var fastResidentsMs = clock.Elapsed.TotalMilliseconds;

                try
                {
                    for (int n = 0; n < districts.Length; n++)
                    {
                        var district = districts[n];
                        var prefab = g.EntityManager.HasComponent<PrefabRef>(district)
                            ? g.EntityManager.GetComponentData<PrefabRef>(district).m_Prefab
                            : Entity.Null;
                        entityField.SetValue(info, district);
                        prefabField.SetValue(info, prefab);

                        var d = new JsonObject { { "name", names.GetRenderedLabelName(district) }, { "residents", residents[n] } };
                        var timings = new JsonObject { { "residentsFastAllDistricts", Math.Round(fastResidentsMs, 1) } };
                        for (int i = 0; i < sections.Length; i++)
                        {
                            var section = sections[i];
                            var key = keys[i];
                            clock.Restart();
                            d.Add(key, GameData.Safe(key, () => Simplify(key, ReadSection(section))));
                            timings.Add(key, Math.Round(clock.Elapsed.TotalMilliseconds, 1));
                        }

                        if (check)
                        {
                            clock.Restart();
                            var game = GameData.Safe("residentsGame", () => ReadSection(gameResidents));
                            timings.Add("residentsGame", Math.Round(clock.Elapsed.TotalMilliseconds, 1));
                            var differences = new List<object>();
                            Compare(residents[n], game, "residents", differences);
                            d.Add("timingsMs", timings);
                            d.Add("residentsDifferences", differences);
                            d.Add("residentsGame", game);
                        }
                        result.Add(d);
                    }
                }
                finally
                {
                    entityField.SetValue(info, playerEntity);
                    prefabField.SetValue(info, playerPrefab);
                    // Makes an open info panel recalculate for the player's own selection next frame.
                    info.RequestUpdate();
                }
            }
            return result;
        }

        /// <summary>
        /// Every field of the fast result whose value differs from the game's (fields only the game writes,
        /// such as "isDistrict", are not compared). Adds {field, game, fast} entries.
        /// </summary>
        private static void Compare(object fast, object game, string path, List<object> differences)
        {
            var fastObject = fast as JsonObject;
            if (fastObject != null)
            {
                var gameObject = game as JsonObject;
                foreach (var entry in fastObject)
                    Compare(entry.Value, gameObject == null ? null : gameObject.Get(entry.Key), path + "." + entry.Key, differences);
                return;
            }
            var fastList = AsList(fast);
            var gameList = AsList(game);
            if (fastList != null || gameList != null)
            {
                var fastCount = fastList == null ? 0 : fastList.Count;
                var gameCount = gameList == null ? 0 : gameList.Count;
                for (int i = 0; i < Math.Max(fastCount, gameCount); i++)
                    Compare(i < fastCount ? fastList[i] : null, i < gameCount ? gameList[i] : null, path + "." + i, differences);
                return;
            }
            if (!SameValue(fast, game))
                differences.Add(new JsonObject { { "field", path }, { "game", game }, { "fast", fast } });
        }

        private static List<object> AsList(object value)
        {
            var items = value as IEnumerable;
            if (items == null || value is string || value is JsonObject)
                return null;
            var list = new List<object>();
            foreach (var item in items)
                list.Add(item);
            return list;
        }

        private static bool SameValue(object a, object b)
        {
            if (a == null || b == null)
                return a == null && b == null;
            if (a is string || b is string)
                return a.ToString() == b.ToString();
            try
            {
                return Convert.ToDecimal(a) == Convert.ToDecimal(b);
            }
            catch (Exception)
            {
                return Equals(a, b);
            }
        }

        /// <summary>
        /// Keeps only what an advisor needs from the two list sections: each policy's name and whether it is
        /// on (not its icon and unlock rules), and each local service building's type (not its entity).
        /// </summary>
        private static object Simplify(string key, object section)
        {
            if (!(section is JsonObject obj))
                return section;
            if (key == "policies" && obj.Get("policies") is List<object> policyList)
            {
                var result = new List<object>();
                foreach (var item in policyList)
                    if (item is JsonObject policy)
                        result.Add(new JsonObject { { "policy", policy.Get("id") }, { "active", policy.Get("active") } });
                return result;
            }
            if (key == "localServices" && obj.Get("localServiceBuildings") is List<object> buildingList)
            {
                var result = new List<object>();
                foreach (var item in buildingList)
                    if (item is JsonObject building)
                        result.Add(BuildingType(building.Get("name")));
                return result;
            }
            return section;
        }

        /// <summary>{"nameId": "Assets.NAME[Crematorium01]"} -> "Crematorium01"</summary>
        private static object BuildingType(object name)
        {
            var id = (name as JsonObject)?.Get("nameId") as string;
            if (id == null)
                return name;
            var open = id.IndexOf('[');
            return open >= 0 && id.EndsWith("]") ? id.Substring(open + 1, id.Length - open - 2) : id;
        }

        /// <summary>One section's numbers for the current selection, or null when the panel hides it (e.g. no residents).</summary>
        private static object ReadSection(InfoSectionBase section)
        {
            if (section == null)
                throw new InvalidOperationException("section not found");
            section.RequestUpdate();
            section.PerformUpdate();
            if (!section.visible)
                return null;
            return JsonCapture.Capture(writer =>
            {
                writer.TypeBegin(section.GetType().FullName);
                section.OnWriteProperties(writer);
                writer.TypeEnd();
            });
        }
    }
}
