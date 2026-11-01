using System;
using System.Collections.Generic;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Transform = Game.Objects.Transform;

namespace CityAssistant.Export
{
    /// <summary>
    /// Phase 6 hole 16: natural resources at each specialised-industry site (farm, forest, ore, oil, fish areas).
    /// economy.json "extractionSites": per main building, the resource left inside its areas, how rich the
    /// ground is there (the concentration that sets the extractors' output) and how much was taken so far.
    /// </summary>
    public static class ResourcesExport
    {
        private class Site
        {
            public Entity Building;
            public string Feature;
            public int Areas;
            public double Surface, ResourceLeft, Extracted, ConcentrationSum, ConcentrationArea;
            public bool RequireNatural;
            public int Companies, Workers;
            public SortedSet<string> Products = new SortedSet<string>();
        }

        public static JsonObject ExtractionSites(GameData g, World world)
        {
            var em = g.EntityManager;
            ExtractorParameterData parameters = default;
            using (var pq = em.CreateEntityQuery(ComponentType.ReadOnly<ExtractorParameterData>()))
                if (!pq.IsEmptyIgnoreFilter)
                    parameters = pq.GetSingleton<ExtractorParameterData>();

            var sites = new Dictionary<Entity, Site>();
            using (var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Extractor>(), ComponentType.ReadOnly<Geometry>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Owner>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }))
            using (var entities = q.ToEntityArray(Allocator.Temp))
            using (var extractors = q.ToComponentDataArray<Extractor>(Allocator.Temp))
            using (var geometries = q.ToComponentDataArray<Geometry>(Allocator.Temp))
            using (var prefabs = q.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            using (var owners = q.ToComponentDataArray<Owner>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var building = MainBuilding(em, owners[i].m_Owner);
                    if (!em.HasComponent<ExtractorAreaData>(prefabs[i].m_Prefab))
                        continue;
                    var areaData = em.GetComponentData<ExtractorAreaData>(prefabs[i].m_Prefab);
                    if (!sites.TryGetValue(building, out var s))
                        sites[building] = s = new Site { Building = building, Feature = areaData.m_MapFeature.ToString(), RequireNatural = areaData.m_RequireNaturalResource };
                    var ex = extractors[i];
                    float surface = geometries[i].m_SurfaceArea;
                    s.Areas++;
                    s.Surface += surface;
                    s.ResourceLeft += ex.m_ResourceAmount;
                    s.Extracted += ex.m_TotalExtracted;
                    // Same weighting as ExtractorCompanySystem.GetBestConcentration: effective concentration
                    // min(1, max concentration / "full" level for that resource), averaged by surface area.
                    float c = areaData.m_RequireNaturalResource
                        ? Math.Min(1f, ExtractorCompanySystem.GetEffectiveConcentration(parameters, areaData.m_MapFeature, ex.m_MaxConcentration))
                        : 1f;
                    s.ConcentrationSum += c * surface;
                    s.ConcentrationArea += surface;
                }
            }

            // Extractor companies sit in small buildings attached to the site's main building.
            using (var companies = g.Companies.ToEntityArray(Allocator.Temp))
            using (var renters = g.Companies.ToComponentDataArray<PropertyRenter>(Allocator.Temp))
            using (var cprefabs = g.Companies.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < companies.Length; i++)
                {
                    if (!em.HasComponent<Game.Companies.ExtractorCompany>(companies[i]))
                        continue;
                    var property = renters[i].m_Property;
                    if (property == Entity.Null || !em.HasComponent<Attached>(property))
                        continue;
                    var building = MainBuilding(em, em.GetComponentData<Attached>(property).m_Parent);
                    if (!sites.TryGetValue(building, out var s))
                        continue;
                    s.Companies++;
                    if (em.HasBuffer<Employee>(companies[i]))
                        s.Workers += em.GetBuffer<Employee>(companies[i], true).Length;
                    if (em.HasComponent<IndustrialProcessData>(cprefabs[i].m_Prefab))
                        s.Products.Add(em.GetComponentData<IndustrialProcessData>(cprefabs[i].m_Prefab).m_Output.m_Resource.ToString());
                }
            }

            var dictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
            var names = world.GetExistingSystemManaged<Game.UI.NameSystem>();
            var rows = new List<object>();
            foreach (var s in sites.Values)
            {
                var b = s.Building;
                var row = new JsonObject();
                if (em.HasComponent<PrefabRef>(b))
                {
                    var prefabName = g.Prefabs.GetPrefabName(em.GetComponentData<PrefabRef>(b).m_Prefab);
                    string title = null;
                    dictionary?.TryGetValue($"Assets.NAME[{prefabName}]", out title);
                    row.Add("name", LeftoverExports.Label(g, names, b));
                    row.Add("type", title ?? prefabName);
                }
                row.Add("resource", s.Feature);
                row.Add("products", new List<object>(s.Products));
                row.Add("areas", s.Areas);
                row.Add("areaM2", Math.Round(s.Surface));
                row.Add("resourceLeft", Math.Round(s.ResourceLeft));
                row.Add("concentrationPercent", s.ConcentrationArea > 0 ? Math.Round(100.0 * s.ConcentrationSum / s.ConcentrationArea, 1) : 0.0);
                row.Add("totalExtracted", Math.Round(s.Extracted));
                row.Add("companies", s.Companies);
                row.Add("workers", s.Workers);
                if (em.HasComponent<Transform>(b))
                {
                    var p = em.GetComponentData<Transform>(b).m_Position;
                    row.Add("x", Math.Round(p.x, 1));
                    row.Add("z", Math.Round(p.z, 1));
                }
                rows.Add(row);
            }
            rows.Sort((a, b) => string.CompareOrdinal(((JsonObject)a).Get("resource") as string, ((JsonObject)b).Get("resource") as string));
            return new JsonObject
            {
                { "count", rows.Count },
                { "sites", rows },
                { "fullConcentration", new JsonObject { { "fertility", parameters.m_FullFertility }, { "ore", parameters.m_FullOre }, { "oil", parameters.m_FullOil }, { "fish", parameters.m_FullFish } } },
                { "note", "One row per specialised-industry site (main building + its drawn areas). resourceLeft = what is left inside the areas (game code AreaResourceSystem: wood from the trees, else resource map minus used, with city modifiers for ore/oil); concentrationPercent = how rich the ground is, area-weighted, 100 % = the game's 'full' level - it scales the extractors' output; totalExtracted = taken so far. Ore and oil do not grow back; fertility and fish recover; forest regrows as trees grow." },
            };
        }

        /// <summary>Climbs from an area's owner (often an upgrade) to the top building.</summary>
        private static Entity MainBuilding(EntityManager em, Entity e)
        {
            for (int i = 0; i < 4 && e != Entity.Null && em.HasComponent<Owner>(e); i++)
                e = em.GetComponentData<Owner>(e).m_Owner;
            return e;
        }
    }
}
