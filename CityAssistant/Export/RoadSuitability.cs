using System;
using System.Collections.Generic;
using Game;
using Game.Economy;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CityAssistant.Export
{
    /// <summary>
    /// The game's own zone suitability per road (the "Residential / Commercial / ... Suitability" road colour
    /// of the zoning info views), for every zone type in use: the same call NetColorSystem makes,
    /// InfoviewUtils.GetColor -> ZoneEvaluationUtils.GetScore with the road's resource availability, land
    /// value (divided by the zone's ZonePropertiesData multiplier), ground/noise/air pollution at the road's
    /// ends, industrial and storage demand and the zone's process estimates. 0 = bad (red) .. 1 = good (green).
    /// Also each road's density (residents + jobs per metre, Game.Net.Density), which decides how fast a
    /// service building's capacity runs out.
    /// Never scheduled: Compute() runs a Burst job on demand.
    /// </summary>
    public partial class RoadSuitability : GameSystemBase
    {
        [BurstCompile]
        private struct SuitabilityJob : IJob
        {
            [ReadOnly] public NativeArray<Entity> m_Roads;
            [ReadOnly] public NativeArray<Entity> m_Zones;
            [ReadOnly] public NativeArray<InfoviewAvailabilityData> m_ZoneKinds;
            [ReadOnly] public ComponentLookup<Edge> m_Edges;
            [ReadOnly] public ComponentLookup<Game.Net.Node> m_Nodes;
            [ReadOnly] public ComponentLookup<LandValue> m_LandValues;
            [ReadOnly] public ComponentLookup<ResourceData> m_ResourceDatas;
            [ReadOnly] public ComponentLookup<ZonePropertiesData> m_ZoneProperties;
            [ReadOnly] public BufferLookup<ResourceAvailability> m_Availability;
            [ReadOnly] public BufferLookup<ProcessEstimate> m_Estimates;
            [ReadOnly] public ResourcePrefabs m_ResourcePrefabs;
            [ReadOnly] public NativeArray<GroundPollution> m_Ground;
            [ReadOnly] public NativeArray<NoisePollution> m_Noise;
            [ReadOnly] public NativeArray<AirPollution> m_Air;
            [ReadOnly] public NativeArray<int> m_IndustrialDemands;
            [ReadOnly] public NativeArray<int> m_StorageDemands;
            [ReadOnly] public NativeList<IndustrialProcessData> m_Processes;
            public ZonePreferenceData m_Preferences;
            public NativeArray<float> m_Result;   // per road, per zone, 2 ends; -1 = no data

            public void Execute()
            {
                int zones = m_Zones.Length;
                for (int i = 0; i < m_Roads.Length; i++)
                {
                    var road = m_Roads[i];
                    if (!m_Availability.TryGetBuffer(road, out var availability) || availability.Length == 0 || !m_Edges.TryGetComponent(road, out var edge)
                        || !m_LandValues.TryGetComponent(edge.m_Start, out var lv0) || !m_LandValues.TryGetComponent(edge.m_End, out var lv1))
                    {
                        for (int k = 0; k < zones * 2; k++)
                            m_Result[i * zones * 2 + k] = -1f;
                        continue;
                    }
                    var p0 = m_Nodes[edge.m_Start].m_Position;
                    var p1 = m_Nodes[edge.m_End].m_Position;
                    var pollution0 = new float3(GroundPollutionSystem.GetPollution(p0, m_Ground).m_Pollution, NoisePollutionSystem.GetPollution(p0, m_Noise).m_Pollution, AirPollutionSystem.GetPollution(p0, m_Air).m_Pollution);
                    var pollution1 = new float3(GroundPollutionSystem.GetPollution(p1, m_Ground).m_Pollution, NoisePollutionSystem.GetPollution(p1, m_Noise).m_Pollution, AirPollutionSystem.GetPollution(p1, m_Air).m_Pollution);
                    for (int z = 0; z < zones; z++)
                    {
                        var zone = m_Zones[z];
                        var kind = m_ZoneKinds[z];
                        float value0 = lv0.m_LandValue, value1 = lv1.m_LandValue;
                        if (m_ZoneProperties.TryGetComponent(zone, out var props))
                        {
                            float divisor = kind.m_AreaType != AreaType.Residential ? props.m_SpaceMultiplier
                                : (props.m_ScaleResidentials ? props.m_ResidentialProperties : props.m_ResidentialProperties / 8f);
                            value0 /= divisor;
                            value1 /= divisor;
                        }
                        m_Estimates.TryGetBuffer(zone, out var estimates);
                        m_Result[(i * zones + z) * 2] = InfoviewUtils.GetColor(kind, availability, 0f, ref m_Preferences, m_IndustrialDemands, m_StorageDemands,
                            pollution0, value0, estimates, m_Processes, m_ResourcePrefabs, m_ResourceDatas);
                        m_Result[(i * zones + z) * 2 + 1] = InfoviewUtils.GetColor(kind, availability, 1f, ref m_Preferences, m_IndustrialDemands, m_StorageDemands,
                            pollution1, value1, estimates, m_Processes, m_ResourcePrefabs, m_ResourceDatas);
                    }
                }
            }
        }

        private EntityQuery m_Preferences;
        private EntityQuery m_ProcessQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Preferences = GetEntityQuery(ComponentType.ReadOnly<ZonePreferenceData>());
            m_ProcessQuery = GetEntityQuery(ComponentType.ReadOnly<IndustrialProcessData>());
        }

        protected override void OnUpdate()
        {
        }

        /// <summary>Suitability of each road for each zone prefab (base64 float32, layout per road: zones x 2 ends).</summary>
        public float[] Compute(NativeArray<Entity> roads, IList<Entity> zonePrefabs)
        {
            EntityManager.CompleteAllTrackedJobs();
            var zones = new NativeArray<Entity>(zonePrefabs.Count, Allocator.TempJob);
            var kinds = new NativeArray<InfoviewAvailabilityData>(zonePrefabs.Count, Allocator.TempJob);
            for (int z = 0; z < zonePrefabs.Count; z++)
            {
                zones[z] = zonePrefabs[z];
                var data = EntityManager.GetComponentData<ZoneData>(zonePrefabs[z]);
                kinds[z] = new InfoviewAvailabilityData { m_AreaType = data.m_AreaType, m_Office = (data.m_ZoneFlags & ZoneFlags.Office) != 0 };
            }
            var result = new NativeArray<float>(roads.Length * zonePrefabs.Count * 2, Allocator.TempJob);
            var processes = m_ProcessQuery.ToComponentDataListAsync<IndustrialProcessData>(Allocator.TempJob, out var processDeps);
            try
            {
                var industrial = World.GetExistingSystemManaged<IndustrialDemandSystem>();
                var job = new SuitabilityJob
                {
                    m_Roads = roads,
                    m_Zones = zones,
                    m_ZoneKinds = kinds,
                    m_Edges = GetComponentLookup<Edge>(true),
                    m_Nodes = GetComponentLookup<Game.Net.Node>(true),
                    m_LandValues = GetComponentLookup<LandValue>(true),
                    m_ResourceDatas = GetComponentLookup<ResourceData>(true),
                    m_ZoneProperties = GetComponentLookup<ZonePropertiesData>(true),
                    m_Availability = GetBufferLookup<ResourceAvailability>(true),
                    m_Estimates = GetBufferLookup<ProcessEstimate>(true),
                    m_ResourcePrefabs = World.GetExistingSystemManaged<ResourceSystem>().GetPrefabs(),
                    m_Ground = World.GetExistingSystemManaged<GroundPollutionSystem>().GetMap(true, out var d1),
                    m_Noise = World.GetExistingSystemManaged<NoisePollutionSystem>().GetMap(true, out var d2),
                    m_Air = World.GetExistingSystemManaged<AirPollutionSystem>().GetMap(true, out var d3),
                    m_IndustrialDemands = industrial.GetBuildingDemands(out var d4),
                    m_StorageDemands = industrial.GetStorageBuildingDemands(out var d5),
                    m_Processes = processes,
                    m_Preferences = m_Preferences.CalculateEntityCount() > 0 ? m_Preferences.GetSingleton<ZonePreferenceData>() : default(ZonePreferenceData),
                    m_Result = result,
                };
                var deps = JobHandle.CombineDependencies(JobHandle.CombineDependencies(d1, d2, d3), JobHandle.CombineDependencies(d4, d5, processDeps));
                job.Schedule(deps).Complete();
                return result.ToArray();
            }
            finally
            {
                zones.Dispose();
                kinds.Dispose();
                result.Dispose();
                processes.Dispose();
            }
        }
    }
}
