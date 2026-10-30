using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CityAssistant.Export
{
    /// <summary>
    /// Every building that gives off pollution, with the air, ground and noise pollution the game itself
    /// gives it: the Burst job calls the game's own BuildingPollutionAddSystem.GetBuildingPollution with the
    /// same inputs the game's pollution job uses (game version 1.6.1f1), so nothing is re-implemented.
    /// A game system only because component lookups can only be created by a system; never scheduled.
    /// </summary>
    public partial class PollutionSources : GameSystemBase
    {
        private struct Source
        {
            public float3 Position;
            public float Air, Ground, Noise;
            public Entity Prefab;
        }

        private EntityQuery m_Buildings;
        private EntityQuery m_Parameters;

        protected override void OnCreate()
        {
            base.OnCreate();
            // The same buildings the game's pollution job looks at.
            m_Buildings = GetEntityQuery(
                ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Placeholder>());
            m_Parameters = GetEntityQuery(ComponentType.ReadOnly<PollutionParameterData>());
        }

        protected override void OnUpdate()
        {
        }

        public JsonObject Compute(GameData g)
        {
            var em = EntityManager;
            var sources = new NativeList<Source>(1024, Allocator.TempJob);
            try
            {
                em.CompleteAllTrackedJobs();
                new SourcesJob
                {
                    Entities = GetEntityTypeHandle(),
                    PrefabRefType = GetComponentTypeHandle<PrefabRef>(true),
                    TransformType = GetComponentTypeHandle<Transform>(true),
                    DestroyedType = GetComponentTypeHandle<Destroyed>(true),
                    AbandonedType = GetComponentTypeHandle<Abandoned>(true),
                    ParkType = GetComponentTypeHandle<Game.Buildings.Park>(true),
                    EfficiencyType = GetBufferTypeHandle<Efficiency>(true),
                    RenterType = GetBufferTypeHandle<Renter>(true),
                    InstalledUpgradeType = GetBufferTypeHandle<InstalledUpgrade>(true),
                    Prefabs = GetComponentLookup<PrefabRef>(true),
                    BuildingDatas = GetComponentLookup<BuildingData>(true),
                    SpawnableDatas = GetComponentLookup<SpawnableBuildingData>(true),
                    PollutionDatas = GetComponentLookup<PollutionData>(true),
                    PollutionModifierDatas = GetComponentLookup<PollutionModifierData>(true),
                    ZoneDatas = GetComponentLookup<ZoneData>(true),
                    Employees = GetBufferLookup<Employee>(true),
                    HouseholdCitizens = GetBufferLookup<HouseholdCitizen>(true),
                    Citizens = GetComponentLookup<Citizen>(true),
                    PollutionEmitModifiers = GetComponentLookup<PollutionEmitModifier>(true),
                    CityModifiers = GetBufferLookup<CityModifier>(true),
                    PollutionParameters = m_Parameters.GetSingleton<PollutionParameterData>(),
                    City = g.CityEntity,
                    Sources = sources,
                }.Run(m_Buildings);

                // Building types by name, so the advisor can say "the oil refinery" rather than a number.
                var types = new List<string>();
                var typeIndex = new Dictionary<Entity, int>();
                var typeOfSource = new int[sources.Length];
                var values = new float[sources.Length * 5];
                for (int i = 0; i < sources.Length; i++)
                {
                    var s = sources[i];
                    values[i * 5 + 0] = s.Position.x;
                    values[i * 5 + 1] = s.Position.z;
                    values[i * 5 + 2] = s.Air;
                    values[i * 5 + 3] = s.Ground;
                    values[i * 5 + 4] = s.Noise;
                    if (!typeIndex.TryGetValue(s.Prefab, out var t))
                    {
                        t = types.Count;
                        typeIndex[s.Prefab] = t;
                        types.Add(g.Prefabs.GetPrefabName(s.Prefab));
                    }
                    typeOfSource[i] = t;
                }
                return new JsonObject
                {
                    { "count", sources.Length },
                    { "values", MapExport.Base64(values) },
                    { "layout", "per building 5 float32: x, z, air, ground, noise (pollution given off, game units; source: game code GetBuildingPollution)" },
                    { "types", types },
                    { "typeOfSource", typeOfSource },
                };
            }
            finally
            {
                sources.Dispose();
            }
        }

        [BurstCompile]
        private struct SourcesJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle Entities;
            [ReadOnly] public ComponentTypeHandle<PrefabRef> PrefabRefType;
            [ReadOnly] public ComponentTypeHandle<Transform> TransformType;
            [ReadOnly] public ComponentTypeHandle<Destroyed> DestroyedType;
            [ReadOnly] public ComponentTypeHandle<Abandoned> AbandonedType;
            [ReadOnly] public ComponentTypeHandle<Game.Buildings.Park> ParkType;
            [ReadOnly] public BufferTypeHandle<Efficiency> EfficiencyType;
            [ReadOnly] public BufferTypeHandle<Renter> RenterType;
            [ReadOnly] public BufferTypeHandle<InstalledUpgrade> InstalledUpgradeType;
            [ReadOnly] public ComponentLookup<PrefabRef> Prefabs;
            [ReadOnly] public ComponentLookup<BuildingData> BuildingDatas;
            [ReadOnly] public ComponentLookup<SpawnableBuildingData> SpawnableDatas;
            [ReadOnly] public ComponentLookup<PollutionData> PollutionDatas;
            [ReadOnly] public ComponentLookup<PollutionModifierData> PollutionModifierDatas;
            [ReadOnly] public ComponentLookup<ZoneData> ZoneDatas;
            [ReadOnly] public BufferLookup<Employee> Employees;
            [ReadOnly] public BufferLookup<HouseholdCitizen> HouseholdCitizens;
            [ReadOnly] public ComponentLookup<Citizen> Citizens;
            [ReadOnly] public ComponentLookup<PollutionEmitModifier> PollutionEmitModifiers;
            [ReadOnly] public BufferLookup<CityModifier> CityModifiers;
            public PollutionParameterData PollutionParameters;
            public Entity City;
            public NativeList<Source> Sources;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // Mirrors BuildingPollutionAddSystem's job, minus its update-frame filter (we want every building now).
                var entities = chunk.GetNativeArray(Entities);
                var prefabs = chunk.GetNativeArray(ref PrefabRefType);
                var transforms = chunk.GetNativeArray(ref TransformType);
                var destroyed = chunk.Has(ref DestroyedType);
                var abandoned = chunk.Has(ref AbandonedType);
                var isPark = chunk.Has(ref ParkType);
                var efficiencies = chunk.GetBufferAccessor(ref EfficiencyType);
                var renters = chunk.GetBufferAccessor(ref RenterType);
                var upgrades = chunk.GetBufferAccessor(ref InstalledUpgradeType);
                var cityModifiers = CityModifiers[City];
                for (int i = 0; i < chunk.Count; i++)
                {
                    var efficiency = BuildingUtils.GetEfficiency(efficiencies, i);
                    var buildingRenters = renters.Length != 0 ? renters[i] : default(DynamicBuffer<Renter>);
                    var buildingUpgrades = upgrades.Length != 0 ? upgrades[i] : default(DynamicBuffer<InstalledUpgrade>);
                    var pollution = BuildingPollutionAddSystem.GetBuildingPollution(prefabs[i].m_Prefab, destroyed, abandoned, isPark, efficiency,
                        buildingRenters, buildingUpgrades, PollutionParameters, cityModifiers, ref Prefabs, ref BuildingDatas, ref SpawnableDatas,
                        ref PollutionDatas, ref PollutionModifierDatas, ref ZoneDatas, ref Employees, ref HouseholdCitizens, ref Citizens,
                        ref PollutionEmitModifiers);
                    if (PollutionEmitModifiers.TryGetComponent(entities[i], out var modifier))
                        modifier.UpdatePollutionData(ref pollution);
                    if (pollution.m_AirPollution > 0f || pollution.m_GroundPollution > 0f || pollution.m_NoisePollution > 0f)
                    {
                        Sources.Add(new Source
                        {
                            Position = transforms[i].m_Position,
                            Air = pollution.m_AirPollution,
                            Ground = pollution.m_GroundPollution,
                            Noise = pollution.m_NoisePollution,
                            Prefab = prefabs[i].m_Prefab,
                        });
                    }
                }
            }
        }
    }
}
