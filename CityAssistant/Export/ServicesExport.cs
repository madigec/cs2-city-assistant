using System.Collections.Generic;
using Game.Prefabs;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Layer 2, Public services domain: what each service's info view panel shows,
    /// plus which service buildings exist (the budget only shows categories).
    /// </summary>
    public static class ServicesExport
    {
        public static JsonObject Build(GameData g, World world, string trigger)
        {
            var o = g.Header("services", trigger);

            o.Add("garbage", GameData.Safe("garbage", () => InfoviewReader.Read<GarbageInfoviewUISystem>(world)));
            o.Add("healthcareAndDeathcare", GameData.Safe("healthcareAndDeathcare", () => InfoviewReader.Read<HealthcareInfoviewUISystem>(world)));
            o.Add("police", GameData.Safe("police", () => InfoviewReader.Read<PoliceInfoviewUISystem>(world)));
            o.Add("fireAndRescue", GameData.Safe("fireAndRescue", () => InfoviewReader.Read<FireAndRescueInfoviewUISystem>(world)));
            o.Add("education", GameData.Safe("education", () => InfoviewReader.Read<EducationInfoviewUISystem>(world)));
            o.Add("post", GameData.Safe("post", () => InfoviewReader.Read<PostInfoviewUISystem>(world)));
            o.Add("telecom", GameData.Safe("telecom", () => InfoviewReader.Read<TelecomInfoviewUISystem>(world)));
            o.Add("disasterShelters", GameData.Safe("disasterShelters", () => InfoviewReader.Read<DisasterInfoviewUISystem>(world)));

            o.Add("buildings", GameData.Safe("buildings", () => Buildings(g)));
            o.Add("buildingDetails", GameData.Safe("buildingDetails", () => ServiceBuildingsExport.Services(g)));
            return o;
        }

        /// <summary>
        /// Every service building type the city has at least one of, grouped by service category.
        /// </summary>
        private static JsonObject Buildings(GameData g)
        {
            var byService = new JsonObject();
            using (var services = g.ServicePrefabs.ToEntityArray(Allocator.Temp))
            {
                foreach (var servicePrefab in services)
                {
                    var list = new List<JsonObject>();
                    var buildingPrefabs = g.Budget.GetServiceBuildings(servicePrefab);
                    if (buildingPrefabs != null)
                    {
                        foreach (var buildingPrefab in buildingPrefabs)
                        {
                            var count = g.Budget.GetNumberOfServiceBuildings(buildingPrefab);
                            if (count <= 0)
                                continue;
                            var staff = g.Budget.GetWorkersAndWorkplaces(buildingPrefab);
                            list.Add(new JsonObject
                            {
                                { "building", g.Prefabs.GetPrefabName(buildingPrefab) },
                                { "count", count },
                                { "workers", staff.x },
                                { "workplaces", staff.y },
                            });
                        }
                    }
                    if (list.Count > 0)
                        byService.Add(g.EntityManager.GetComponentData<ServiceData>(servicePrefab).m_Service.ToString(), list);
                }
            }
            return byService;
        }
    }
}
