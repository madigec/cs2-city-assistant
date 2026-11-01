using System;
using System.Collections.Generic;
using Game.Areas;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Phase 6 hole 12: what policies and special buildings actually change. The game sums every city-wide
    /// effect (from city policies and from signature / unique buildings and their upgrades) into one modifier
    /// per type on the city; a value is changed by value += add, then value += value x percent / 100
    /// (CityUtils.ApplyModifier). Districts have their own modifiers (DistrictModifier), buildings with local
    /// effects carry them in their prefab (LocalModifierData).
    /// </summary>
    public static class PoliciesExport
    {
        private static JsonObject Effect(string type, float add, float relative)
        {
            return new JsonObject
            {
                { "effect", type },
                { "add", Math.Round(add, 4) },
                { "percent", Math.Round(relative * 100, 2) },
            };
        }

        public static JsonObject Build(GameData g, World world)
        {
            var em = g.EntityManager;
            var city = new List<object>();
            if (em.HasBuffer<CityModifier>(g.CityEntity))
            {
                var mods = em.GetBuffer<CityModifier>(g.CityEntity, true);
                for (int i = 0; i < mods.Length; i++)
                {
                    var d = mods[i].m_Delta;
                    if (Math.Abs(d.x) > 1e-6 || Math.Abs(d.y) > 1e-6)
                        city.Add(Effect(((CityModifierType)i).ToString(), d.x, d.y));
                }
            }

            var districts = new List<object>();
            var names = world.GetExistingSystemManaged<NameSystem>();
            using (var entities = g.Districts.ToEntityArray(Allocator.Temp))
            {
                foreach (var d in entities)
                {
                    if (!em.HasBuffer<DistrictModifier>(d))
                        continue;
                    var mods = em.GetBuffer<DistrictModifier>(d, true);
                    var effects = new List<object>();
                    for (int i = 0; i < mods.Length; i++)
                    {
                        var v = mods[i].m_Delta;
                        if (Math.Abs(v.x) < 1e-6 && Math.Abs(v.y) < 1e-6)
                            continue;
                        var type = (DistrictModifierType)i;
                        var e = Effect(type.ToString(), v.x, v.y);
                        if (type == DistrictModifierType.ProductConsumption || type == DistrictModifierType.BuildingUpkeep)
                            e.Add("note", "this effect does nothing in game version 1.6.1f1 (never read by the simulation)");
                        effects.Add(e);
                    }
                    string name = null;
                    try { name = names?.GetRenderedLabelName(d); } catch (Exception) { }
                    districts.Add(new JsonObject { { "district", name }, { "effects", effects } });
                }
            }

            // Buildings with area effects, grouped by building type.
            var local = new SortedDictionary<string, (int count, Entity prefab)>();
            using (var prefabs = g.Buildings.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                foreach (var p in prefabs)
                {
                    if (!em.HasBuffer<LocalModifierData>(p.m_Prefab))
                        continue;
                    string name = g.Prefabs.GetPrefabName(p.m_Prefab);
                    local.TryGetValue(name, out var v);
                    local[name] = (v.count + 1, p.m_Prefab);
                }
            }
            var localList = new List<object>();
            foreach (var kv in local)
            {
                var effects = new List<object>();
                var data = em.GetBuffer<LocalModifierData>(kv.Value.prefab, true);
                for (int i = 0; i < data.Length; i++)
                    effects.Add(new JsonObject
                    {
                        { "effect", data[i].m_Type.ToString() },
                        { "mode", data[i].m_Mode.ToString() },
                        { "value", new[] { Math.Round(data[i].m_Delta.min, 3), Math.Round(data[i].m_Delta.max, 3) } },
                        { "radiusMetres", new[] { Math.Round(data[i].m_Radius.min), Math.Round(data[i].m_Radius.max) } },
                    });
                localList.Add(new JsonObject { { "building", kv.Key }, { "count", kv.Value.count }, { "effects", effects } });
            }

            return new JsonObject
            {
                { "cityEffects", city },
                { "districtEffects", districts },
                { "buildingsWithAreaEffects", localList },
                { "note", "cityEffects: every active city-wide effect, the sum of all policies and special buildings; a value becomes (value + add) x (1 + percent / 100) (CityUtils.ApplyModifier). Which policy or building contributes which part is not split out. Area effects: value and radius as the min..max range stored in the building's game data." },
            };
        }
    }
}
